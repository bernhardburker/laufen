using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class WorkoutAstAndCompilerTests
{
    private static WorkspaceLayout ResolveRepositoryLayout() =>
        WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);

    private static WorkoutTargetSpec HrZone(int start, int? end = null) => new()
    {
        Modality = "hr_zone",
        HrZoneStart = start,
        HrZoneEnd = end
    };

    private static WorkoutTargetSpec PaceRange(int fastSec, int slowSec) => new()
    {
        Modality = "pace_range",
        PaceFastSecPerKm = fastSec,
        PaceSlowSecPerKm = slowSec
    };

    private static CoachDecisionRationale DefaultRationale() => new()
    {
        Observations = ["Resting HR stable at 46 bpm", "1 clean comeback run completed after 10d break"],
        TradeoffWeighed = "Progression vs post-break durability",
        ProgressionIntent = "Consolidate aerobic durability before resuming threshold",
        RejectedAlternative = "Full threshold session skipped due to single post-break run"
    };

    private static BlockCompassSnapshot DefaultCompass(DateOnly date) => new()
    {
        UpdatedAt = date,
        CurrentBlockFocus = "Post-break aerobic consolidation",
        BaseMilestone = "Build toward 50-55m clean Z2 runs",
        QualityMilestone = "Resume 4x 5m Z4 after 2 clean base sessions",
        ObservedAthletePatterns = ["Short interval rests keep HR elevated"],
        LastDecisionSummary = "Prescribed 45m Z2 aerobic run"
    };

    [Test]
    public void CompilerMatchesRealBerniCalendarEventsSlot2AAndSlot2BDslExactly()
    {
        var layout = ResolveRepositoryLayout();
        var events = StrictJsonLoader.LoadCalendarEvents(layout, "berni");
        var slot2A = events.Single(e => e.Id == 160591207);
        var slot2B = events.Single(e => e.Id == 160252764);

        var ast2A = new WorkoutDefinition
        {
            Title = slot2A.Name!,
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

        var ast2B = new WorkoutDefinition
        {
            Title = slot2B.Name!,
            SessionType = "easy_run",
            Steps =
            [
                new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(1) },
                new() { Kind = "steady", DurationSeconds = 1800, Target = HrZone(2) },
                new() { Kind = "cooldown", DurationSeconds = 300, Target = HrZone(1) }
            ]
        };

        var compiled2A = IntervalsWorkoutCompiler.Compile(ast2A);
        var compiled2B = IntervalsWorkoutCompiler.Compile(ast2B);

        Assert.That(compiled2A.TotalDurationSeconds, Is.EqualTo(2880));
        Assert.That(compiled2A.DslText, Is.EqualTo(slot2A.Description));
        Assert.That(compiled2B.TotalDurationSeconds, Is.EqualTo(2700));
        Assert.That(compiled2B.DslText, Is.EqualTo(slot2B.Description));
    }

    [Test]
    public void CompilerFormatsPaceIntervalsProgressiveThresholdMicroThresholdWithCueAndRecoveryRun()
    {
        var intervals = new WorkoutDefinition
        {
            Title = "10x 1m Speed Intervals",
            SessionType = "intervals",
            Steps =
            [
                new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(1, 2) },
                new()
                {
                    Kind = "repeat",
                    Reps = 10,
                    Work = new() { DurationSeconds = 60, Target = PaceRange(310, 325) },
                    Recovery = new() { DurationSeconds = 90, Target = HrZone(1) }
                },
                new() { Kind = "cooldown", DurationSeconds = 300, Target = HrZone(1) }
            ]
        };

        var compiledIntervals = IntervalsWorkoutCompiler.Compile(intervals);
        Assert.That(compiledIntervals.TotalDurationSeconds, Is.EqualTo(2400));
        Assert.That(compiledIntervals.DslText, Does.Contain("10x\n- 1m 05:10-05:25/km Pace\n- 90s Recovery Z1 HR"));

        var progressive = new WorkoutDefinition
        {
            Title = "Progressive Threshold",
            SessionType = "progressive_threshold",
            Steps =
            [
                new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(2) },
                new() { Kind = "steady", DurationSeconds = 1200, Target = HrZone(3) },
                new() { Kind = "steady", DurationSeconds = 600, Target = HrZone(4) },
                new() { Kind = "cooldown", DurationSeconds = 300, Target = HrZone(1) }
            ]
        };

        var compiledProgressive = IntervalsWorkoutCompiler.Compile(progressive);
        Assert.That(compiledProgressive.DslText, Does.Contain("Main Set\n- 20m Z3 HR\n- 10m Z4 HR"));

        var micro = new WorkoutDefinition
        {
            Title = "Micro Threshold 5x4m",
            SessionType = "micro_threshold",
            Steps =
            [
                new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(1, 2) },
                new()
                {
                    Kind = "repeat",
                    Reps = 5,
                    Work = new() { DurationSeconds = 240, Target = HrZone(4) },
                    Recovery = new() { DurationSeconds = 90, Target = HrZone(1), Cue = "Zügig gehen oder locker traben" }
                },
                new() { Kind = "cooldown", DurationSeconds = 300, Target = HrZone(1) }
            ]
        };

        var compiledMicro = IntervalsWorkoutCompiler.Compile(micro);
        Assert.That(compiledMicro.DslText, Does.Contain("- 90s Recovery Z1 HR \"Zügig gehen oder locker traben\""));

        var recovery = new WorkoutDefinition
        {
            Title = "Recovery Run 30m",
            SessionType = "recovery_run",
            Steps = [new() { Kind = "steady", DurationSeconds = 1800, Target = HrZone(1) }]
        };

        var compiledRecovery = IntervalsWorkoutCompiler.Compile(recovery);
        Assert.That(compiledRecovery.DslText, Is.EqualTo("Recovery Run\n- 30m Z1 HR\n"));
    }

    [Test]
    public void ValidatorRejectsModalityViolationsAndTextPaceLeaks()
    {
        var easyWithPace = new WorkoutDefinition
        {
            Title = "Easy Run",
            SessionType = "easy_run",
            Steps = [new() { Kind = "steady", DurationSeconds = 2400, Target = PaceRange(390, 410) }]
        };
        Assert.That(
            WorkoutModalityValidator.ValidateWorkout(easyWithPace).Errors.Select(e => e.Code),
            Does.Contain("aerobic_session_forbids_non_hr_or_high_zone"));

        var easyWithTextLeak = new WorkoutDefinition
        {
            Title = "Easy Run",
            SessionType = "easy_run",
            CoachingNotes = "Aim for roughly 06:45/km effort.",
            Steps = [new() { Kind = "steady", DurationSeconds = 2400, Target = HrZone(2) }]
        };
        Assert.That(
            WorkoutModalityValidator.ValidateWorkout(easyWithTextLeak).Errors.Select(e => e.Code),
            Does.Contain("aerobic_session_pace_leak_in_text"));

        var shortHrInterval = new WorkoutDefinition
        {
            Title = "Short Speed",
            SessionType = "intervals",
            Steps =
            [
                new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(1, 2) },
                new()
                {
                    Kind = "repeat",
                    Reps = 8,
                    Work = new() { DurationSeconds = 60, Target = HrZone(5) },
                    Recovery = new() { DurationSeconds = 60, Target = HrZone(1) }
                },
                new() { Kind = "cooldown", DurationSeconds = 300, Target = HrZone(1) }
            ]
        };
        Assert.That(
            WorkoutModalityValidator.ValidateWorkout(shortHrInterval).Errors.Select(e => e.Code),
            Does.Contain("short_interval_forbids_hr_target"));

        var mixedTarget = new WorkoutDefinition
        {
            Title = "Mixed Target",
            SessionType = "tempo_run",
            Steps =
            [
                new()
                {
                    Kind = "steady",
                    DurationSeconds = 1800,
                    Target = new()
                    {
                        Modality = "hr_zone",
                        HrZoneStart = 4,
                        PaceFastSecPerKm = 340,
                        PaceSlowSecPerKm = 360
                    }
                }
            ]
        };
        Assert.That(
            WorkoutModalityValidator.ValidateWorkout(mixedTarget).Errors.Select(e => e.Code),
            Does.Contain("mixed_modality_target"));

        var hotWarmup = new WorkoutDefinition
        {
            Title = "Hot Warmup",
            SessionType = "tempo_run",
            Steps =
            [
                new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(3) },
                new() { Kind = "steady", DurationSeconds = 1200, Target = HrZone(4) }
            ]
        };
        Assert.That(
            WorkoutModalityValidator.ValidateWorkout(hotWarmup).Errors.Select(e => e.Code),
            Does.Contain("warmup_cooldown_must_be_low_hr"));

        var z2RecoveryRun = new WorkoutDefinition
        {
            Title = "Too Hard Recovery",
            SessionType = "recovery_run",
            Steps = [new() { Kind = "steady", DurationSeconds = 1800, Target = HrZone(2) }]
        };
        Assert.That(
            WorkoutModalityValidator.ValidateWorkout(z2RecoveryRun).Errors.Select(e => e.Code),
            Does.Contain("aerobic_session_forbids_non_hr_or_high_zone"));

        var tooShort = new WorkoutDefinition
        {
            Title = "Tiny Run",
            SessionType = "easy_run",
            Steps = [new() { Kind = "steady", DurationSeconds = 300, Target = HrZone(2) }]
        };
        Assert.That(
            WorkoutModalityValidator.ValidateWorkout(tooShort).Errors.Select(e => e.Code),
            Does.Contain("workout_duration_out_of_bounds"));
    }

    [Test]
    public void ValidatorEnforcesDualOptionMedicalAndCalendarSafetyGates()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var berniData = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);
        var dossier = CoachIntelligenceDossierBuilder.Build(
            berniData.Profile,
            berniData.Activities,
            berniData.WellnessRecords,
            berniData.RunReviews,
            berniData.CalendarEvents,
            refDate);

        var easyWorkout = new WorkoutDefinition
        {
            Title = "Easy 45m Z2",
            SessionType = "easy_run",
            Steps =
            [
                new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(1) },
                new() { Kind = "steady", DurationSeconds = 1800, Target = HrZone(2) },
                new() { Kind = "cooldown", DurationSeconds = 300, Target = HrZone(1) }
            ]
        };

        var thresholdWorkout = new WorkoutDefinition
        {
            Title = "Threshold 3x6m",
            SessionType = "threshold_intervals",
            Steps =
            [
                new() { Kind = "warmup", DurationSeconds = 600, Target = HrZone(1, 2) },
                new()
                {
                    Kind = "repeat",
                    Reps = 3,
                    Work = new() { DurationSeconds = 360, Target = HrZone(4) },
                    Recovery = new() { DurationSeconds = 120, Target = HrZone(1) }
                },
                new() { Kind = "cooldown", DurationSeconds = 300, Target = HrZone(1) }
            ]
        };

        // Valid dual option pairing quality + base
        var validDual = new CoachPlanDecision
        {
            Action = "dual_option",
            TargetDate = refDate,
            PrimaryWorkout = thresholdWorkout,
            SecondaryWorkout = easyWorkout,
            Rationale = DefaultRationale(),
            UpdatedCompass = DefaultCompass(refDate)
        };
        Assert.That(WorkoutModalityValidator.ValidateDecision(validDual, dossier).IsValid, Is.True);

        // Invalid dual option pairing two easy runs
        var invalidDual = validDual with { PrimaryWorkout = easyWorkout };
        Assert.That(
            WorkoutModalityValidator.ValidateDecision(invalidDual, dossier).Errors.Select(e => e.Code),
            Does.Contain("dual_option_must_pair_quality_with_base"));

        // keep_existing valid on 2026-11-25 (berni has Slot 2A & 2B), invalid on 2026-11-26 (no scheduled event)
        var keepExistingToday = new CoachPlanDecision
        {
            Action = "keep_existing",
            TargetDate = refDate,
            KeepExistingReason = "Planned workouts remain appropriate.",
            Rationale = DefaultRationale(),
            UpdatedCompass = DefaultCompass(refDate)
        };
        Assert.That(WorkoutModalityValidator.ValidateDecision(keepExistingToday, dossier).IsValid, Is.True);

        var tomorrow = refDate.AddDays(1);
        var tomorrowDossier = CoachIntelligenceDossierBuilder.Build(
            berniData.Profile,
            berniData.Activities,
            berniData.WellnessRecords,
            berniData.RunReviews,
            berniData.CalendarEvents,
            refDate,
            tomorrow);
        var keepExistingEmpty = keepExistingToday with { TargetDate = tomorrow };
        Assert.That(
            WorkoutModalityValidator.ValidateDecision(keepExistingEmpty, tomorrowDossier).Errors.Select(e => e.Code),
            Does.Contain("keep_existing_without_scheduled_event"));

        // Medical red flag blocks quality and long_run, allows rest_day & recovery_run
        var redFlagWellness = new List<IntervalsWellnessEntry>(berniData.WellnessRecords)
        {
            new()
            {
                Id = refDate,
                Updated = DateTimeOffset.UtcNow,
                RestingHr = 55,
                Comments = "Fieber und Erkältung",
                TempWeight = false,
                TempRestingHr = false
            }
        };
        var sickDossier = CoachIntelligenceDossierBuilder.Build(
            berniData.Profile,
            berniData.Activities,
            redFlagWellness,
            berniData.RunReviews,
            berniData.CalendarEvents,
            refDate);

        var sickQuality = new CoachPlanDecision
        {
            Action = "single_workout",
            TargetDate = refDate,
            PrimaryWorkout = thresholdWorkout,
            Rationale = DefaultRationale(),
            UpdatedCompass = DefaultCompass(refDate)
        };
        Assert.That(
            WorkoutModalityValidator.ValidateDecision(sickQuality, sickDossier).Errors.Select(e => e.Code),
            Does.Contain("medical_red_flag_blocks_intensity"));

        var sickRest = new CoachPlanDecision
        {
            Action = "rest_day",
            TargetDate = refDate,
            RestDayReason = "Acute fever and elevated RHR require complete rest.",
            Rationale = DefaultRationale(),
            UpdatedCompass = DefaultCompass(refDate)
        };
        Assert.That(WorkoutModalityValidator.ValidateDecision(sickRest, sickDossier).IsValid, Is.True);

        // Completed run on target date blocks single_workout
        var completedEvents = new List<CalendarEventEntry>
        {
            new()
            {
                Id = 999,
                AthleteId = berniData.Profile.Id,
                Category = "WORKOUT",
                StartDateLocal = refDate.ToDateTime(new TimeOnly(8, 0)),
                Name = "Morning Run",
                PairedActivityId = "i999"
            }
        };
        var completedDossier = CoachIntelligenceDossierBuilder.Build(
            berniData.Profile,
            berniData.Activities,
            berniData.WellnessRecords,
            berniData.RunReviews,
            completedEvents,
            refDate);
        Assert.That(
            WorkoutModalityValidator.ValidateDecision(sickQuality, completedDossier).Errors.Select(e => e.Code),
            Does.Contain("completed_run_on_target_date"));
    }

    [Test]
    public void StrictJsonDeserializesCoachPlanDecisionRegardlessOfPropertyOrderAndRejectsUnknownFields()
    {
        const string validJson = """
            {
              "updated_compass": {
                "last_decision_summary": "Easy 45m Z2",
                "observed_athlete_patterns": ["Steady cadence around 151 spm"],
                "quality_milestone": "Wait for 2 clean base runs",
                "base_milestone": "45m -> 50m Z2",
                "current_block_focus": "Re-entry base consolidation",
                "updated_at": "2026-11-25"
              },
              "rationale": {
                "rejected_alternative": "Threshold intervals deferred",
                "progression_intent": "Second post-break aerobic anchor",
                "tradeoff_weighed": "Fresh TSB vs 10d back break",
                "observations": ["1 clean comeback run completed"]
              },
              "primary_workout": {
                "coaching_notes": "Use brisk walking breaks on hills if HR nears 159 bpm.",
                "steps": [
                  { "target": { "hr_zone_start": 1, "modality": "hr_zone" }, "duration_seconds": 600, "kind": "warmup" },
                  { "target": { "hr_zone_start": 2, "modality": "hr_zone" }, "duration_seconds": 1800, "kind": "steady" },
                  { "target": { "hr_zone_start": 1, "modality": "hr_zone" }, "duration_seconds": 300, "kind": "cooldown" }
                ],
                "session_type": "easy_run",
                "title": "Re-Entry Aerobic Base 45m"
              },
              "target_date": "2026-11-25",
              "action": "single_workout"
            }
            """;

        var decision = StrictJsonLoader.Deserialize<CoachPlanDecision>(validJson);
        Assert.That(decision.Action, Is.EqualTo("single_workout"));
        Assert.That(decision.PrimaryWorkout, Is.Not.Null);
        Assert.That(WorkoutModalityValidator.ValidateDecision(decision).IsValid, Is.True);

        var unknownFieldJson = validJson.Replace("\"action\": \"single_workout\"", "\"extra_drift\": 1, \"action\": \"single_workout\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => StrictJsonLoader.Deserialize<CoachPlanDecision>(unknownFieldJson));
    }
}
