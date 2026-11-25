using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

[TestFixture]
public sealed class AutopilotOrchestratorTests
{
    private static readonly DateOnly RefDate = new(2026, 12, 1);

    [Test]
    public void HysteresisSuppressesReplanOnStableMorningSyncWithMinorReadinessJitter()
    {
        var dossier = CreateSyntheticDossier(
            rhrDelta: 1.0,
            hrvRatio: 0.96,
            sleepDelta: -0.4,
            watchWorkoutCount: 3,
            missedAutoCount: 0,
            redFlagCount: 0,
            daysSinceLastRun: 2,
            compassUpdatedAt: RefDate);

        var decision = AutopilotHysteresisEvaluator.Evaluate(dossier, "morning_sync", athleteMessage: null);

        Assert.That(decision.ShouldReplan, Is.False);
        Assert.That(decision.ReasonCode, Is.EqualTo("hysteresis_stable_plan"));
        Assert.That(decision.TriggerReasons, Is.Empty);
    }

    [Test]
    public void HysteresisTriggersOnMeaningfulReadinessScheduleOrEventSignals()
    {
        var baseDossier = CreateSyntheticDossier(
            rhrDelta: 0.0,
            hrvRatio: 1.0,
            sleepDelta: 0.0,
            watchWorkoutCount: 2,
            missedAutoCount: 0,
            redFlagCount: 0,
            daysSinceLastRun: 2,
            compassUpdatedAt: RefDate);

        Assert.That(AutopilotHysteresisEvaluator.Evaluate(baseDossier, "post_run", null).TriggerReasons, Does.Contain("event_trigger:post_run"));
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(baseDossier, "telegram_override", null).TriggerReasons, Does.Contain("event_trigger:telegram_override"));
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(baseDossier, "morning_sync", "Beine schwer").TriggerReasons, Does.Contain("athlete_message"));
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(baseDossier, "morning_sync", null, forceReplan: true).TriggerReasons, Does.Contain("force_replan"));

        var rhrSpike = CreateSyntheticDossier(rhrDelta: 3.0, hrvRatio: 1.0, sleepDelta: 0.0, watchWorkoutCount: 2, missedAutoCount: 0, redFlagCount: 0, daysSinceLastRun: 2, compassUpdatedAt: RefDate);
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(rhrSpike, "morning_sync", null).TriggerReasons, Does.Contain("rhr_spike"));

        var hrvDrop = CreateSyntheticDossier(rhrDelta: 0.0, hrvRatio: 0.84, sleepDelta: 0.0, watchWorkoutCount: 2, missedAutoCount: 0, redFlagCount: 0, daysSinceLastRun: 2, compassUpdatedAt: RefDate);
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(hrvDrop, "morning_sync", null).TriggerReasons, Does.Contain("hrv_drop"));

        var sleepDeficit = CreateSyntheticDossier(rhrDelta: 0.0, hrvRatio: 1.0, sleepDelta: -1.5, watchWorkoutCount: 2, missedAutoCount: 0, redFlagCount: 0, daysSinceLastRun: 2, compassUpdatedAt: RefDate);
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(sleepDeficit, "morning_sync", null).TriggerReasons, Does.Contain("sleep_deficit"));

        var underfilled = CreateSyntheticDossier(rhrDelta: 0.0, hrvRatio: 1.0, sleepDelta: 0.0, watchWorkoutCount: 1, missedAutoCount: 0, redFlagCount: 0, daysSinceLastRun: 2, compassUpdatedAt: RefDate);
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(underfilled, "morning_sync", null).TriggerReasons, Does.Contain("watch_window_underfilled"));

        var missedAuto = CreateSyntheticDossier(rhrDelta: 0.0, hrvRatio: 1.0, sleepDelta: 0.0, watchWorkoutCount: 2, missedAutoCount: 1, redFlagCount: 0, daysSinceLastRun: 2, compassUpdatedAt: RefDate);
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(missedAuto, "morning_sync", null).TriggerReasons, Does.Contain("missed_auto_workouts"));

        var duplicateDay = CreateSyntheticDossier(rhrDelta: 0.0, hrvRatio: 1.0, sleepDelta: 0.0, watchWorkoutCount: 2, missedAutoCount: 0, redFlagCount: 0, daysSinceLastRun: 2, compassUpdatedAt: RefDate, duplicateSameDay: true);
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(duplicateDay, "morning_sync", null).TriggerReasons, Does.Contain("duplicate_scheduled_day"));

        var runAfterCompass = CreateSyntheticDossier(rhrDelta: 0.0, hrvRatio: 1.0, sleepDelta: 0.0, watchWorkoutCount: 2, missedAutoCount: 0, redFlagCount: 0, daysSinceLastRun: 0, compassUpdatedAt: RefDate.AddDays(-1));
        Assert.That(AutopilotHysteresisEvaluator.Evaluate(runAfterCompass, "morning_sync", null).TriggerReasons, Does.Contain("completed_run_after_compass"));
    }

    [Test]
    public async Task CycleOrchestratorSkipsLlmWhenSuppressedAndIsolatesNarratorFailures()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var stableRefDate = new DateOnly(2026, 11, 25);

        var calendarWithWatchWorkouts = new List<CalendarEventEntry>
        {
            new()
            {
                Id = 9901,
                AthleteId = "i697752",
                ExternalId = "laufen-auto-2026-11-26",
                Category = "WORKOUT",
                StartDateLocal = new DateTime(2026, 11, 26, 7, 0, 0),
                Name = "Easy Base 45m",
                Description = "Warmup\n- 10m Z1 HR\n\nMain Set\n- 30m Z2 HR\n\nCooldown\n- 5m Z1 HR\n"
            },
            new()
            {
                Id = 9902,
                AthleteId = "i697752",
                ExternalId = "laufen-auto-2026-11-28",
                Category = "WORKOUT",
                StartDateLocal = new DateTime(2026, 11, 28, 7, 0, 0),
                Name = "Aerober Long Run 60m",
                Description = "Warmup\n- 10m Z1 HR\n\nMain Set\n- 45m Z2 HR\n\nCooldown\n- 5m Z1 HR\n"
            }
        };

        var currentCompass = new BlockCompassSnapshot
        {
            UpdatedAt = stableRefDate,
            CurrentBlockFocus = "Re-entry base build",
            BaseMilestone = "2 clean Z2 runs",
            QualityMilestone = "4x5m Z4",
            ObservedAthletePatterns = [],
            LastDecisionSummary = "Stable week",
            HealthState = "ReEntry"
        };

        var spyPlanner = new SpyPlannerStrategy(new RollingAutopilotStrategy(new SimulationStubLlmClient()));
        var throwingNarrator = new ThrowingNarrator();
        var orchestrator = new AutopilotCycleOrchestrator(spyPlanner, throwingNarrator);

        var suppressedResult = await orchestrator.RunCycleAsync(new AutopilotCycleRequest
        {
            AthleteSlug = "berni",
            RegistryEntry = data.RegistryEntry,
            Profile = data.Profile,
            Activities = data.Activities,
            WellnessRecords = data.WellnessRecords,
            RunReviews = data.RunReviews,
            CalendarEvents = calendarWithWatchWorkouts,
            ReferenceDate = stableRefDate,
            TargetDate = stableRefDate,
            CurrentCompass = currentCompass,
            Trigger = "morning_sync",
            IsDryRun = true
        });

        Assert.That(suppressedResult.Succeeded, Is.True);
        Assert.That(suppressedResult.Hysteresis.ShouldReplan, Is.False);
        Assert.That(spyPlanner.CallCount, Is.EqualTo(0));
        Assert.That(suppressedResult.TelegramPreview, Is.Not.Null);

        var postRunResult = await orchestrator.RunCycleAsync(new AutopilotCycleRequest
        {
            AthleteSlug = "berni",
            RegistryEntry = data.RegistryEntry,
            Profile = data.Profile,
            Activities = data.Activities,
            WellnessRecords = data.WellnessRecords,
            RunReviews = data.RunReviews,
            CalendarEvents = calendarWithWatchWorkouts,
            ReferenceDate = stableRefDate,
            TargetDate = stableRefDate,
            CurrentCompass = currentCompass,
            PreviousHealthState = "ReEntry",
            Trigger = "post_run",
            IsDryRun = true
        });

        Assert.That(postRunResult.Succeeded, Is.True);
        Assert.That(postRunResult.Hysteresis.ShouldReplan, Is.True);
        Assert.That(spyPlanner.CallCount, Is.EqualTo(1));
        Assert.That(postRunResult.CalendarSyncPlan, Is.Not.Null);
        Assert.That(postRunResult.NarrativeGate.ShouldNarrate, Is.True);
        Assert.That(postRunResult.NarrativeError, Does.Contain("narrator offline"));
        Assert.That(postRunResult.TelegramPreview, Is.Not.Null);
    }

    private static CoachIntelligenceDossier CreateSyntheticDossier(
        double rhrDelta,
        double hrvRatio,
        double sleepDelta,
        int watchWorkoutCount,
        int missedAutoCount,
        int redFlagCount,
        int? daysSinceLastRun,
        DateOnly compassUpdatedAt,
        bool duplicateSameDay = false)
    {
        var upcoming7 = new List<ScheduledCalendarItem>();
        for (var i = 0; i < watchWorkoutCount; i++)
        {
            var date = duplicateSameDay ? RefDate.AddDays(1) : RefDate.AddDays(i + 1);
            upcoming7.Add(new ScheduledCalendarItem
            {
                Id = 1000 + i,
                Date = date,
                Category = "WORKOUT",
                Name = $"Easy {i + 1}",
                Ownership = "auto"
            });
        }

        var missed = new List<ScheduledCalendarItem>();
        for (var i = 0; i < missedAutoCount; i++)
        {
            missed.Add(new ScheduledCalendarItem
            {
                Id = 2000 + i,
                Date = RefDate.AddDays(-1 - i),
                Category = "WORKOUT",
                Name = "Missed Easy",
                Ownership = "auto"
            });
        }

        var redFlags = new List<MedicalRedFlag>();
        for (var i = 0; i < redFlagCount; i++)
        {
            redFlags.Add(new MedicalRedFlag { Code = "ILLNESS", Date = RefDate, Detail = "Fever" });
        }

        return new CoachIntelligenceDossier
        {
            ReferenceDate = RefDate,
            TargetDate = RefDate,
            TargetDayOfWeek = "Tuesday",
            Athlete = new AthleteDossierSummary
            {
                Id = "i697752",
                Name = "Berni",
                Lthr = 171,
                ProfileRestingHr = 47
            },
            StimulusProgression = new StimulusProgressionDossier
            {
                Ledger = new RollingStimulusLedger
                {
                    DaysSinceLastRun = daysSinceLastRun,
                    RunsLast7Days = 3,
                    RunsLast14Days = 6,
                    VolumeKmLast7Days = 20,
                    VolumeKmLast14Days = 40,
                    VolumeMinutesLast7Days = 135,
                    VolumeMinutesLast14Days = 270
                },
                BreakReentry = new BreakReentryContext
                {
                    LongestGapDaysLast28Days = 3,
                    HasRecentExtendedBreak = false,
                    RunsCompletedSinceRecentBreak = 5,
                    CleanBaseRunsSinceRecentBreak = 4,
                    RecentContextClues = []
                },
                BaseProgressionChain = [],
                QualityProgressionChain = []
            },
            Readiness = new ReadinessSignalDossier
            {
                IsWellnessStale = false,
                RestingHrDelta = rhrDelta,
                HrvBaselineRatio = hrvRatio,
                SleepHoursDelta = sleepDelta,
                ThreeDayTrend = [],
                LoadDataSource = "activities",
                RecentRunNotes = [],
                MedicalRedFlags = redFlags
            },
            Calendar = new UpcomingCalendarDossier
            {
                EventsOnTargetDate = [],
                UpcomingEventsNext7Days = upcoming7,
                UpcomingEventsNext14Days = upcoming7,
                MissedAutoWorkoutsLast7Days = missed,
                UserLockedDates = [],
                HasCompletedRunOnTargetDate = false,
                HasUncompletedPlannedWorkoutOnTargetDate = false
            },
            CurrentCompass = new BlockCompassSnapshot
            {
                UpdatedAt = compassUpdatedAt,
                CurrentBlockFocus = "Base",
                BaseMilestone = "60m Z2",
                QualityMilestone = "4x5m",
                ObservedAthletePatterns = [],
                LastDecisionSummary = "Stable",
                HealthState = "Normal"
            },
            DataQualityNotes = []
        };
    }

    private sealed class SpyPlannerStrategy(ICoachPlanningStrategy inner) : ICoachPlanningStrategy
    {
        public int CallCount { get; private set; }

        public Task<CoachPlanningOutcome> PlanAsync(CoachPlanningContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return inner.PlanAsync(context, cancellationToken);
        }
    }

    private sealed class ThrowingNarrator : IProgressNarrator
    {
        public Task<NarrativeGenerationOutcome> NarrateAsync(
            NarrativeGenerationContext context,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("narrator offline");
        }
    }
}
