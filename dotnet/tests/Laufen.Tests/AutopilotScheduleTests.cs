using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class AutopilotScheduleTests
{
    private static WorkspaceLayout ResolveRepositoryLayout() =>
        WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);

    private static WorkoutTargetSpec HrZone(int start, int? end = null) => new()
    {
        Modality = "hr_zone",
        HrZoneStart = start,
        HrZoneEnd = end
    };

    private static WorkoutDefinition EasyBase45() => new()
    {
        Title = "Aerobe Basis & Z2 Dauerlauf (45m)",
        SessionType = "easy_run",
        Steps =
        [
            new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(1) },
            new() { Kind = "steady", DurationSeconds = 1800, Target = HrZone(2) },
            new() { Kind = "cooldown", DurationSeconds = 300, Target = HrZone(1) }
        ]
    };

    private static WorkoutDefinition LongRun60() => new()
    {
        Title = "Aerober Long Run (60m Z2)",
        SessionType = "long_run",
        Steps =
        [
            new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(1) },
            new() { Kind = "steady", DurationSeconds = 2700, Target = HrZone(2) },
            new() { Kind = "cooldown", DurationSeconds = 300, Target = HrZone(1) }
        ]
    };

    private static WorkoutDefinition Recovery30() => new()
    {
        Title = "Aktive Erholung (30m Z1)",
        SessionType = "recovery_run",
        Steps =
        [
            new() { Kind = "steady", DurationSeconds = 1800, Target = HrZone(1) }
        ]
    };

    private static WorkoutDefinition Threshold4x5() => new()
    {
        Title = "Kontrollierter Schwellenlauf (4x 5m Z4)",
        SessionType = "threshold_intervals",
        Steps =
        [
            new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(1, 2) },
            new()
            {
                Kind = "repeat",
                Reps = 4,
                Work = new() { DurationSeconds = 300, Target = HrZone(4) },
                Recovery = new() { DurationSeconds = 120, Target = HrZone(1) }
            },
            new() { Kind = "cooldown", DurationSeconds = 600, Target = HrZone(1) }
        ]
    };

    private static CoachDecisionRationale DefaultRationale() => new()
    {
        Observations = ["RHR 46 bpm (-1.6 vs baseline)", "1 clean comeback run completed after 10d break"],
        TradeoffWeighed = "Re-entry durability vs immediate threshold stimulus",
        ProgressionIntent = "Complete 2nd clean Z2 run before resuming threshold on Dec 1",
        RejectedAlternative = "Threshold today rejected due to incomplete post-break ramp-up"
    };

    private static BlockCompassSnapshot DefaultCompass(DateOnly refDate, string healthState = "ReEntry") => new()
    {
        UpdatedAt = refDate,
        CurrentBlockFocus = "Post-break aerobic consolidation -> threshold reintroduction",
        BaseMilestone = "Reach 60m Z2 Long Run",
        QualityMilestone = "Resume 4x 5m Z4 once >= 2 clean base runs completed",
        ObservedAthletePatterns = ["Clean 100% Z1/Z2 comeback run on 2026-11-22"],
        LastDecisionSummary = "Replaced 2026-11-25 split A/B with Z2 Base; scheduled threshold in Week 1 end",
        HealthState = healthState,
        AutoEventIds = [160591207, 160252764]
    };

    [Test]
    public void SyntheticLoadEstimatorCalibratesAgainstIntervalsIcuWorkouts()
    {
        var easy45Tss = TrainingLoadProjector.EstimateWorkoutLoad(EasyBase45());
        var thresholdTss = TrainingLoadProjector.EstimateWorkoutLoad(Threshold4x5());
        var recovery30Tss = TrainingLoadProjector.EstimateWorkoutLoad(Recovery30());
        var long60Tss = TrainingLoadProjector.EstimateWorkoutLoad(LongRun60());

        Assert.That(easy45Tss, Is.InRange(37.0, 43.0));
        Assert.That(thresholdTss, Is.InRange(45.0, 55.0));
        Assert.That(recovery30Tss, Is.InRange(18.0, 24.0));
        Assert.That(long60Tss, Is.InRange(50.0, 60.0));
    }

    [Test]
    public void BanisterProjectorMatchesAnalyticalEwmaDecayAndImpulse()
    {
        var refDate = new DateOnly(2026, 11, 25);
        var loads = new Dictionary<DateOnly, double>
        {
            [refDate] = 40.0,
            [refDate.AddDays(3)] = 54.0
        };

        var result = TrainingLoadProjector.ProjectBanister(12.6, 7.7, refDate, loads, 14);

        Assert.That(result.DailyPoints, Has.Count.EqualTo(14));
        Assert.That(result.TotalProjectedLoad, Is.EqualTo(94.0));

        var kCtl = Math.Exp(-1.0 / 42.0);
        var kAtl = Math.Exp(-1.0 / 7.0);
        var expectedDay0Ctl = Math.Round((12.6 * kCtl) + (40.0 * (1.0 - kCtl)), 1, MidpointRounding.ToEven);
        var expectedDay0Atl = Math.Round((7.7 * kAtl) + (40.0 * (1.0 - kAtl)), 1, MidpointRounding.ToEven);

        Assert.That(result.DailyPoints[0].Ctl, Is.EqualTo(expectedDay0Ctl));
        Assert.That(result.DailyPoints[0].Atl, Is.EqualTo(expectedDay0Atl));
        Assert.That(result.MinTsbDate, Is.EqualTo(refDate.AddDays(3)));
        Assert.That(result.MinProjectedTsb, Is.LessThan(0.0));
        Assert.That(result.EndingTsb, Is.GreaterThan(result.MinProjectedTsb));
    }

    [Test]
    public void RealBerniDataEnforcesReentryRampUpAndReconciles14DaySchedule()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var berni = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);
        var autoIds = new HashSet<long> { 160591207, 160252764 };

        var dossier = CoachIntelligenceDossierBuilder.Build(
            berni.Profile,
            berni.Activities,
            berni.WellnessRecords,
            berni.RunReviews,
            berni.CalendarEvents,
            refDate,
            refDate,
            autoIds);

        Assert.That(dossier.Calendar.UpcomingEventsNext14Days, Has.Count.EqualTo(2));
        Assert.That(dossier.Calendar.UpcomingEventsNext14Days.All(e => e.Ownership == "auto"), Is.True);

        // Plan A: Immediate threshold on 2026-11-25 fails re-entry ramp-up (CleanBaseRunsSinceRecentBreak == 1)
        var prematureThreshold = new AutopilotPlanDecision
        {
            Trigger = "morning_sync",
            ReferenceDate = refDate,
            HorizonDays = 14,
            HealthState = "ReEntry",
            Operations =
            [
                new() { Date = refDate, Operation = "delete", EventId = 160252764 },
                new() { Date = refDate, Operation = "upsert", Workout = Threshold4x5() }
            ],
            Rationale = DefaultRationale(),
            UpdatedCompass = DefaultCompass(refDate)
        };

        var invalidResult = AutopilotScheduleGatekeeper.Validate(prematureThreshold, dossier);
        Assert.That(invalidResult.IsValid, Is.False);
        Assert.That(invalidResult.Errors.Select(e => e.Code), Does.Contain("reentry_requires_base_before_quality"));

        // Plan B: Easy Base on Nov 25 (2nd clean base), Long Run on Nov 28, Threshold on Dec 1, Preview runs in Week 2
        var validPlan = new AutopilotPlanDecision
        {
            Trigger = "morning_sync",
            ReferenceDate = refDate,
            HorizonDays = 14,
            HealthState = "ReEntry",
            Operations =
            [
                new() { Date = refDate, Operation = "delete", EventId = 160591207, Reason = "Defer threshold until 2 clean post-break base runs" },
                new() { Date = refDate, Operation = "upsert", Workout = EasyBase45() },
                new() { Date = refDate.AddDays(3), Operation = "upsert", Workout = LongRun60() },
                new() { Date = refDate.AddDays(6), Operation = "upsert", Workout = Threshold4x5() },
                new() { Date = refDate.AddDays(8), Operation = "upsert", Workout = EasyBase45() },
                new() { Date = refDate.AddDays(11), Operation = "upsert", Workout = LongRun60() }
            ],
            Rationale = DefaultRationale(),
            UpdatedCompass = DefaultCompass(refDate)
        };

        var validResult = AutopilotScheduleGatekeeper.Validate(validPlan, dossier);
        Assert.That(validResult.IsValid, Is.True, string.Join("; ", validResult.Errors.Select(e => e.Message)));

        var reconciled = AutopilotScheduleGatekeeper.Reconcile(validPlan, dossier);
        Assert.That(reconciled.DeletedEventIds, Is.EquivalentTo(new long[] { 160591207, 160252764 }));
        Assert.That(reconciled.WatchCommitWindow, Has.Count.EqualTo(3));
        Assert.That(reconciled.PreviewWindow, Has.Count.EqualTo(2));
        Assert.That(reconciled.CompiledUpserts, Has.Count.EqualTo(5));
        Assert.That(reconciled.BanisterProjection, Is.Not.Null);
        Assert.That(reconciled.BanisterProjection!.TotalProjectedLoad, Is.GreaterThan(200.0));
        Assert.That(reconciled.CompiledUpserts[0].CompiledWorkout, Is.Not.Null);
    }

    [Test]
    public void GatekeeperEnforcesUserLockedImmutabilityIntradayFreezeAndSickHold()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var berni = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);

        // Mark 2026-11-25 as user_locked (omitting auto IDs or passing lockedDates)
        var lockedDates = new HashSet<DateOnly> { refDate };
        var dossier = CoachIntelligenceDossierBuilder.Build(
            berni.Profile,
            berni.Activities,
            berni.WellnessRecords,
            berni.RunReviews,
            berni.CalendarEvents,
            refDate,
            refDate,
            autoEventIds: new HashSet<long> { 160591207, 160252764 },
            userLockedDates: lockedDates);

        var mutateLockedOnMorningSync = new AutopilotPlanDecision
        {
            Trigger = "morning_sync",
            ReferenceDate = refDate,
            HorizonDays = 14,
            HealthState = "ReEntry",
            Operations =
            [
                new() { Date = refDate, Operation = "upsert", Workout = EasyBase45() }
            ],
            Rationale = DefaultRationale(),
            UpdatedCompass = DefaultCompass(refDate)
        };

        Assert.That(
            AutopilotScheduleGatekeeper.Validate(mutateLockedOnMorningSync, dossier).Errors.Select(e => e.Code),
            Does.Contain("user_locked_event_immutable"));

        var mutateViaTelegramOverride = mutateLockedOnMorningSync with { Trigger = "telegram_override" };
        Assert.That(AutopilotScheduleGatekeeper.Validate(mutateViaTelegramOverride, dossier).IsValid, Is.True);

        // Intraday freeze blocks mutation on refDate even for auto events, while allowing tomorrow
        var autoDossier = CoachIntelligenceDossierBuilder.Build(
            berni.Profile,
            berni.Activities,
            berni.WellnessRecords,
            berni.RunReviews,
            berni.CalendarEvents,
            refDate,
            refDate,
            autoEventIds: new HashSet<long> { 160591207, 160252764 });

        var intradayToday = mutateLockedOnMorningSync with { Trigger = "intraday_check" };
        Assert.That(
            AutopilotScheduleGatekeeper.Validate(intradayToday, autoDossier).Errors.Select(e => e.Code),
            Does.Contain("same_day_intraday_freeze"));

        var intradayTomorrowDossier = CoachIntelligenceDossierBuilder.Build(
            berni.Profile,
            berni.Activities,
            berni.WellnessRecords,
            berni.RunReviews,
            berni.CalendarEvents.Where(e => e.Id != 160591207).ToList(),
            refDate,
            refDate,
            autoEventIds: new HashSet<long> { 160252764 });
        var intradayTomorrow = intradayToday with
        {
            Operations = [new() { Date = refDate.AddDays(1), Operation = "upsert", Workout = EasyBase45() }]
        };
        Assert.That(AutopilotScheduleGatekeeper.Validate(intradayTomorrow, intradayTomorrowDossier).IsValid, Is.True);

        // SickHold blocks all upserts and allows delete of open auto events
        var sickHoldUpsert = new AutopilotPlanDecision
        {
            Trigger = "morning_sync",
            ReferenceDate = refDate,
            HorizonDays = 14,
            HealthState = "SickHold",
            Operations = [new() { Date = refDate, Operation = "upsert", Workout = Recovery30() }],
            Rationale = DefaultRationale(),
            UpdatedCompass = DefaultCompass(refDate, "SickHold")
        };
        Assert.That(
            AutopilotScheduleGatekeeper.Validate(sickHoldUpsert, autoDossier).Errors.Select(e => e.Code),
            Does.Contain("sickhold_forbids_planned_workouts"));

        var sickHoldClear = sickHoldUpsert with
        {
            Operations =
            [
                new() { Date = refDate, Operation = "delete", EventId = 160591207 },
                new() { Date = refDate, Operation = "delete", EventId = 160252764 }
            ]
        };
        var sickCleared = AutopilotScheduleGatekeeper.Reconcile(sickHoldClear, autoDossier);
        Assert.That(sickCleared.WatchCommitWindow, Is.Empty);
        Assert.That(sickCleared.DeletedEventIds, Is.EquivalentTo(new long[] { 160591207, 160252764 }));
    }

    [Test]
    public void GatekeeperEnforcesNoConsecutiveRunDaysWhenConfigured()
    {
        var layout = ResolveRepositoryLayout();
        var baseProfile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var strictProfile = baseProfile with { AllowConsecutiveRunDays = false };
        var refDate = new DateOnly(2026, 11, 25);

        var dossier = CoachIntelligenceDossierBuilder.Build(
            strictProfile,
            [],
            [],
            null,
            null,
            refDate);

        var backToBackPlan = new AutopilotPlanDecision
        {
            Trigger = "morning_sync",
            ReferenceDate = refDate,
            HorizonDays = 14,
            HealthState = "Normal",
            Operations =
            [
                new() { Date = refDate, Operation = "upsert", Workout = EasyBase45() },
                new() { Date = refDate.AddDays(1), Operation = "upsert", Workout = LongRun60() }
            ],
            Rationale = DefaultRationale(),
            UpdatedCompass = DefaultCompass(refDate, "Normal")
        };

        Assert.That(
            AutopilotScheduleGatekeeper.Validate(backToBackPlan, dossier).Errors.Select(e => e.Code),
            Does.Contain("consecutive_run_days_not_allowed"));

        var spacedPlan = backToBackPlan with
        {
            Operations =
            [
                new() { Date = refDate, Operation = "upsert", Workout = EasyBase45() },
                new() { Date = refDate.AddDays(2), Operation = "upsert", Workout = LongRun60() }
            ]
        };
        Assert.That(AutopilotScheduleGatekeeper.Validate(spacedPlan, dossier).IsValid, Is.True);
    }

    [Test]
    public void WeatherForecastEvaluatesPaceIntervalFriendlinessAndProfileBackwardCompatibility()
    {
        var refDate = new DateOnly(2026, 11, 25);
        var weather = new WeatherForecastDossier
        {
            Source = "open-meteo",
            LocationLabel = "Gerasdorf",
            Days =
            [
                new() { Date = refDate.AddDays(-1), TempMinC = 4, TempMaxC = 10, PrecipitationMm = 0, MaxWindGustKmh = 15, Summary = "Past" },
                new() { Date = refDate, TempMinC = 5, TempMaxC = 14, PrecipitationMm = 0.5, MaxWindGustKmh = 18, Summary = "Clear" },
                new() { Date = refDate.AddDays(1), TempMinC = 8, TempMaxC = 16, PrecipitationMm = 12.0, MaxWindGustKmh = 52, Summary = "Stormy" },
                new() { Date = refDate.AddDays(2), TempMinC = 17, TempMaxC = 27, PrecipitationMm = 0, MaxWindGustKmh = 10, Summary = "Hot" }
            ]
        };

        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var dossier = CoachIntelligenceDossierBuilder.Build(profile, [], [], null, null, refDate, weatherForecast: weather);

        Assert.That(dossier.Weather, Is.Not.Null);
        Assert.That(dossier.Weather!.Days, Has.Count.EqualTo(3));
        Assert.That(dossier.Weather.Days[0].PaceIntervalFriendly, Is.True);
        Assert.That(dossier.Weather.Days[1].PaceIntervalFriendly, Is.False);
        Assert.That(dossier.Weather.Days[2].PaceIntervalFriendly, Is.False);

        const string profileJsonWithGrid = """
            {
              "athlete": {
                "id": "i1",
                "name": "GridRunner",
                "preferred_run_days": ["tuesday", "thursday", "sunday"],
                "location": { "latitude": 48.29, "longitude": 16.47, "label": "Gerasdorf" },
                "heart_rate": {
                  "resting_hr": 48,
                  "max_hr": 192,
                  "lthr": 178,
                  "zones": { "z2": { "zone": 2, "name": "Zone 2", "min": 151, "max": 159 } }
                }
              }
            }
            """;

        var deserialized = StrictJsonLoader.Deserialize<AthleteProfileDocument>(profileJsonWithGrid).Athlete;
        Assert.That(deserialized.PreferredRunDays, Is.EquivalentTo(["tuesday", "thursday", "sunday"]));
        Assert.That(deserialized.Location?.Label, Is.EqualTo("Gerasdorf"));
    }

    [Test]
    public void StrictJsonDeserializesBlockCompassWithAndWithoutAutopilotFields()
    {
        const string legacyCompassJson = """
            {
              "updated_at": "2026-11-25",
              "current_block_focus": "Base",
              "base_milestone": "50m Z2",
              "quality_milestone": "4x5m Z4",
              "observed_athlete_patterns": [],
              "last_decision_summary": "Easy 45m"
            }
            """;

        var legacy = StrictJsonLoader.Deserialize<BlockCompassSnapshot>(legacyCompassJson);
        Assert.That(legacy.HealthState, Is.Null);
        Assert.That(legacy.AutoEventIds, Is.Null);

        var extendedJson = legacyCompassJson.Replace(
            "\"last_decision_summary\": \"Easy 45m\"",
            "\"last_decision_summary\": \"Easy 45m\", \"health_state\": \"ReEntry\", \"auto_event_ids\": [160591207]",
            StringComparison.Ordinal);
        var extended = StrictJsonLoader.Deserialize<BlockCompassSnapshot>(extendedJson);
        Assert.That(extended.HealthState, Is.EqualTo("ReEntry"));
        Assert.That(extended.AutoEventIds, Is.EquivalentTo(new long[] { 160591207 }));

        // Verify JSON serialization roundtrip on AutopilotPlanDecision
        var decision = new AutopilotPlanDecision
        {
            Trigger = "morning_sync",
            ReferenceDate = new DateOnly(2026, 11, 25),
            HorizonDays = 14,
            HealthState = "ReEntry",
            Operations = [new() { Date = new DateOnly(2026, 11, 25), Operation = "upsert", Workout = EasyBase45() }],
            Rationale = DefaultRationale(),
            UpdatedCompass = extended
        };
        var json = JsonSerializer.Serialize(decision);
        var roundtrip = StrictJsonLoader.Deserialize<AutopilotPlanDecision>(json);
        Assert.That(roundtrip.HorizonDays, Is.EqualTo(14));
        Assert.That(roundtrip.Operations, Has.Count.EqualTo(1));
    }
}
