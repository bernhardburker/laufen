using System.Diagnostics;
using System.Globalization;
using System.Text;
using Laufen.Domain;

namespace Laufen.Infrastructure;

public sealed record StagingSoakStepResult(
    string StepName,
    bool Succeeded,
    long ElapsedMs,
    string Summary);

public sealed record StagingSoakReport(
    string AthleteSlug,
    string Mode,
    bool Passed,
    IReadOnlyList<StagingSoakStepResult> Steps,
    string OutputDirectory);

/// <summary>
/// Executes a full 9-step end-to-end autopilot lifecycle soak inside an isolated workspace
/// backed by deterministic in-memory mocks for Intervals.icu, Telegram, and LLM providers.
/// </summary>
public static class StagingSoakHarness
{
    public static async Task<StagingSoakReport> RunOfflineEndToEndSoakAsync(
        WorkspaceLayout sourceLayout,
        string slug,
        DateOnly refDate,
        string? preserveOutputDir = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceLayout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var keepOutput = !string.IsNullOrWhiteSpace(preserveOutputDir);
        var tempRoot = keepOutput
            ? Path.GetFullPath(preserveOutputDir!)
            : Path.Combine(Path.GetTempPath(), $"laufen-soak-{Guid.NewGuid():N}");

        var steps = new List<StagingSoakStepResult>(9);
        try
        {
            PrepareIsolatedWorkspace(sourceLayout, tempRoot, slug);
            var layout = WorkspaceLayout.FromRoot(tempRoot);
            var registry = AthleteDataRepository.LoadRegistry(layout);
            var entry = registry.Athletes[slug];
            var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);

            var intervalsFake = new InMemoryIntervalsClient(data.Profile, data.Activities, data.WellnessRecords, data.CalendarEvents);
            var telegramFake = new InMemoryTelegramClient();
            var plannerLlm = new SimulationStubLlmClient();
            var narratorLlm = new NarrativeStubLlmClient();
            var sw = new Stopwatch();

            // 1. sync-data
            sw.Restart();
            var syncSummary = await TelemetrySyncService.SyncAthleteAsync(
                layout,
                entry,
                slug,
                intervalsFake,
                refDate,
                new TelemetrySyncOptions { DryRun = false, WriteToWorkspace = false },
                allowProductionWrite: true,
                cancellationToken).ConfigureAwait(false);
            sw.Stop();
            steps.Add(new StagingSoakStepResult(
                "sync-data",
                File.Exists(Path.Combine(syncSummary.TargetDirectory, "intervals_activities.json")),
                sw.ElapsedMilliseconds,
                string.Create(CultureInfo.InvariantCulture, $"Synced {syncSummary.ActivitiesCount} activities to isolated .dotnet_sync/.")));

            // 2. review
            sw.Restart();
            var reviewsDict = new Dictionary<string, RunReviewRecord>(StringComparer.Ordinal);
            var validRuns = PhysiologicalCalculator.FilterValidRuns(data.Activities)
                .Where(a => DateOnly.FromDateTime(a.StartDateLocal) <= refDate)
                .OrderByDescending(a => a.StartDateLocal)
                .Take(3)
                .ToList();
            foreach (var run in validRuns)
            {
                var stream = StrictJsonLoader.LoadActivityStream(layout, slug, run.Id);
                reviewsDict[run.Id] = PostRunReviewCalculator.EvaluateRun(run, data.Profile, stream);
            }
            AutopilotStateStore.SaveDotNetRunReviews(layout, slug, reviewsDict);
            sw.Stop();
            steps.Add(new StagingSoakStepResult(
                "review",
                reviewsDict.Count == validRuns.Count,
                sw.ElapsedMilliseconds,
                string.Create(CultureInfo.InvariantCulture, $"Computed {reviewsDict.Count} 4-stage reviews into run_reviews_dotnet.json.")));

            // 3. autopilot-run
            sw.Restart();
            var orchestrator = new AutopilotCycleOrchestrator(
                new RollingAutopilotStrategy(plannerLlm),
                new LlmProgressNarrator(narratorLlm));
            var cycle = await orchestrator.RunCycleAsync(new AutopilotCycleRequest
            {
                AthleteSlug = slug,
                RegistryEntry = entry,
                Profile = data.Profile,
                Activities = data.Activities,
                WellnessRecords = data.WellnessRecords,
                RunReviews = reviewsDict,
                CalendarEvents = data.CalendarEvents,
                ReferenceDate = refDate,
                TargetDate = refDate,
                Trigger = "morning_sync",
                ForceReplan = true,
                ForceNarrate = true,
                IsDryRun = false
            }, cancellationToken).ConfigureAwait(false);

            if (cycle.PlanningOutcome?.Decision is not null)
            {
                AutopilotStateStore.SaveBlockCompass(layout, slug, cycle.PlanningOutcome.Decision.UpdatedCompass);
                AutopilotStateStore.AppendDecisionTrace(layout, slug, cycle.PlanningOutcome.TraceEntry);
            }
            if (cycle.NarrativeOutcome?.Report is not null)
            {
                AutopilotStateStore.SaveNarrativeArtifacts(layout, slug, cycle.NarrativeOutcome.Report);
            }
            sw.Stop();
            steps.Add(new StagingSoakStepResult(
                "autopilot-run",
                cycle.Succeeded && cycle.CalendarSyncPlan is not null && cycle.NarrativeOutcome?.Success == true,
                sw.ElapsedMilliseconds,
                $"Health={cycle.UpdatedCompass?.HealthState ?? "-"}, Watch={cycle.PlanningOutcome?.ReconciledSchedule?.WatchCommitWindow.Count ?? 0}."));

            // 4. sync-calendar (apply + 2nd idempotency pass verifying zero churn)
            sw.Restart();
            var livePlan = cycle.CalendarSyncPlan! with { IsDryRun = false };
            var syncExec = await IntervalsCalendarSyncExecutor.ExecuteAsync(
                livePlan,
                entry,
                intervalsFake,
                allowProductionWrite: true,
                cancellationToken).ConfigureAwait(false);

            var secondPassPlan = IntervalsCalendarSyncPlanner.BuildPlan(
                slug,
                entry.IntervalsId,
                cycle.PlanningOutcome!.ReconciledSchedule!,
                intervalsFake.EventsSnapshot,
                syncExec.ResultingAutoEventIds.ToHashSet(),
                new HashSet<DateOnly>(),
                isDryRun: true);
            sw.Stop();
            steps.Add(new StagingSoakStepResult(
                "sync-calendar",
                syncExec.Applied && secondPassPlan.CreateCount == 0 && secondPassPlan.DeleteCount == 0,
                sw.ElapsedMilliseconds,
                string.Create(CultureInfo.InvariantCulture, $"Created={syncExec.CreatedEventIds.Count}, Deleted={syncExec.DeletedEventIds.Count}, SecondPassChurn={secondPassPlan.CreateCount + secondPassPlan.DeleteCount}.")));

            // 5. telegram-briefing (send + idempotency check)
            sw.Restart();
            var sentMsgId = await telegramFake.SendMessageAsync(cycle.TelegramPreview!, cancellationToken).ConfigureAwait(false);
            var tgState = TelegramInteractionRouter.MarkMorningBriefingSent(data.TelegramState, refDate);
            AutopilotStateStore.SaveDotNetTelegramState(layout, slug, tgState);
            var resendBlocked = !TelegramInteractionRouter.ShouldSendMorningBriefing(tgState, refDate);
            sw.Stop();
            steps.Add(new StagingSoakStepResult(
                "telegram-briefing",
                sentMsgId.HasValue && resendBlocked,
                sw.ElapsedMilliseconds,
                $"Sent briefing msgId={sentMsgId}, idempotentResendBlocked={resendBlocked}."));

            // 6. telegram-override (accept lock without LLM + easier override via LLM)
            sw.Restart();
            var acceptRoute = TelegramInteractionRouter.RouteCallback($"accept:{refDate.AddDays(1):yyyy-MM-dd}", refDate);
            tgState = TelegramInteractionRouter.ApplyStateLock(tgState, acceptRoute.TargetDate!.Value);
            AutopilotStateStore.SaveDotNetTelegramState(layout, slug, tgState);

            var easierRoute = TelegramInteractionRouter.RouteCallback($"easier:{refDate:yyyy-MM-dd}", refDate);
            var overrideDossier = CoachIntelligenceDossierBuilder.Build(
                data.Profile,
                data.Activities,
                data.WellnessRecords,
                reviewsDict,
                intervalsFake.EventsSnapshot,
                refDate,
                refDate,
                syncExec.ResultingAutoEventIds.ToHashSet(),
                tgState.UserLockedDates?.ToHashSet(),
                AutopilotStateStore.LoadBlockCompass(layout, slug));
            var overrideOutcome = await new RollingAutopilotStrategy(plannerLlm).PlanAsync(new CoachPlanningContext
            {
                Dossier = overrideDossier,
                Trigger = easierRoute.Trigger,
                AthleteMessage = easierRoute.AthleteMessage
            }, cancellationToken).ConfigureAwait(false);
            if (overrideOutcome.Success && overrideOutcome.Decision is not null)
            {
                AutopilotStateStore.SaveBlockCompass(layout, slug, overrideOutcome.Decision.UpdatedCompass);
                AutopilotStateStore.AppendDecisionTrace(layout, slug, overrideOutcome.TraceEntry);
            }
            sw.Stop();
            steps.Add(new StagingSoakStepResult(
                "telegram-override",
                !acceptRoute.RequiresLlmReplan && overrideOutcome.Success,
                sw.ElapsedMilliseconds,
                $"AcceptLocked={acceptRoute.TargetDate:yyyy-MM-dd}, OverrideSucceeded={overrideOutcome.Success}."));

            // 7. dashboard
            sw.Restart();
            var dashSnapshot = DashboardSnapshotBuilder.Build(
                slug,
                entry.DashboardSlug,
                data.Profile,
                data.Activities,
                data.WellnessRecords,
                reviewsDict,
                intervalsFake.EventsSnapshot,
                refDate,
                currentCompass: AutopilotStateStore.LoadBlockCompass(layout, slug),
                latestNarrative: AutopilotStateStore.LoadNarrativeSummary(layout, slug),
                recentTraces: AutopilotStateStore.LoadDecisionTraces(layout, slug, limit: 10),
                userLockedDates: tgState.UserLockedDates?.ToHashSet());
            var html = DashboardHtmlRenderer.Render(dashSnapshot);
            var htmlPath = Path.Combine(layout.RootDirectory, "dist-dotnet", entry.DashboardSlug, "index.html");
            Directory.CreateDirectory(Path.GetDirectoryName(htmlPath)!);
            File.WriteAllText(htmlPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            sw.Stop();
            steps.Add(new StagingSoakStepResult(
                "dashboard",
                File.Exists(htmlPath) && html.Contains("14-Tage-Etappenziele", StringComparison.Ordinal),
                sw.ElapsedMilliseconds,
                string.Create(CultureInfo.InvariantCulture, $"Rendered static HTML5 dashboard ({html.Length} chars).")));

            // 8. daemon-once (callback + new run within 6h)
            sw.Restart();
            var tickClockNow = new DateTimeOffset(refDate.Year, refDate.Month, refDate.Day, 15, 0, 0, TimeSpan.Zero);
            tgState = tgState with { LastActivitiesPollTs = tickClockNow.AddMinutes(-20).ToUnixTimeSeconds() };
            AutopilotStateStore.SaveDotNetTelegramState(layout, slug, tgState);

            var soakRunId = $"soak-run-{refDate:yyyyMMdd}";
            intervalsFake.AddRun(new IntervalsActivityEntry
            {
                Id = soakRunId,
                IcuAthleteId = entry.IntervalsId,
                Source = "GARMIN_CONNECT",
                StartDateLocal = new DateTime(refDate.Year, refDate.Month, refDate.Day, 13, 30, 0, DateTimeKind.Unspecified),
                Type = "Run",
                Name = "Soak Verification Run",
                Distance = 6000.0,
                MovingTime = 2400,
                AverageHeartrate = 148,
                MaxHeartrate = 158,
                HasHeartrate = true,
                IcuTrainingLoad = 42,
                IcuHrZoneTimes = [600, 1600, 200, 0, 0]
            });

            telegramFake.EnqueueUpdate(new TelegramIncomingUpdate
            {
                UpdateId = 9001,
                CallbackQuery = new TelegramCallbackQuery
                {
                    Id = "soak-cb-1",
                    ChatId = entry.TelegramChatId!,
                    Data = $"accept:{refDate.AddDays(2):yyyy-MM-dd}"
                }
            });

            var daemonRunner = new AutopilotDaemonRunner(new FixedDaemonClock(tickClockNow));
            var tickSummary = await daemonRunner.RunSingleTickAsync(
                layout,
                new DaemonRuntimeConfig
                {
                    DryRun = false,
                    ApplyCalendar = true,
                    SendTelegram = true,
                    AllowProductionWrite = true,
                    RebuildDashboardOnChange = true
                },
                telegramFake,
                intervalsFake,
                plannerLlm,
                narratorLlm,
                cancellationToken).ConfigureAwait(false);
            sw.Stop();
            steps.Add(new StagingSoakStepResult(
                "daemon-once",
                tickSummary.Errors.Count == 0 && tickSummary.CallbacksAnswered == 1 && tickSummary.PostRunReviewsTriggered == 1,
                sw.ElapsedMilliseconds,
                string.Create(CultureInfo.InvariantCulture, $"Callbacks={tickSummary.CallbacksAnswered}, PostRuns={tickSummary.PostRunReviewsTriggered}, Errors={tickSummary.Errors.Count}.")));

            // 9. trace
            sw.Restart();
            var traces = AutopilotStateStore.LoadDecisionTraces(layout, slug, limit: 10);
            var traceMd = DecisionTracePresenter.FormatMarkdown(slug, traces, overrideDossier);
            sw.Stop();
            steps.Add(new StagingSoakStepResult(
                "trace",
                traces.Count >= 2 && traceMd.Contains("## Decision Trace:", StringComparison.Ordinal),
                sw.ElapsedMilliseconds,
                string.Create(CultureInfo.InvariantCulture, $"Loaded {traces.Count} trace entries and formatted Markdown summary.")));

            var allPassed = steps.All(s => s.Succeeded);
            return new StagingSoakReport(slug, "offline_mocked", allPassed, steps, tempRoot);
        }
        finally
        {
            if (!keepOutput && Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void PrepareIsolatedWorkspace(WorkspaceLayout source, string destRoot, string slug)
    {
        var athleteDir = Path.Combine(destRoot, "athletes", slug);
        Directory.CreateDirectory(athleteDir);

        var sourceRegistry = StrictJsonLoader.LoadRegistry(source);
        if (!sourceRegistry.Athletes.TryGetValue(slug, out var sourceEntry))
        {
            throw new KeyNotFoundException($"Unknown athlete slug '{slug}'.");
        }

        var isolatedEntry = sourceEntry with
        {
            Active = true,
            BriefingEnabled = true,
            TelegramChatId = string.IsNullOrWhiteSpace(sourceEntry.TelegramChatId) ? "157754568" : sourceEntry.TelegramChatId
        };

        var isolatedRegistry = new AthleteRegistry
        {
            DefaultAthlete = slug,
            Athletes = new Dictionary<string, AthleteRegistryEntry>(StringComparer.Ordinal)
            {
                [slug] = isolatedEntry
            }
        };

        File.WriteAllText(
            Path.Combine(destRoot, "athletes", "registry.json"),
            System.Text.Json.JsonSerializer.Serialize(isolatedRegistry) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var srcAthleteDir = Path.Combine(source.AthletesDirectory, slug);
        foreach (var file in Directory.GetFiles(srcAthleteDir))
        {
            File.Copy(file, Path.Combine(athleteDir, Path.GetFileName(file)), overwrite: true);
        }

        var srcStreamsDir = Path.Combine(srcAthleteDir, "streams");
        if (Directory.Exists(srcStreamsDir))
        {
            var destStreamsDir = Path.Combine(athleteDir, "streams");
            Directory.CreateDirectory(destStreamsDir);
            foreach (var sFile in Directory.GetFiles(srcStreamsDir))
            {
                File.Copy(sFile, Path.Combine(destStreamsDir, Path.GetFileName(sFile)), overwrite: true);
            }
        }
    }

    private sealed class InMemoryIntervalsClient(
        AthleteProfile profile,
        IReadOnlyList<IntervalsActivityEntry> activities,
        IReadOnlyList<IntervalsWellnessEntry> wellness,
        IReadOnlyList<CalendarEventEntry> events) : IIntervalsIcuClient
    {
        private readonly List<IntervalsActivityEntry> _activities = [.. activities];
        private readonly List<CalendarEventEntry> _events = [.. events];
        private long _nextId = 90000;

        public IReadOnlyList<CalendarEventEntry> EventsSnapshot => _events;

        public void AddRun(IntervalsActivityEntry run) => _activities.Insert(0, run);

        public Task<IReadOnlyList<IntervalsActivityEntry>> GetActivitiesAsync(
            string athleteId, DateOnly oldest, DateOnly newest, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IntervalsActivityEntry>>(_activities.ToList());

        public Task<ActivityStreamData?> GetActivityStreamAsync(
            string activityId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityStreamData?>(new ActivityStreamData
            {
                Time = [0, 1, 2],
                Heartrate = [142, 143, 144],
                VelocitySmooth = [2.2, 2.2, 2.2]
            });

        public Task<IReadOnlyList<IntervalsWellnessEntry>> GetWellnessAsync(
            string athleteId, DateOnly oldest, DateOnly newest, CancellationToken cancellationToken = default) =>
            Task.FromResult(wellness);

        public Task<IReadOnlyList<CalendarEventEntry>> GetCalendarEventsAsync(
            string athleteId, DateOnly oldest, DateOnly newest, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEventEntry>>(_events.ToList());

        public Task<CalendarEventEntry> CreateCalendarEventAsync(
            string athleteId, IntervalsCalendarEventUpsertRequest request, CancellationToken cancellationToken = default)
        {
            var created = new CalendarEventEntry
            {
                Id = ++_nextId,
                AthleteId = athleteId,
                Category = request.Category,
                StartDateLocal = DateTime.Parse(request.StartDateLocal, CultureInfo.InvariantCulture),
                Type = request.Type,
                Name = request.Name,
                Description = request.Description,
                ExternalId = request.ExternalId
            };
            _events.Add(created);
            return Task.FromResult(created);
        }

        public Task<bool> DeleteCalendarEventAsync(
            string athleteId, long eventId, CancellationToken cancellationToken = default)
        {
            _events.RemoveAll(e => e.Id == eventId);
            return Task.FromResult(true);
        }

        public Task<IntervalsAthleteProfileDto> GetAthleteProfileAsync(
            string athleteId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntervalsAthleteProfileDto
            {
                Id = athleteId,
                Name = profile.Name,
                IcuRestingHr = profile.HeartRate.RestingHr,
                Lthr = profile.HeartRate.Lthr,
                MaxHr = profile.HeartRate.MaxHr,
                SportSettings =
                [
                    new IntervalsSportSettingDto
                    {
                        Types = ["Run"],
                        Lthr = profile.HeartRate.Lthr,
                        MaxHr = profile.HeartRate.MaxHr,
                        HrZones = profile.HeartRate.RawHrZones,
                        HrZoneNames = profile.HeartRate.RawZoneNames
                    }
                ]
            });

        public Task<IntervalsActivityEntry> UpdateActivityNoteAsync(
            string athleteId, string activityId, ActivityNoteUpdateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntervalsActivityEntry
            {
                Id = activityId,
                IcuAthleteId = athleteId,
                Source = "GARMIN_CONNECT",
                StartDateLocal = DateTime.UtcNow,
                Description = request.Description
            });
    }

    private sealed class InMemoryTelegramClient : ITelegramBotClient
    {
        private readonly Queue<TelegramIncomingUpdate> _updates = new();
        private long _messageSeq;

        public void EnqueueUpdate(TelegramIncomingUpdate update) => _updates.Enqueue(update);

        public Task<long?> SendMessageAsync(TelegramOutgoingMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(++_messageSeq);

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string text, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<TelegramIncomingUpdate>> GetUpdatesAsync(
            long? offset = null, int timeoutSeconds = 0, CancellationToken cancellationToken = default)
        {
            var list = new List<TelegramIncomingUpdate>();
            while (_updates.Count > 0)
            {
                var u = _updates.Dequeue();
                if (!offset.HasValue || u.UpdateId >= offset.Value)
                {
                    list.Add(u);
                }
            }
            return Task.FromResult<IReadOnlyList<TelegramIncomingUpdate>>(list);
        }
    }
}
