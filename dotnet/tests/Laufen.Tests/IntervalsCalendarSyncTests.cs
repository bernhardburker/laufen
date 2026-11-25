using System.Net;
using System.Text;
using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class IntervalsCalendarSyncTests
{
    private static WorkoutDefinition EasyBase45(string title = "Aerobe Basis & Z2 Dauerlauf (45m)") => new()
    {
        Title = title,
        SessionType = "easy_run",
        Steps =
        [
            new() { Kind = "warmup", DurationSeconds = 600, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } },
            new() { Kind = "steady", DurationSeconds = 1800, Target = new() { Modality = "hr_zone", HrZoneStart = 2 } },
            new() { Kind = "cooldown", DurationSeconds = 300, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } }
        ]
    };

    private static WorkoutDefinition LongRun60() => new()
    {
        Title = "Aerober Long Run (60m Z2)",
        SessionType = "long_run",
        Steps =
        [
            new() { Kind = "warmup", DurationSeconds = 600, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } },
            new() { Kind = "steady", DurationSeconds = 2700, Target = new() { Modality = "hr_zone", HrZoneStart = 2 } },
            new() { Kind = "cooldown", DurationSeconds = 300, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } }
        ]
    };

    private static AutopilotPlanDecision BuildDecision(
        DateOnly refDate,
        string trigger,
        IReadOnlyList<ScheduledDayOperation> ops,
        string healthState = "Normal") => new()
        {
            Trigger = trigger,
            ReferenceDate = refDate,
            HorizonDays = 14,
            HealthState = healthState,
            Operations = ops,
            Rationale = new()
            {
                Observations = ["Test sync"],
                TradeoffWeighed = "Stability vs churn",
                ProgressionIntent = "Maintain 14d rolling schedule",
                RejectedAlternative = "None"
            },
            UpdatedCompass = new()
            {
                UpdatedAt = refDate,
                CurrentBlockFocus = "Base",
                BaseMilestone = "60m Z2",
                QualityMilestone = "4x5m Z4",
                ObservedAthletePatterns = [],
                LastDecisionSummary = "Test decision",
                HealthState = healthState
            }
        };

    private static CalendarEventEntry MakeCalendarEvent(
        long id,
        DateOnly date,
        string name,
        string description,
        string? externalId = null,
        string? pairedActivityId = null) => new()
        {
            Id = id,
            AthleteId = "i771119",
            Category = "WORKOUT",
            StartDateLocal = new DateTime(date.Year, date.Month, date.Day, 7, 0, 0, DateTimeKind.Unspecified),
            Type = "Run",
            Name = name,
            Description = description,
            ExternalId = externalId,
            PairedActivityId = pairedActivityId,
            MovingTime = 2700
        };

    [Test]
    public void CoachIntelligenceDossierRecognizesSelfDescribingLaufenAutoExternalId()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni-test").Athlete;
        var refDate = new DateOnly(2026, 12, 1);

        var autoByExternalId = MakeCalendarEvent(
            901,
            refDate,
            "Auto Workout",
            "Warmup\n- 10m Z1 HR\n",
            externalId: "laufen-auto-2026-12-01");
        var manualEvent = MakeCalendarEvent(
            902,
            refDate.AddDays(2),
            "Manual Event",
            "Warmup\n- 10m Z1 HR\n",
            externalId: null);

        var dossier = CoachIntelligenceDossierBuilder.Build(
            profile,
            [],
            [],
            null,
            [autoByExternalId, manualEvent],
            refDate);

        Assert.That(dossier.Calendar.UpcomingEventsNext14Days[0].Ownership, Is.EqualTo("auto"));
        Assert.That(dossier.Calendar.UpcomingEventsNext14Days[1].Ownership, Is.EqualTo("user_locked"));
    }

    [Test]
    public void IntervalsCalendarSyncPlannerSkipsUnchangedAutoAndProtectsUserLockedUnlessOverride()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni-test").Athlete;
        var refDate = new DateOnly(2026, 12, 1);

        var easyWorkout = EasyBase45();
        var compiledEasy = IntervalsWorkoutCompiler.Compile(easyWorkout);

        var unchangedAuto = MakeCalendarEvent(
            1001,
            refDate,
            easyWorkout.Title,
            compiledEasy.DslText,
            externalId: "laufen-auto-2026-12-01");
        var staleAuto = MakeCalendarEvent(
            1002,
            refDate.AddDays(2),
            "Old Auto Run",
            "Warmup\n- 10m Z1 HR\n",
            externalId: "laufen-auto-2026-12-03");
        var lockedEvent = MakeCalendarEvent(
            1003,
            refDate.AddDays(4),
            "Sacred User Event",
            "Warmup\n- 10m Z1 HR\n");

        var calendar = new List<CalendarEventEntry> { unchangedAuto, staleAuto, lockedEvent };
        var autoIds = new HashSet<long> { 1001, 1002 };

        var dossier = CoachIntelligenceDossierBuilder.Build(
            profile,
            [],
            [],
            null,
            calendar,
            refDate,
            autoEventIds: autoIds);

        var decision = BuildDecision(
            refDate,
            "morning_sync",
            [
                new() { Date = refDate, Operation = "upsert", Workout = easyWorkout },
                new() { Date = refDate.AddDays(2), Operation = "upsert", Workout = LongRun60() }
            ]);

        var reconciled = AutopilotScheduleGatekeeper.Reconcile(decision, dossier);
        var plan = IntervalsCalendarSyncPlanner.BuildPlan(
            "berni-test",
            "i771119",
            reconciled,
            calendar,
            autoIds,
            new HashSet<DateOnly>(),
            isDryRun: true);

        Assert.That(plan.UnchangedCount, Is.EqualTo(1));
        Assert.That(plan.DeleteCount, Is.EqualTo(1));
        Assert.That(plan.CreateCount, Is.EqualTo(1));
        Assert.That(plan.Actions.Any(a => a.Kind == "skip_unchanged" && a.ExistingEventId == 1001), Is.True);
        Assert.That(plan.Actions.Any(a => a.Kind == "delete" && a.ExistingEventId == 1002), Is.True);
        Assert.That(plan.Actions.Any(a => a.Kind == "create" && a.Date == refDate.AddDays(2)), Is.True);

        // telegram_override can replace user_locked event
        var overrideDecision = BuildDecision(
            refDate,
            "telegram_override",
            [new() { Date = refDate.AddDays(4), Operation = "delete", EventId = 1003 }]);
        var overrideReconciled = AutopilotScheduleGatekeeper.Reconcile(overrideDecision, dossier);
        var overridePlan = IntervalsCalendarSyncPlanner.BuildPlan(
            "berni-test",
            "i771119",
            overrideReconciled,
            calendar,
            autoIds,
            new HashSet<DateOnly>(),
            isDryRun: true);

        Assert.That(overridePlan.DeleteCount, Is.EqualTo(1));
        Assert.That(overridePlan.Actions[0].ExistingEventId, Is.EqualTo(1003));
    }

    [Test]
    public void StagingSafetyGuardAllowsTestTenantsAndBlocksProductionWithoutFlag()
    {
        var staging = new AthleteRegistryEntry
        {
            IntervalsId = "i771119",
            DisplayName = "Berni Test",
            Active = false,
            DashboardSlug = "berni-test-415ebf"
        };
        var prod = new AthleteRegistryEntry
        {
            IntervalsId = "i697752",
            DisplayName = "Berni",
            Active = true,
            DashboardSlug = "berni"
        };

        Assert.That(StagingSafetyGuard.EvaluateWritePermission("berni-test", staging).Allowed, Is.True);
        Assert.That(StagingSafetyGuard.EvaluateWritePermission("berni", prod, allowProductionWrite: false).Allowed, Is.False);
        Assert.That(StagingSafetyGuard.EvaluateWritePermission("berni", prod, allowProductionWrite: true).Allowed, Is.True);
    }

    [Test]
    public async Task IntervalsIcuHttpClientAndSyncExecutorHandleStreamsDeletesAndDryRunCleanly()
    {
        var calls = new List<(string Method, string Url, string Auth)>();
        long nextCreatedId = 5000;

        using var handler = new MockHttpMessageHandler(req =>
        {
            var auth = req.Headers.Authorization?.ToString() ?? string.Empty;
            var url = req.RequestUri!.ToString();
            calls.Add((req.Method.Method, url, auth));

            if (req.Method == HttpMethod.Get && url.Contains("/streams", StringComparison.Ordinal))
            {
                const string streamPayload = """
                    [
                      { "type": "time", "data": [0, 1, 2] },
                      { "type": "heartrate", "data": [140, 141, 142] },
                      { "type": "velocity_smooth", "data": [2.1, 2.2, 2.2] },
                      { "type": "fixed_altitude", "data": [160.0, null, 160.5] }
                    ]
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(streamPayload, Encoding.UTF8, "application/json")
                };
            }

            if (req.Method == HttpMethod.Delete && url.EndsWith("/events/999", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (req.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (req.Method == HttpMethod.Post && url.EndsWith("/events", StringComparison.Ordinal))
            {
                var createdId = ++nextCreatedId;
                var json = $$"""
                    {
                      "id": {{createdId}},
                      "athlete_id": "i771119",
                      "category": "WORKOUT",
                      "start_date_local": "2026-12-03T07:00:00",
                      "name": "Aerober Long Run (60m Z2)",
                      "external_id": "laufen-auto-2026-12-03"
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var client = new IntervalsIcuHttpClient("secret-key", "https://intervals.icu/api/v1", httpClient);

        // 1. Stream array-to-object conversion + Basic Auth header check
        var stream = await client.GetActivityStreamAsync("i213105633");
        Assert.That(stream, Is.Not.Null);
        Assert.That(stream!.Time, Has.Count.EqualTo(3));
        Assert.That(stream.FixedAltitude![1], Is.Null);
        Assert.That(calls[0].Auth, Does.StartWith("Basic "));

        // 2. 404 delete treated idempotently as true
        Assert.That(await client.DeleteCalendarEventAsync("i771119", 999), Is.True);

        calls.Clear();
        var stagingEntry = new AthleteRegistryEntry
        {
            IntervalsId = "i771119",
            DisplayName = "Berni Test",
            Active = false,
            DashboardSlug = "berni-test-415ebf"
        };

        var refDate = new DateOnly(2026, 12, 1);
        var dryPlan = new CalendarSyncPlan
        {
            AthleteSlug = "berni-test",
            IntervalsAthleteId = "i771119",
            ReferenceDate = refDate,
            Trigger = "morning_sync",
            IsDryRun = true,
            Actions =
            [
                new CalendarSyncAction
                {
                    Kind = "skip_unchanged",
                    Date = refDate,
                    ExistingEventId = 1001,
                    Ownership = "auto",
                    Request = null,
                    Reason = "Unchanged"
                },
                new CalendarSyncAction
                {
                    Kind = "create",
                    Date = refDate.AddDays(2),
                    ExistingEventId = null,
                    Ownership = "auto",
                    Request = new IntervalsCalendarEventUpsertRequest
                    {
                        StartDateLocal = "2026-12-03T07:00:00",
                        Name = "Aerober Long Run (60m Z2)",
                        Description = "Warmup\n- 10m Z1 HR\n",
                        ExternalId = "laufen-auto-2026-12-03"
                    },
                    Reason = "Create"
                }
            ],
            DeleteCount = 0,
            CreateCount = 1,
            UnchangedCount = 1,
            ProtectedSkippedCount = 0
        };

        // 3. Dry-run performs zero HTTP calls
        var dryResult = await IntervalsCalendarSyncExecutor.ExecuteAsync(dryPlan, stagingEntry, client);
        Assert.That(dryResult.Applied, Is.False);
        Assert.That(calls, Is.Empty);
        Assert.That(dryResult.ResultingAutoEventIds, Is.EqualTo(new long[] { 1001 }));

        // 4. Live staging execution creates event and combines retained + newly created auto IDs
        var livePlan = dryPlan with { IsDryRun = false };
        var liveResult = await IntervalsCalendarSyncExecutor.ExecuteAsync(livePlan, stagingEntry, client);
        Assert.That(liveResult.Applied, Is.True);
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(liveResult.CreatedEventIds, Is.EqualTo(new long[] { 5001 }));
        Assert.That(liveResult.ResultingAutoEventIds, Is.EqualTo(new long[] { 1001, 5001 }));
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
