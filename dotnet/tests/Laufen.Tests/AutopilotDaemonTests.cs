using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class AutopilotDaemonTests
{
    [Test]
    public void DaemonScheduleEvaluatorHandlesTimingIdempotencyLookbackAndChatResolution()
    {
        var registry = new AthleteRegistry
        {
            DefaultAthlete = "berni",
            Athletes = new Dictionary<string, AthleteRegistryEntry>(StringComparer.Ordinal)
            {
                ["berni"] = new()
                {
                    IntervalsId = "i697752",
                    DisplayName = "Berni",
                    Active = true,
                    DashboardSlug = "berni",
                    TelegramChatId = "157754568",
                    BriefingEnabled = true,
                    BriefingTime = "06:30"
                },
                ["inactive"] = new()
                {
                    IntervalsId = "i771119",
                    DisplayName = "Inactive",
                    Active = false,
                    DashboardSlug = "inactive",
                    TelegramChatId = "999999",
                    BriefingEnabled = true
                }
            }
        };

        var beforeMorning = new DateTimeOffset(2026, 11, 26, 6, 29, 0, TimeSpan.Zero);
        var atMorning = new DateTimeOffset(2026, 11, 26, 6, 30, 0, TimeSpan.Zero);
        var states = new Dictionary<string, TelegramStateRecord?>(StringComparer.Ordinal)
        {
            ["berni"] = new()
            {
                LastBriefingDate = "2026-11-25",
                LastActivitiesPollTs = beforeMorning.ToUnixTimeSeconds()
            }
        };

        var dueBefore = DaemonScheduleEvaluator.EvaluateDueActions(registry, states, beforeMorning);
        Assert.That(dueBefore.Any(d => d.ActionKind == DaemonScheduledActionKind.MorningBriefing), Is.False);
        Assert.That(dueBefore.Any(d => d.ActionKind == DaemonScheduledActionKind.EveningPreview), Is.False);

        var dueAt = DaemonScheduleEvaluator.EvaluateDueActions(registry, states, atMorning);
        Assert.That(dueAt.Any(d => d.AthleteSlug == "berni" && d.ActionKind == DaemonScheduledActionKind.MorningBriefing), Is.True);
        Assert.That(dueAt.Any(d => d.AthleteSlug == "inactive"), Is.False);

        // Once sent today, morning briefing is idempotent
        states["berni"] = TelegramInteractionRouter.MarkMorningBriefingSent(states["berni"], new DateOnly(2026, 11, 26));
        var dueAfterSent = DaemonScheduleEvaluator.EvaluateDueActions(registry, states, atMorning);
        Assert.That(dueAfterSent.Any(d => d.ActionKind == DaemonScheduledActionKind.MorningBriefing), Is.False);

        // Run detection: skips >6h old, skips future, skips already-notified
        var now = new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);
        var tgState = new TelegramStateRecord { NotifiedActivityIds = ["run_done"] };
        var activities = new List<IntervalsActivityEntry>
        {
            MakeRun("run_old", new DateTime(2026, 11, 26, 7, 0, 0, DateTimeKind.Unspecified)),
            MakeRun("run_done", new DateTime(2026, 11, 26, 13, 0, 0, DateTimeKind.Unspecified)),
            MakeRun("run_fresh", new DateTime(2026, 11, 26, 13, 30, 0, DateTimeKind.Unspecified)),
            MakeRun("run_future", new DateTime(2026, 11, 26, 16, 0, 0, DateTimeKind.Unspecified))
        };

        var detected = DaemonScheduleEvaluator.DetectNewCompletedRuns(activities, tgState, now, lookbackHours: 6);
        Assert.That(detected, Has.Count.EqualTo(1));
        Assert.That(detected[0].Id, Is.EqualTo("run_fresh"));

        Assert.That(DaemonScheduleEvaluator.ResolveAthleteByChatId(registry, "157754568"), Is.EqualTo("berni"));
        Assert.That(DaemonScheduleEvaluator.ResolveAthleteByChatId(registry, "unknown-chat"), Is.Null);
    }

    [Test]
    public async Task DaemonRunnerProcessesAcceptEasierNewRunAndIsolatesAthleteFaults()
    {
        var tempRoot = CreateTempMultiAthleteWorkspace();
        try
        {
            var layout = WorkspaceLayout.FromRoot(tempRoot);
            var now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
            var clock = new FixedDaemonClock(now);
            var runner = new AutopilotDaemonRunner(clock);

            // Seed berni & berni-test states so morning briefing isn't triggered during update/poll tests
            var initialState = new TelegramStateRecord
            {
                LastBriefingDate = "2026-11-25",
                LastDispatchedBriefingDate = "2026-11-25",
                LastActivitiesPollTs = now.AddMinutes(-15).ToUnixTimeSeconds()
            };
            AutopilotStateStore.SaveDotNetTelegramState(layout, "berni", initialState);
            AutopilotStateStore.SaveDotNetTelegramState(layout, "berni-test", initialState);

            // Tick 1: accept:2026-11-26 answers callback immediately, locks date, zero LLM calls
            var tgFake = new FakeTelegramClient(
            [
                new TelegramIncomingUpdate
                {
                    UpdateId = 500,
                    CallbackQuery = new TelegramCallbackQuery
                    {
                        Id = "cb-accept",
                        ChatId = "157754568",
                        Data = "accept:2026-11-26"
                    }
                }
            ]);

            var countingPlanner = new CountingLlmClient(new SimulationStubLlmClient());
            var narrator = new NarrativeStubLlmClient();

            var tick1 = await runner.RunSingleTickAsync(
                layout,
                new DaemonRuntimeConfig
                {
                    DryRun = false,
                    ApplyCalendar = false,
                    SendTelegram = false,
                    AllowProductionWrite = true,
                    RebuildDashboardOnChange = false
                },
                tgFake,
                intervalsClient: null,
                countingPlanner,
                narrator);

            Assert.That(tick1.UpdatesProcessed, Is.EqualTo(1));
            Assert.That(tick1.CallbacksAnswered, Is.EqualTo(1));
            Assert.That(tick1.OverridesExecuted, Is.EqualTo(0));
            Assert.That(countingPlanner.CallCount, Is.EqualTo(0));
            Assert.That(tgFake.AnsweredCallbacks, Has.Count.EqualTo(1));
            Assert.That(tgFake.AnsweredCallbacks[0].Id, Is.EqualTo("cb-accept"));

            var savedAfterAccept = AutopilotStateStore.LoadDotNetTelegramState(layout, "berni");
            Assert.That(savedAfterAccept!.LastUpdateId, Is.EqualTo(500L));
            Assert.That(savedAfterAccept.UserLockedDates, Does.Contain(new DateOnly(2026, 11, 26)));

            // Tick 2: easier:2026-11-25 triggers LLM override + mocked calendar sync + reply,
            // AND activity poll fails for berni while succeeding for berni-test (fault isolation + post_run review + dashboard)
            tgFake.QueueUpdates(
            [
                new TelegramIncomingUpdate
                {
                    UpdateId = 501,
                    CallbackQuery = new TelegramCallbackQuery
                    {
                        Id = "cb-easier",
                        ChatId = "157754568",
                        Data = "easier:2026-11-25"
                    }
                }
            ]);

            var intervalsFake = new FakeIntervalsClient
            {
                FailActivitiesForAthleteId = "i697752",
                ActivitiesByAthleteId =
                {
                    ["i771119"] = [MakeRun("new_run_77", new DateTime(2026, 11, 25, 8, 30, 0, DateTimeKind.Unspecified))]
                }
            };

            var tick2 = await runner.RunSingleTickAsync(
                layout,
                new DaemonRuntimeConfig
                {
                    DryRun = false,
                    ApplyCalendar = true,
                    SendTelegram = true,
                    AllowProductionWrite = true,
                    RebuildDashboardOnChange = true
                },
                tgFake,
                intervalsFake,
                countingPlanner,
                narrator);

            Assert.That(tick2.CallbacksAnswered, Is.EqualTo(1));
            Assert.That(tick2.OverridesExecuted, Is.EqualTo(1));
            Assert.That(countingPlanner.CallCount, Is.GreaterThanOrEqualTo(2)); // override + berni-test post_run
            Assert.That(tick2.PostRunReviewsTriggered, Is.EqualTo(1)); // berni-test succeeded despite berni poll failing!
            Assert.That(tick2.Errors, Has.Count.EqualTo(1));
            Assert.That(tick2.Errors[0], Does.Contain("berni"));
            Assert.That(tick2.DashboardsRebuilt, Is.GreaterThanOrEqualTo(2));
            Assert.That(tgFake.SentMessages.Count, Is.GreaterThanOrEqualTo(1));

            var testReviews = AutopilotStateStore.LoadDotNetRunReviews(layout, "berni-test");
            Assert.That(testReviews.ContainsKey("new_run_77"), Is.True);
            var testState = AutopilotStateStore.LoadDotNetTelegramState(layout, "berni-test");
            Assert.That(testState!.NotifiedActivityIds, Does.Contain("new_run_77"));
            Assert.That(File.Exists(Path.Combine(tempRoot, "dist-dotnet", "berni", "index.html")), Is.True);
            Assert.That(File.Exists(Path.Combine(tempRoot, "dist-dotnet", "berni-test-415ebf", "index.html")), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static IntervalsActivityEntry MakeRun(string id, DateTime startLocal) => new()
    {
        Id = id,
        IcuAthleteId = "i771119",
        Source = "GARMIN_CONNECT",
        StartDateLocal = startLocal,
        Type = "Run",
        Name = "Morning Base Run",
        Distance = 6500.0,
        MovingTime = 2400,
        AverageHeartrate = 148,
        MaxHeartrate = 158,
        HasHeartrate = true,
        IcuTrainingLoad = 45,
        IcuHrZoneTimes = [600, 1600, 200, 0, 0]
    };

    private static string CreateTempMultiAthleteWorkspace()
    {
        var source = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"laufen-daemon-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "athletes", "berni"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "athletes", "berni-test"));

        const string registryJson = """
            {
              "default_athlete": "berni",
              "athletes": {
                "berni": {
                  "intervals_id": "i697752",
                  "display_name": "Berni",
                  "active": true,
                  "dashboard_slug": "berni",
                  "telegram_chat_id": "157754568",
                  "briefing_enabled": true,
                  "briefing_time": "06:30"
                },
                "berni-test": {
                  "intervals_id": "i771119",
                  "display_name": "Berni Test",
                  "active": true,
                  "dashboard_slug": "berni-test-415ebf",
                  "telegram_chat_id": "987654321",
                  "briefing_enabled": true,
                  "briefing_time": "06:30"
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(tempRoot, "athletes", "registry.json"), registryJson);

        foreach (var slug in new[] { "berni", "berni-test" })
        {
            foreach (var file in Directory.GetFiles(Path.Combine(source.AthletesDirectory, slug)))
            {
                File.Copy(file, Path.Combine(tempRoot, "athletes", slug, Path.GetFileName(file)));
            }
        }

        return tempRoot;
    }

    private sealed class CountingLlmClient(ILlmClient inner) : ILlmClient
    {
        public int CallCount { get; private set; }

        public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return inner.GenerateAsync(request, cancellationToken);
        }
    }

    private sealed class FakeTelegramClient(IEnumerable<TelegramIncomingUpdate> initialUpdates) : ITelegramBotClient
    {
        private readonly Queue<TelegramIncomingUpdate> _queue = new(initialUpdates);

        public List<(string Id, string Text)> AnsweredCallbacks { get; } = [];
        public List<TelegramOutgoingMessage> SentMessages { get; } = [];

        public void QueueUpdates(IEnumerable<TelegramIncomingUpdate> updates)
        {
            foreach (var u in updates)
            {
                _queue.Enqueue(u);
            }
        }

        public Task<long?> SendMessageAsync(TelegramOutgoingMessage message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.FromResult<long?>(SentMessages.Count + 1000);
        }

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string text, CancellationToken cancellationToken = default)
        {
            AnsweredCallbacks.Add((callbackQueryId, text));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TelegramIncomingUpdate>> GetUpdatesAsync(
            long? offset = null,
            int timeoutSeconds = 0,
            CancellationToken cancellationToken = default)
        {
            var list = new List<TelegramIncomingUpdate>();
            while (_queue.Count > 0)
            {
                var u = _queue.Dequeue();
                if (!offset.HasValue || u.UpdateId >= offset.Value)
                {
                    list.Add(u);
                }
            }

            return Task.FromResult<IReadOnlyList<TelegramIncomingUpdate>>(list);
        }
    }

    private sealed class FakeIntervalsClient : IIntervalsIcuClient
    {
        private long _nextEventId = 8000;

        public string? FailActivitiesForAthleteId { get; init; }
        public Dictionary<string, IReadOnlyList<IntervalsActivityEntry>> ActivitiesByAthleteId { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<IntervalsActivityEntry>> GetActivitiesAsync(
            string athleteId,
            DateOnly oldest,
            DateOnly newest,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(athleteId, FailActivitiesForAthleteId, StringComparison.Ordinal))
            {
                throw new HttpRequestException("Simulated upstream failure", null, HttpStatusCode.ServiceUnavailable);
            }

            return Task.FromResult(ActivitiesByAthleteId.TryGetValue(athleteId, out var list) ? list : []);
        }

        public Task<ActivityStreamData?> GetActivityStreamAsync(string activityId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityStreamData?>(new ActivityStreamData
            {
                Time = [0, 1, 2],
                Heartrate = [142, 143, 144],
                VelocitySmooth = [2.2, 2.2, 2.2]
            });

        public Task<IReadOnlyList<IntervalsWellnessEntry>> GetWellnessAsync(
            string athleteId, DateOnly oldest, DateOnly newest, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IntervalsWellnessEntry>>([]);

        public Task<IReadOnlyList<CalendarEventEntry>> GetCalendarEventsAsync(
            string athleteId, DateOnly oldest, DateOnly newest, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEventEntry>>([]);

        public Task<CalendarEventEntry> CreateCalendarEventAsync(
            string athleteId,
            IntervalsCalendarEventUpsertRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CalendarEventEntry
            {
                Id = ++_nextEventId,
                AthleteId = athleteId,
                Category = request.Category,
                StartDateLocal = DateTime.Parse(request.StartDateLocal, CultureInfo.InvariantCulture),
                Name = request.Name,
                Description = request.Description,
                ExternalId = request.ExternalId
            });

        public Task<bool> DeleteCalendarEventAsync(string athleteId, long eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IntervalsAthleteProfileDto> GetAthleteProfileAsync(string athleteId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntervalsAthleteProfileDto { Id = athleteId, Name = "Fake" });

        public Task<IntervalsActivityEntry> UpdateActivityNoteAsync(
            string athleteId, string activityId, ActivityNoteUpdateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntervalsActivityEntry
            {
                Id = activityId,
                IcuAthleteId = athleteId,
                Source = "GARMIN_CONNECT",
                StartDateLocal = DateTime.UtcNow
            });
    }
}
