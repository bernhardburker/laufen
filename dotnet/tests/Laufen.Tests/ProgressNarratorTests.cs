using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class ProgressNarratorTests
{
    private static WorkspaceLayout ResolveRepositoryLayout() =>
        WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);

    [Test]
    public void ProgressNarratorGateSuppressesRoutineRolloversAndFiresOnMeaningfulEvents()
    {
        var routineMorning = ProgressNarratorGate.Evaluate("morning_sync", "Normal", "Normal", "Base build", "Base build");
        Assert.That(routineMorning.ShouldNarrate, Is.False);
        Assert.That(routineMorning.TriggerReason, Is.EqualTo("suppressed"));

        var routineIntraday = ProgressNarratorGate.Evaluate("intraday_check", "ReEntry", "ReEntry", "ReEntry", "ReEntry");
        Assert.That(routineIntraday.ShouldNarrate, Is.False);

        var postRun = ProgressNarratorGate.Evaluate("post_run", "Normal", "Normal", "Base build", "Base build");
        Assert.That(postRun.ShouldNarrate, Is.True);
        Assert.That(postRun.TriggerReason, Is.EqualTo("post_run"));

        var healthShift = ProgressNarratorGate.Evaluate("morning_sync", "Normal", "SickHold", "Base build", "Base build");
        Assert.That(healthShift.ShouldNarrate, Is.True);
        Assert.That(healthShift.TriggerReason, Is.EqualTo("health_state_shift"));

        var blockShift = ProgressNarratorGate.Evaluate("morning_sync", "Normal", "Normal", "ReEntry base", "Threshold build");
        Assert.That(blockShift.ShouldNarrate, Is.True);
        Assert.That(blockShift.TriggerReason, Is.EqualTo("block_focus_shift"));

        var manualForce = ProgressNarratorGate.Evaluate("morning_sync", null, null, null, null, forceManual: true);
        Assert.That(manualForce.ShouldNarrate, Is.True);
        Assert.That(manualForce.TriggerReason, Is.EqualTo("manual"));
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void NarrativeBriefingDossierBuilderHandlesRealActiveAndColdStartTenants(string slug)
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
        var refDate = new DateOnly(2026, 11, 25);

        var briefing = NarrativeBriefingDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            "post_run");

        Assert.That(briefing.ReferenceDate, Is.EqualTo(refDate));
        if (slug == "berni")
        {
            Assert.That(briefing.LatestCompletedRun, Is.Not.Null);
            Assert.That(briefing.LatestCompletedRun!.ActivityId, Is.EqualTo("i213105633"));
            Assert.That(briefing.LatestCompletedRun.Date, Is.EqualTo(new DateOnly(2026, 11, 22)));
            Assert.That(briefing.MacroTrends4Weeks, Is.Not.Empty);
            Assert.That(briefing.Zone2GapTrend, Is.Not.Null);
            Assert.That(briefing.Zone2GapTrend!.SampleCount, Is.GreaterThanOrEqualTo(2));
        }
        else
        {
            Assert.That(briefing.LatestCompletedRun, Is.Null);
            Assert.That(briefing.MacroTrends4Weeks, Is.Empty);
            Assert.That(briefing.Zone2GapTrend, Is.Null);
        }
    }

    [Test]
    public void NarrativeBriefingDossierBuilderEnforcesRefDateCutoff()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 11);

        var briefing = NarrativeBriefingDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            "post_run");

        Assert.That(briefing.LatestCompletedRun, Is.Not.Null);
        Assert.That(briefing.LatestCompletedRun!.Date, Is.LessThanOrEqualTo(refDate));
        Assert.That(briefing.LatestCompletedRun.ActivityId, Is.Not.EqualTo("i213105633"));
    }

    [Test]
    public void ProgressNarratorValidatorRejectsCssClassesInvalidSeveritiesAndPrescriptivePaceTargets()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);

        var briefing = NarrativeBriefingDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            "post_run");

        var validWithHistoricalGap = new NarrativeReport
        {
            ReferenceDate = refDate,
            TriggerReason = "post_run",
            StatusTitle = "Solider Wiedereinstieg",
            StatusBadge = "100% Z1/Z2",
            StatusSeverity = "success",
            CommentaryMarkdown = "### Rückblick\nDeine Z2-GAP-Pace liegt aktuell bei 07:58/km – rein als Ergebnis ruhiger Pulssteuerung.",
            Insights =
            [
                new NarrativeInsight
                {
                    Title = "Saubere Herzfrequenz",
                    Severity = "success",
                    Badge = "AEF 0.835",
                    Text = "Keine Pace-Vorgabe nötig; Gehpausen bei Bedarf nutzen."
                }
            ],
            ScheduleHighlights = ["2026-11-28: Aerober Long Run 60m"]
        };

        var validResult = ProgressNarratorValidator.Validate(validWithHistoricalGap, briefing);
        Assert.That(validResult.IsValid, Is.True, string.Join("; ", validResult.Errors.Select(e => e.Message)));

        var leakingReport = validWithHistoricalGap with
        {
            StatusSeverity = "critical",
            CommentaryMarkdown = "Laufe mit 06:45/km im lockeren Lauf. tag-amber",
            Insights =
            [
                new NarrativeInsight
                {
                    Title = "Warnung",
                    Severity = "tag-green",
                    Badge = "tag-cyan",
                    Text = "Zielpace 07:00/km anpeilen."
                }
            ]
        };

        var invalidResult = ProgressNarratorValidator.Validate(leakingReport, briefing);
        Assert.That(invalidResult.IsValid, Is.False);
        var codes = invalidResult.Errors.Select(e => e.Code).ToList();
        Assert.That(codes, Does.Contain("invalid_status_severity"));
        Assert.That(codes, Does.Contain("invalid_insight_severity"));
        Assert.That(codes, Does.Contain("css_class_leak"));
        Assert.That(codes, Does.Contain("prescriptive_aerobic_pace_leak"));
    }

    [Test]
    public async Task LlmProgressNarratorSelfCorrectsInvalidOutputAndPersistsIsolatedArtifacts()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);

        var briefing = NarrativeBriefingDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            "post_run");

        const string badJson = """
            {
              "reference_date": "2026-11-25",
              "trigger_reason": "post_run",
              "status_title": "Test",
              "status_badge": "tag-amber",
              "status_severity": "info",
              "commentary_markdown": "Bitte laufe mit 07:00/km.",
              "insights": [
                { "title": "Pace", "severity": "info", "badge": "tag-green", "text": "Zielpace 07:00/km" }
              ],
              "schedule_highlights": []
            }
            """;

        var stubClient = new NarrativeStubLlmClient();
        var validResponse = await stubClient.GenerateAsync(new LlmRequest(
            ProgressNarratorPromptBuilder.BuildSystemPrompt(),
            ProgressNarratorPromptBuilder.BuildUserPrompt(briefing)));

        var retryingStub = new StubLlmClient([badJson, validResponse.Text]);
        var narrator = new LlmProgressNarrator(retryingStub);

        var outcome = await narrator.NarrateAsync(new NarrativeGenerationContext
        {
            Briefing = briefing,
            MaxValidationRetries = 1
        });

        Assert.That(outcome.Success, Is.True, outcome.FailureReason);
        Assert.That(outcome.Attempts, Has.Count.EqualTo(2));
        Assert.That(outcome.Attempts[0].ValidationErrors, Is.Not.Empty);
        Assert.That(outcome.Attempts[1].ValidationErrors, Is.Empty);
        Assert.That(outcome.Report, Is.Not.Null);

        var tempRoot = Path.Combine(Path.GetTempPath(), $"laufen-narrator-store-{Guid.NewGuid():N}");
        var athletesDir = Path.Combine(tempRoot, "athletes");
        Directory.CreateDirectory(Path.Combine(athletesDir, "berni"));
        var tempLayout = new WorkspaceLayout(tempRoot, athletesDir);
        try
        {
            AutopilotStateStore.SaveNarrativeArtifacts(tempLayout, "berni", outcome.Report!);
            var loaded = AutopilotStateStore.LoadNarrativeSummary(tempLayout, "berni");
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.StatusTitle, Is.EqualTo(outcome.Report.StatusTitle));
            Assert.That(File.Exists(Path.Combine(athletesDir, "berni", "narrative_commentary_dotnet.md")), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
