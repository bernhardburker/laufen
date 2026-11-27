using System.Text;
using System.Text.Json;
using Laufen.Domain;

namespace Laufen.Infrastructure;

public interface IDaemonClock
{
    DateTimeOffset NowLocal { get; }
}

public sealed class SystemDaemonClock : IDaemonClock
{
    public static SystemDaemonClock Instance { get; } = new();
    public DateTimeOffset NowLocal => DateTimeOffset.Now;
}

public sealed class FixedDaemonClock(DateTimeOffset fixedNowLocal) : IDaemonClock
{
    public DateTimeOffset NowLocal => fixedNowLocal;
}

/// <summary>
/// Fault-isolated background worker orchestrating Telegram long-polling callbacks/overrides,
/// scheduled morning/evening briefings, 10-minute post-run activity polling, and shadow HTML rebuilds.
/// </summary>
public sealed class AutopilotDaemonRunner(IDaemonClock? clock = null)
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IDaemonClock _clock = clock ?? SystemDaemonClock.Instance;

    public async Task<DaemonTickSummary> RunSingleTickAsync(
        WorkspaceLayout layout,
        DaemonRuntimeConfig config,
        ITelegramBotClient? telegramClient,
        IIntervalsIcuClient? intervalsClient,
        ILlmClient plannerLlm,
        ILlmClient narratorLlm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(plannerLlm);
        ArgumentNullException.ThrowIfNull(narratorLlm);

        var nowLocal = _clock.NowLocal;
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var states = LoadEffectiveTelegramStates(layout, registry);

        var updatesProcessed = 0;
        var callbacksAnswered = 0;
        var overridesExecuted = 0;
        var morningTriggered = 0;
        var eveningTriggered = 0;
        var postRunTriggered = 0;
        var dashboardsRebuilt = 0;
        var errors = new List<string>();

        var plannerStrategy = new RollingAutopilotStrategy(plannerLlm);
        var narrator = new LlmProgressNarrator(narratorLlm);
        var orchestrator = new AutopilotCycleOrchestrator(plannerStrategy, narrator);

        if (telegramClient is not null)
        {
            var maxUpdateId = states.Values
                .Where(s => s?.LastUpdateId is not null)
                .Select(s => s!.LastUpdateId!.Value)
                .DefaultIfEmpty(0L)
                .Max();
            long? offset = maxUpdateId > 0 ? maxUpdateId + 1 : null;

            IReadOnlyList<TelegramIncomingUpdate> updates = [];
            try
            {
                updates = await telegramClient.GetUpdatesAsync(
                    offset,
                    config.DryRun ? 0 : config.TelegramLongPollTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"Telegram getUpdates failed: {ex.Message}");
            }

            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                updatesProcessed++;
                try
                {
                    var (answered, ranOverride, rebuilt) = await ProcessTelegramUpdateAsync(
                        layout,
                        registry,
                        states,
                        update,
                        today,
                        config,
                        telegramClient,
                        intervalsClient,
                        orchestrator,
                        cancellationToken).ConfigureAwait(false);

                    if (answered)
                    {
                        callbacksAnswered++;
                    }
                    if (ranOverride)
                    {
                        overridesExecuted++;
                    }
                    if (rebuilt)
                    {
                        dashboardsRebuilt++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"Update {update.UpdateId} failed: {ex.Message}");
                }
            }
        }

        var dueActions = DaemonScheduleEvaluator.EvaluateDueActions(
            registry,
            states,
            nowLocal,
            config.ActivityPollIntervalMinutes);

        foreach (var due in dueActions.Where(d => d.ActionKind is DaemonScheduledActionKind.MorningBriefing or DaemonScheduledActionKind.EveningPreview))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var targetDate = due.ActionKind == DaemonScheduledActionKind.EveningPreview
                    ? due.ReferenceDate.AddDays(1)
                    : due.ReferenceDate;
                var entry = registry.Athletes[due.AthleteSlug];
                var data = AthleteDataRepository.LoadAthleteData(layout, registry, due.AthleteSlug);
                var compass = AutopilotStateStore.LoadBlockCompass(layout, due.AthleteSlug);
                var latestTrace = AutopilotStateStore.LoadLatestDecisionTrace(layout, due.AthleteSlug);
                var dotnetReviews = AutopilotStateStore.LoadDotNetRunReviews(layout, due.AthleteSlug);
                var effectiveReviews = dotnetReviews.Count > 0 ? dotnetReviews : data.RunReviews;
                states.TryGetValue(due.AthleteSlug, out var tgState);
                var mergedLocks = AutopilotStateStore.MergeUserLockedDates(data.TelegramState, tgState);

                var cycleResult = await orchestrator.RunCycleAsync(new AutopilotCycleRequest
                {
                    AthleteSlug = due.AthleteSlug,
                    RegistryEntry = entry,
                    Profile = data.Profile,
                    Activities = data.Activities,
                    WellnessRecords = data.WellnessRecords,
                    RunReviews = effectiveReviews,
                    CalendarEvents = data.CalendarEvents,
                    ReferenceDate = due.ReferenceDate,
                    TargetDate = targetDate,
                    LockedDates = mergedLocks,
                    CurrentCompass = compass,
                    PreviousHealthState = latestTrace?.HealthState,
                    PreviousBlockFocus = compass?.CurrentBlockFocus,
                    Trigger = "morning_sync",
                    MaxValidationRetries = config.MaxValidationRetries,
                    IsDryRun = config.DryRun
                }, cancellationToken).ConfigureAwait(false);

                if (!cycleResult.Succeeded)
                {
                    errors.Add($"Scheduled {due.ActionKind} failed for '{due.AthleteSlug}': {cycleResult.FailureReason}");
                    continue;
                }

                if (due.ActionKind == DaemonScheduledActionKind.MorningBriefing)
                {
                    morningTriggered++;
                }
                else
                {
                    eveningTriggered++;
                }

                if (!config.DryRun)
                {
                    var rebuilt = await PersistAndDispatchCycleSideEffectsAsync(
                        layout,
                        due.AthleteSlug,
                        entry,
                        data,
                        cycleResult,
                        due.ReferenceDate,
                        targetDate,
                        due.ActionKind == DaemonScheduledActionKind.EveningPreview,
                        config,
                        telegramClient,
                        intervalsClient,
                        states,
                        cancellationToken).ConfigureAwait(false);

                    if (rebuilt)
                    {
                        dashboardsRebuilt++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"Scheduled {due.ActionKind} for '{due.AthleteSlug}' threw: {ex.Message}");
            }
        }

        if (intervalsClient is not null)
        {
            foreach (var due in dueActions.Where(d => d.ActionKind == DaemonScheduledActionKind.ActivityPoll))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (triggered, rebuilt) = await PollActivitiesForAthleteAsync(
                        layout,
                        registry,
                        due.AthleteSlug,
                        due.ReferenceDate,
                        nowLocal,
                        states,
                        config,
                        telegramClient,
                        intervalsClient,
                        orchestrator,
                        cancellationToken).ConfigureAwait(false);

                    postRunTriggered += triggered;
                    if (rebuilt)
                    {
                        dashboardsRebuilt++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"Activity poll for '{due.AthleteSlug}' failed: {ex.Message}");
                }
            }
        }

        return new DaemonTickSummary
        {
            Timestamp = nowLocal,
            UpdatesProcessed = updatesProcessed,
            CallbacksAnswered = callbacksAnswered,
            OverridesExecuted = overridesExecuted,
            MorningBriefingsTriggered = morningTriggered,
            EveningPreviewsTriggered = eveningTriggered,
            PostRunReviewsTriggered = postRunTriggered,
            DashboardsRebuilt = dashboardsRebuilt,
            Errors = errors,
            DryRun = config.DryRun
        };
    }

    public async Task RunLoopAsync(
        WorkspaceLayout layout,
        DaemonRuntimeConfig config,
        ITelegramBotClient? telegramClient,
        IIntervalsIcuClient? intervalsClient,
        ILlmClient plannerLlm,
        ILlmClient narratorLlm,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, config.PollIntervalSeconds));
        while (!cancellationToken.IsCancellationRequested)
        {
            _ = await RunSingleTickAsync(
                layout,
                config,
                telegramClient,
                intervalsClient,
                plannerLlm,
                narratorLlm,
                cancellationToken).ConfigureAwait(false);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(bool Answered, bool RanOverride, bool Rebuilt)> ProcessTelegramUpdateAsync(
        WorkspaceLayout layout,
        AthleteRegistry registry,
        Dictionary<string, TelegramStateRecord?> states,
        TelegramIncomingUpdate update,
        DateOnly today,
        DaemonRuntimeConfig config,
        ITelegramBotClient telegramClient,
        IIntervalsIcuClient? intervalsClient,
        AutopilotCycleOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var chatId = update.CallbackQuery?.ChatId ?? update.Message?.ChatId;
        var slug = DaemonScheduleEvaluator.ResolveAthleteByChatId(registry, chatId);
        if (slug is null)
        {
            return (false, false, false);
        }

        var entry = registry.Athletes[slug];
        if (!TelegramInteractionRouter.IsUserAuthorized(entry, chatId))
        {
            return (false, false, false);
        }

        TelegramInteractionRoute route;
        var answered = false;

        if (update.CallbackQuery is { } cb)
        {
            route = TelegramInteractionRouter.RouteCallback(cb.Data, today);
            if (!config.DryRun)
            {
                await telegramClient.AnswerCallbackQueryAsync(cb.Id, route.AcknowledgementText, cancellationToken).ConfigureAwait(false);
            }
            answered = true;
        }
        else if (update.Message is { } msg && !string.IsNullOrWhiteSpace(msg.Text))
        {
            route = TelegramInteractionRouter.RouteFreeText(msg.Text, today);
        }
        else
        {
            return (false, false, false);
        }

        states.TryGetValue(slug, out var currentState);
        var nextState = TelegramInteractionRouter.AdvanceUpdateOffset(currentState, update.UpdateId);

        if (route.LocksDate && route.TargetDate.HasValue)
        {
            nextState = TelegramInteractionRouter.ApplyStateLock(nextState, route.TargetDate.Value);
        }

        if (!route.RequiresLlmReplan)
        {
            states[slug] = nextState;
            if (!config.DryRun)
            {
                AutopilotStateStore.SaveDotNetTelegramState(layout, slug, nextState);
            }

            return (answered, false, false);
        }

        var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
        var compass = AutopilotStateStore.LoadBlockCompass(layout, slug);
        var latestTrace = AutopilotStateStore.LoadLatestDecisionTrace(layout, slug);
        var dotnetReviews = AutopilotStateStore.LoadDotNetRunReviews(layout, slug);
        var effectiveReviews = dotnetReviews.Count > 0 ? dotnetReviews : data.RunReviews;
        var mergedLocks = AutopilotStateStore.MergeUserLockedDates(data.TelegramState, nextState);

        var cycleResult = await orchestrator.RunCycleAsync(new AutopilotCycleRequest
        {
            AthleteSlug = slug,
            RegistryEntry = entry,
            Profile = data.Profile,
            Activities = data.Activities,
            WellnessRecords = data.WellnessRecords,
            RunReviews = effectiveReviews,
            CalendarEvents = data.CalendarEvents,
            ReferenceDate = today,
            TargetDate = route.TargetDate ?? today,
            LockedDates = mergedLocks,
            CurrentCompass = compass,
            PreviousHealthState = latestTrace?.HealthState,
            PreviousBlockFocus = compass?.CurrentBlockFocus,
            Trigger = route.Trigger,
            AthleteMessage = route.AthleteMessage,
            MaxValidationRetries = config.MaxValidationRetries,
            IsDryRun = config.DryRun
        }, cancellationToken).ConfigureAwait(false);

        states[slug] = nextState;
        if (!config.DryRun)
        {
            AutopilotStateStore.SaveDotNetTelegramState(layout, slug, nextState);
        }

        if (!cycleResult.Succeeded)
        {
            throw new InvalidOperationException(cycleResult.FailureReason ?? "Telegram override planning failed.");
        }

        var rebuilt = false;
        if (!config.DryRun)
        {
            if (cycleResult.PlanningOutcome?.Decision is not null)
            {
                AutopilotStateStore.SaveBlockCompass(layout, slug, cycleResult.PlanningOutcome.Decision.UpdatedCompass);
                AutopilotStateStore.AppendDecisionTrace(layout, slug, cycleResult.PlanningOutcome.TraceEntry);
            }

            if (config.ApplyCalendar && intervalsClient is not null && cycleResult.CalendarSyncPlan is not null)
            {
                var syncExec = await IntervalsCalendarSyncExecutor.ExecuteAsync(
                    cycleResult.CalendarSyncPlan,
                    entry,
                    intervalsClient,
                    config.AllowProductionWrite,
                    cancellationToken).ConfigureAwait(false);

                if (syncExec.Applied && cycleResult.UpdatedCompass is not null)
                {
                    AutopilotStateStore.SaveBlockCompass(layout, slug, cycleResult.UpdatedCompass with
                    {
                        UpdatedAt = today,
                        AutoEventIds = syncExec.ResultingAutoEventIds
                    });
                }
            }

            if (config.SendTelegram && !string.IsNullOrWhiteSpace(entry.TelegramChatId))
            {
                var permission = StagingSafetyGuard.EvaluateWritePermission(slug, entry, config.AllowProductionWrite);
                if (permission.Allowed)
                {
                    var replyMessage = cycleResult.TelegramPreview ?? new TelegramOutgoingMessage
                    {
                        ChatId = entry.TelegramChatId,
                        Text = route.AcknowledgementText,
                        MessageKind = "telegram_override_reply",
                        TargetDate = route.TargetDate
                    };
                    await telegramClient.SendMessageAsync(replyMessage, cancellationToken).ConfigureAwait(false);
                }
            }

            if (config.RebuildDashboardOnChange)
            {
                RebuildShadowDashboard(layout, registry, slug, data, today, nextState);
                rebuilt = true;
            }
        }

        return (answered, true, rebuilt);
    }

    private static async Task<bool> PersistAndDispatchCycleSideEffectsAsync(
        WorkspaceLayout layout,
        string slug,
        AthleteRegistryEntry entry,
        AthleteWorkspaceData data,
        AutopilotCycleResult cycleResult,
        DateOnly refDate,
        DateOnly targetDate,
        bool isEveningPreview,
        DaemonRuntimeConfig config,
        ITelegramBotClient? telegramClient,
        IIntervalsIcuClient? intervalsClient,
        Dictionary<string, TelegramStateRecord?> states,
        CancellationToken cancellationToken)
    {
        if (cycleResult.PlanningOutcome?.Decision is not null)
        {
            AutopilotStateStore.SaveBlockCompass(layout, slug, cycleResult.PlanningOutcome.Decision.UpdatedCompass);
            AutopilotStateStore.AppendDecisionTrace(layout, slug, cycleResult.PlanningOutcome.TraceEntry);
        }

        if (cycleResult.NarrativeOutcome?.Report is not null)
        {
            AutopilotStateStore.SaveNarrativeArtifacts(layout, slug, cycleResult.NarrativeOutcome.Report);
        }

        if (config.ApplyCalendar && intervalsClient is not null && cycleResult.CalendarSyncPlan is not null)
        {
            var syncExec = await IntervalsCalendarSyncExecutor.ExecuteAsync(
                cycleResult.CalendarSyncPlan,
                entry,
                intervalsClient,
                config.AllowProductionWrite,
                cancellationToken).ConfigureAwait(false);

            if (syncExec.Applied && cycleResult.UpdatedCompass is not null)
            {
                AutopilotStateStore.SaveBlockCompass(layout, slug, cycleResult.UpdatedCompass with
                {
                    UpdatedAt = refDate,
                    AutoEventIds = syncExec.ResultingAutoEventIds
                });
            }
        }

        states.TryGetValue(slug, out var currentState);
        if (config.SendTelegram && telegramClient is not null && cycleResult.TelegramPreview is not null)
        {
            var permission = StagingSafetyGuard.EvaluateWritePermission(slug, entry, config.AllowProductionWrite);
            if (permission.Allowed)
            {
                var outgoing = cycleResult.TelegramPreview;
                if (isEveningPreview)
                {
                    outgoing = outgoing with { MessageKind = "evening_preview", TargetDate = targetDate };
                }

                await telegramClient.SendMessageAsync(outgoing, cancellationToken).ConfigureAwait(false);
                var updatedState = isEveningPreview
                    ? TelegramInteractionRouter.MarkEveningPreviewSent(currentState, refDate)
                    : TelegramInteractionRouter.MarkMorningBriefingSent(currentState, refDate);
                states[slug] = updatedState;
                AutopilotStateStore.SaveDotNetTelegramState(layout, slug, updatedState);
            }
        }

        if (config.RebuildDashboardOnChange)
        {
            RebuildShadowDashboard(layout, AthleteDataRepository.LoadRegistry(layout), slug, data, refDate, states.GetValueOrDefault(slug));
            return true;
        }

        return false;
    }

    private static async Task<(int Triggered, bool Rebuilt)> PollActivitiesForAthleteAsync(
        WorkspaceLayout layout,
        AthleteRegistry registry,
        string slug,
        DateOnly refDate,
        DateTimeOffset nowLocal,
        Dictionary<string, TelegramStateRecord?> states,
        DaemonRuntimeConfig config,
        ITelegramBotClient? telegramClient,
        IIntervalsIcuClient intervalsClient,
        AutopilotCycleOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var entry = registry.Athletes[slug];
        var freshActivities = await intervalsClient.GetActivitiesAsync(
            entry.IntervalsId,
            refDate.AddDays(-2),
            refDate.AddDays(1),
            cancellationToken).ConfigureAwait(false);

        states.TryGetValue(slug, out var state);
        var newRuns = DaemonScheduleEvaluator.DetectNewCompletedRuns(
            freshActivities,
            state,
            nowLocal,
            config.PostRunLookbackHours);

        var nextState = TelegramInteractionRouter.MarkActivitiesPolled(state, nowLocal);

        if (newRuns.Count == 0)
        {
            states[slug] = nextState;
            if (!config.DryRun)
            {
                AutopilotStateStore.SaveDotNetTelegramState(layout, slug, nextState);
            }
            return (0, false);
        }

        var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
        var mergedActivities = MergeActivities(data.Activities, freshActivities);
        var dotnetReviews = AutopilotStateStore.LoadDotNetRunReviews(layout, slug);
        var updatedReviews = new Dictionary<string, RunReviewRecord>(
            dotnetReviews.Count > 0 ? dotnetReviews : data.RunReviews,
            StringComparer.Ordinal);
        var calendarById = data.CalendarEvents.ToDictionary(e => e.Id);
        var openCycle = data.CoachHistory
            .Where(c => string.Equals(c.Status, "offen", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Date)
            .FirstOrDefault();

        foreach (var run in newRuns)
        {
            ActivityStreamData? stream = StrictJsonLoader.LoadActivityStream(layout, slug, run.Id);
            if (stream is null)
            {
                try
                {
                    stream = await intervalsClient.GetActivityStreamAsync(run.Id, cancellationToken).ConfigureAwait(false);
                    if (stream is not null && !config.DryRun)
                    {
                        var streamPath = Path.Combine(layout.AthletesDirectory, slug, "streams", $"{run.Id}.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(streamPath)!);
                        File.WriteAllText(streamPath, JsonSerializer.Serialize(stream, CompactJsonOptions) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
                {
                    stream = null;
                }
            }

            CalendarEventEntry? pairedEvt = null;
            if (run.PairedEventId.HasValue)
            {
                calendarById.TryGetValue(run.PairedEventId.Value, out pairedEvt);
            }

            var review = PostRunReviewCalculator.EvaluateRun(run, data.Profile, stream, pairedEvt, openCycle);
            updatedReviews[run.Id] = review;
            nextState = TelegramInteractionRouter.MarkActivityNotified(nextState, run.Id);
        }

        if (!config.DryRun)
        {
            AutopilotStateStore.SaveDotNetRunReviews(layout, slug, updatedReviews);
        }

        var compass = AutopilotStateStore.LoadBlockCompass(layout, slug);
        var latestTrace = AutopilotStateStore.LoadLatestDecisionTrace(layout, slug);
        var mergedLocks = AutopilotStateStore.MergeUserLockedDates(data.TelegramState, nextState);

        var cycleResult = await orchestrator.RunCycleAsync(new AutopilotCycleRequest
        {
            AthleteSlug = slug,
            RegistryEntry = entry,
            Profile = data.Profile,
            Activities = mergedActivities,
            WellnessRecords = data.WellnessRecords,
            RunReviews = updatedReviews,
            CalendarEvents = data.CalendarEvents,
            ReferenceDate = refDate,
            TargetDate = refDate,
            LockedDates = mergedLocks,
            CurrentCompass = compass,
            PreviousHealthState = latestTrace?.HealthState,
            PreviousBlockFocus = compass?.CurrentBlockFocus,
            Trigger = "post_run",
            MaxValidationRetries = config.MaxValidationRetries,
            IsDryRun = config.DryRun
        }, cancellationToken).ConfigureAwait(false);

        states[slug] = nextState;
        if (!config.DryRun)
        {
            AutopilotStateStore.SaveDotNetTelegramState(layout, slug, nextState);
        }

        if (!cycleResult.Succeeded)
        {
            throw new InvalidOperationException(cycleResult.FailureReason ?? "Post-run cycle failed.");
        }

        var rebuilt = false;
        if (!config.DryRun)
        {
            if (cycleResult.PlanningOutcome?.Decision is not null)
            {
                AutopilotStateStore.SaveBlockCompass(layout, slug, cycleResult.PlanningOutcome.Decision.UpdatedCompass);
                AutopilotStateStore.AppendDecisionTrace(layout, slug, cycleResult.PlanningOutcome.TraceEntry);
            }

            if (cycleResult.NarrativeOutcome?.Report is not null)
            {
                AutopilotStateStore.SaveNarrativeArtifacts(layout, slug, cycleResult.NarrativeOutcome.Report);
            }

            if (config.ApplyCalendar && cycleResult.CalendarSyncPlan is not null)
            {
                var syncExec = await IntervalsCalendarSyncExecutor.ExecuteAsync(
                    cycleResult.CalendarSyncPlan,
                    entry,
                    intervalsClient,
                    config.AllowProductionWrite,
                    cancellationToken).ConfigureAwait(false);
                if (syncExec.Applied && cycleResult.UpdatedCompass is not null)
                {
                    AutopilotStateStore.SaveBlockCompass(layout, slug, cycleResult.UpdatedCompass with
                    {
                        UpdatedAt = refDate,
                        AutoEventIds = syncExec.ResultingAutoEventIds
                    });
                }
            }

            if (config.SendTelegram && telegramClient is not null
                && TelegramPreferencePolicy.IsPostRunSummaryEnabled(entry)
                && !string.IsNullOrWhiteSpace(entry.TelegramChatId)
                && cycleResult.TelegramPreview is not null)
            {
                var permission = StagingSafetyGuard.EvaluateWritePermission(slug, entry, config.AllowProductionWrite);
                if (permission.Allowed)
                {
                    await telegramClient.SendMessageAsync(cycleResult.TelegramPreview, cancellationToken).ConfigureAwait(false);
                }
            }

            if (config.RebuildDashboardOnChange)
            {
                RebuildShadowDashboard(layout, registry, slug, data with { Activities = mergedActivities, RunReviews = updatedReviews }, refDate, nextState);
                rebuilt = true;
            }
        }

        return (1, rebuilt);
    }

    private static Dictionary<string, TelegramStateRecord?> LoadEffectiveTelegramStates(
        WorkspaceLayout layout,
        AthleteRegistry registry)
    {
        var states = new Dictionary<string, TelegramStateRecord?>(StringComparer.Ordinal);
        foreach (var slug in registry.Athletes.Keys)
        {
            var dotnetState = AutopilotStateStore.LoadDotNetTelegramState(layout, slug);
            var pythonState = StrictJsonLoader.LoadTelegramState(layout, slug);
            states[slug] = dotnetState ?? pythonState;
        }

        return states;
    }

    private static List<IntervalsActivityEntry> MergeActivities(
        IReadOnlyList<IntervalsActivityEntry> existing,
        IReadOnlyList<IntervalsActivityEntry> incoming)
    {
        var byId = new Dictionary<string, IntervalsActivityEntry>(StringComparer.Ordinal);
        foreach (var act in existing)
        {
            byId[act.Id] = act;
        }

        foreach (var act in incoming)
        {
            byId[act.Id] = act;
        }

        return byId.Values.OrderByDescending(a => a.StartDateLocal).ToList();
    }

    private static void RebuildShadowDashboard(
        WorkspaceLayout layout,
        AthleteRegistry registry,
        string slug,
        AthleteWorkspaceData data,
        DateOnly refDate,
        TelegramStateRecord? effectiveTgState)
    {
        var compass = AutopilotStateStore.LoadBlockCompass(layout, slug);
        var narrative = AutopilotStateStore.LoadNarrativeSummary(layout, slug);
        var latestTrace = AutopilotStateStore.LoadLatestDecisionTrace(layout, slug);
        var dotnetReviews = AutopilotStateStore.LoadDotNetRunReviews(layout, slug);
        var mergedLocks = AutopilotStateStore.MergeUserLockedDates(data.TelegramState, effectiveTgState);
        var effectiveReviews = dotnetReviews.Count > 0 ? dotnetReviews : data.RunReviews;
        IReadOnlyList<AutopilotDecisionTraceEntry> traces = latestTrace is not null ? [latestTrace] : [];

        var snapshot = DashboardSnapshotBuilder.Build(
            slug,
            data.RegistryEntry.DashboardSlug,
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            effectiveReviews,
            data.CalendarEvents,
            refDate,
            currentCompass: compass,
            latestNarrative: narrative,
            recentTraces: traces,
            userLockedDates: mergedLocks);

        var html = DashboardHtmlRenderer.Render(snapshot);
        var outputFile = Path.Combine(layout.RootDirectory, "dist-dotnet", data.RegistryEntry.DashboardSlug, "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(outputFile, html, utf8NoBom);

        if (string.Equals(slug, registry.DefaultAthlete, StringComparison.Ordinal))
        {
            var rootIndex = Path.Combine(layout.RootDirectory, "dist-dotnet", "index.html");
            File.WriteAllText(rootIndex, html, utf8NoBom);
        }
    }
}
