using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class AutopilotSimulationTests
{
    private static WorkspaceLayout ResolveRepositoryLayout() =>
        WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);

    [TestCase(AutopilotScenarioCatalog.MissedWorkdaysAutoRollover)]
    [TestCase(AutopilotScenarioCatalog.SpontaneousOnWatchWorkoutSwap)]
    [TestCase(AutopilotScenarioCatalog.IllnessSickholdAndReentry)]
    [TestCase(AutopilotScenarioCatalog.TelegramUserLockedOverride)]
    public async Task AllBuiltInMultiDayScenariosPassWithZeroAuditFailures(string scenarioName)
    {
        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var scenario = AutopilotScenarioCatalog.GetScenario(scenarioName);
        var strategy = new RollingAutopilotStrategy(new SimulationStubLlmClient());

        var result = await AutopilotSimulator.RunAsync(scenario, profile, "berni", "stub", strategy);

        Assert.That(result.Passed, Is.True,
            string.Join("; ", result.AllFindings.Select(f => $"{f.AuditCode}@Day+{f.DayOffset}: {f.Message}")));
        Assert.That(result.Steps, Has.Count.EqualTo(scenario.Steps.Count));
        Assert.That(result.AllFindings.Where(f => f.Severity == "fail"), Is.Empty);
    }

    [Test]
    public async Task IllnessSickholdScenarioTransitionsThroughReEntryBeforeUnlockingNormal()
    {
        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var scenario = AutopilotScenarioCatalog.GetScenario(AutopilotScenarioCatalog.IllnessSickholdAndReentry);
        var strategy = new RollingAutopilotStrategy(new SimulationStubLlmClient());

        var result = await AutopilotSimulator.RunAsync(scenario, profile, "berni", "stub", strategy);

        Assert.That(result.Passed, Is.True);
        Assert.That(result.Steps[0].HealthStateAfter, Is.EqualTo("SickHold"));
        Assert.That(result.Steps[0].ActiveAutoEventIds, Is.Empty);
        Assert.That(result.Steps[1].HealthStateAfter, Is.EqualTo("SickHold"));
        Assert.That(result.Steps[2].HealthStateAfter, Is.EqualTo("ReEntry"));
        Assert.That(result.Steps[3].HealthStateAfter, Is.EqualTo("ReEntry"));
        Assert.That(result.Steps[4].HealthStateAfter, Is.EqualTo("Normal"));
    }

    [Test]
    public async Task TelegramLockScenarioPreservesLockedSessionsUntilOverride()
    {
        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var strategy = new RollingAutopilotStrategy(new SimulationStubLlmClient());

        var lockResult = await AutopilotSimulator.RunAsync(
            AutopilotScenarioCatalog.GetScenario(AutopilotScenarioCatalog.TelegramUserLockedOverride),
            profile,
            "berni",
            "stub",
            strategy);

        Assert.That(lockResult.Passed, Is.True);
        Assert.That(lockResult.Steps[0].Outcome.ReconciledSchedule!.DeletedEventIds, Does.Not.Contain(810001L));
        Assert.That(lockResult.Steps[0].Outcome.ReconciledSchedule!.DeletedEventIds, Does.Not.Contain(810002L));
        Assert.That(lockResult.Steps[1].Outcome.ReconciledSchedule!.DeletedEventIds, Does.Not.Contain(810001L));
        Assert.That(lockResult.Steps[2].Outcome.ReconciledSchedule!.DeletedEventIds, Does.Not.Contain(810002L));
        Assert.That(lockResult.Steps[3].Outcome.ReconciledSchedule!.DeletedEventIds, Does.Contain(810002L));
        Assert.That(lockResult.Steps[3].Outcome.ReconciledSchedule!.CompiledUpserts.Any(u => u.Date == new DateOnly(2026, 12, 3)), Is.False);
    }

    [Test]
    public void AuditorDetectsIllnessNormalStateAndFailedOutcome()
    {
        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var refDate = new DateOnly(2026, 12, 1);

        var dossier = CoachIntelligenceDossierBuilder.Build(profile, [], [], null, null, refDate);
        var paceWorkout = new WorkoutDefinition
        {
            Title = "Speed 5x60s",
            SessionType = "intervals",
            Steps =
            [
                new() { Kind = "warmup", DurationSeconds = 600, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } },
                new()
                {
                    Kind = "repeat",
                    Reps = 5,
                    Work = new() { DurationSeconds = 60, Target = new() { Modality = "pace_range", PaceFastSecPerKm = 300, PaceSlowSecPerKm = 320 } },
                    Recovery = new() { DurationSeconds = 120, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } }
                },
                new() { Kind = "cooldown", DurationSeconds = 300, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } }
            ]
        };

        var badDecision = new AutopilotPlanDecision
        {
            Trigger = "morning_sync",
            ReferenceDate = refDate,
            HorizonDays = 14,
            HealthState = "Normal",
            Operations = [new() { Date = refDate, Operation = "upsert", Workout = paceWorkout }],
            Rationale = new()
            {
                Observations = ["Illness ignored"],
                TradeoffWeighed = "None",
                ProgressionIntent = "Force speed",
                RejectedAlternative = "None"
            },
            UpdatedCompass = new()
            {
                UpdatedAt = refDate,
                CurrentBlockFocus = "Speed",
                BaseMilestone = "Base",
                QualityMilestone = "Speed",
                ObservedAthletePatterns = [],
                LastDecisionSummary = "Forced intervals while sick",
                HealthState = "Normal"
            }
        };

        var reconciled = AutopilotScheduleGatekeeper.Reconcile(badDecision, dossier);
        var step = new SimulatedTriggerStep
        {
            DayOffset = 0,
            Trigger = "telegram_override",
            TimeOfDay = new TimeOnly(8, 0),
            AthleteMessage = "Bin krank mit Fieber"
        };

        var outcome = new CoachPlanningOutcome
        {
            Success = true,
            ModelUsed = "test",
            Decision = badDecision,
            ReconciledSchedule = reconciled,
            Attempts = [],
            TraceEntry = new AutopilotDecisionTraceEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                AthleteId = profile.Id,
                ReferenceDate = refDate,
                Trigger = "telegram_override",
                Success = true,
                AttemptCount = 1,
                UpsertCount = 1,
                DeleteCount = 0,
                WatchWindowDates = [refDate],
                PreviewWindowDates = [],
                ValidationErrors = []
            }
        };

        var findings = AutopilotSimulationAuditor.AuditStep(0, dossier, step, "Normal", outcome, [], []);
        Assert.That(findings.Select(f => f.AuditCode), Does.Contain("AUDIT-03"));

        var failedOutcome = outcome with { Success = false, FailureReason = "Timeout", Decision = null, ReconciledSchedule = null };
        var failFindings = AutopilotSimulationAuditor.AuditStep(0, dossier, step, "Normal", failedOutcome, [], []);
        Assert.That(failFindings.Select(f => f.AuditCode), Does.Contain("AUDIT-01"));
    }

    [Test]
    public async Task SimulationReportWriterFormatsMarkdownWithoutAbsoluteLocalPaths()
    {
        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var scenario = AutopilotScenarioCatalog.GetScenario(AutopilotScenarioCatalog.TelegramUserLockedOverride);
        var result = await AutopilotSimulator.RunAsync(scenario, profile, "berni", "stub", new RollingAutopilotStrategy(new SimulationStubLlmClient()));

        var markdown = SimulationReportWriter.FormatMarkdown(result);
        Assert.That(markdown, Does.Contain("# Simulation Report: `telegram_user_locked_override`"));
        Assert.That(markdown, Does.Contain("PASS (0 failures)"));
        Assert.That(markdown, Does.Contain("Chronological Step Timeline"));
        Assert.That(markdown, Does.Not.Contain("/home/"));
    }
}
