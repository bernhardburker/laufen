using System.Text.Json;
using Laufen.Cli;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class StageMilestonesAndTraceTests
{
    [Test]
    public void StageMilestoneEvaluatorEvaluatesRealBerniMilestonesAndNorthStar()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);

        var dossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate);

        Assert.That(dossier.Milestones, Is.Not.Null);
        var ms = dossier.Milestones!;
        Assert.That(ms.ActiveMilestones, Has.Count.EqualTo(3));
        Assert.That(ms.ActiveMilestones.Select(m => m.Category).ToList(), Does.Contain("aerobic_durability"));
        Assert.That(ms.ActiveMilestones.Select(m => m.Category).ToList(), Does.Contain("threshold_capacity"));
        Assert.That(ms.ActiveMilestones.Select(m => m.Category).ToList(), Does.Contain("consistency_budget"));
        Assert.That(ms.NorthStar.WindowWeeks, Is.EqualTo(12));
        Assert.That(ms.NorthStar.Z2GapSampleCount, Is.GreaterThanOrEqualTo(1));

        var html = DashboardHtmlRenderer.Render(DashboardSnapshotBuilder.Build(
            "berni",
            data.RegistryEntry.DashboardSlug,
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate));
        Assert.That(html, Does.Contain("14-Tage-Etappenziele"));
        Assert.That(html, Does.Contain("Nordstern-Trend"));
    }

    [Test]
    public void StageMilestoneEvaluatorPausesThresholdUnderSickHoldAndReEntry()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);

        var reentryCompass = new BlockCompassSnapshot
        {
            UpdatedAt = refDate,
            CurrentBlockFocus = "Re-entry",
            BaseMilestone = "2x clean Z2",
            QualityMilestone = "Paused",
            ObservedAthletePatterns = [],
            LastDecisionSummary = "Re-entry",
            HealthState = "ReEntry"
        };

        var reentryDossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            currentCompass: reentryCompass);

        var thresholdMs = reentryDossier.Milestones!.ActiveMilestones.First(m => m.Category == "threshold_capacity");
        Assert.That(thresholdMs.Status, Is.EqualTo("paused_health"));

        var sickCompass = reentryCompass with { HealthState = "SickHold" };
        var sickEval = StageMilestoneEvaluator.Evaluate(reentryDossier, data.RunReviews, sickCompass);
        Assert.That(sickEval.ActiveMilestones.All(m => m.Status == "paused_health"), Is.True);
    }

    [Test]
    public void TraceStorePresenterAndCliSupportFilteringMarkdownAndJson()
    {
        var tempRoot = CreateTempWorkspaceCopy("berni");
        try
        {
            var layout = WorkspaceLayout.FromRoot(tempRoot);
            var t1 = new AutopilotDecisionTraceEntry
            {
                Timestamp = new DateTimeOffset(2026, 11, 24, 6, 30, 0, TimeSpan.Zero),
                AthleteId = "i697752",
                ReferenceDate = new DateOnly(2026, 11, 24),
                Trigger = "morning_sync",
                Success = true,
                Model = "simulation-stub-v1",
                AttemptCount = 1,
                HealthState = "ReEntry",
                UpsertCount = 2,
                DeleteCount = 1,
                WatchWindowDates = [new DateOnly(2026, 11, 24)],
                PreviewWindowDates = [new DateOnly(2026, 12, 2)],
                ProjectedEndingCtl = 15.2,
                ProjectedEndingAtl = 14.0,
                ProjectedMinTsb = -2.1,
                ValidationErrors = [],
                Rationale = new CoachDecisionRationale
                {
                    Observations = ["Comeback run clean"],
                    TradeoffWeighed = "Base ramp-up over threshold",
                    ProgressionIntent = "Build aerobic continuity",
                    RejectedAlternative = "Skipped intervals"
                }
            };
            var t2 = t1 with
            {
                Timestamp = new DateTimeOffset(2026, 11, 25, 16, 0, 0, TimeSpan.Zero),
                ReferenceDate = new DateOnly(2026, 11, 25),
                Trigger = "post_run"
            };

            AutopilotStateStore.AppendDecisionTrace(layout, "berni", t1);
            AutopilotStateStore.AppendDecisionTrace(layout, "berni", t2);

            var allLoaded = AutopilotStateStore.LoadDecisionTraces(layout, "berni");
            Assert.That(allLoaded, Has.Count.EqualTo(2));
            Assert.That(allLoaded[0].Trigger, Is.EqualTo("post_run"));

            var postOnly = AutopilotStateStore.LoadDecisionTraces(layout, "berni", triggerFilter: "post_run");
            Assert.That(postOnly, Has.Count.EqualTo(1));

            using var jsonOut = new StringWriter();
            using var jsonErr = new StringWriter();
            var jsonExit = LaufenCliRunner.Run(
                ["trace", "--workspace", tempRoot, "--athlete", "berni", "--ref-date", "2026-11-25", "--trigger", "post_run", "--json"],
                jsonOut,
                jsonErr);
            Assert.That(jsonExit, Is.EqualTo(0), jsonErr.ToString());
            using (var doc = JsonDocument.Parse(jsonOut.ToString()))
            {
                Assert.That(doc.RootElement.GetProperty("count").GetInt32(), Is.EqualTo(1));
                Assert.That(doc.RootElement.GetProperty("milestones").GetProperty("active_milestones").GetArrayLength(), Is.EqualTo(3));
            }

            using var mdOut = new StringWriter();
            using var mdErr = new StringWriter();
            var mdExit = LaufenCliRunner.Run(
                ["trace", "--workspace", tempRoot, "--athlete", "berni", "--ref-date", "2026-11-25", "--markdown"],
                mdOut,
                mdErr);
            Assert.That(mdExit, Is.EqualTo(0), mdErr.ToString());
            Assert.That(mdOut.ToString(), Does.Contain("## Decision Trace: `berni`"));
            Assert.That(mdOut.ToString(), Does.Contain("14-Tage Etappenziele"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static string CreateTempWorkspaceCopy(string slug)
    {
        var source = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"laufen-trace-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "athletes", slug));
        File.Copy(Path.Combine(source.AthletesDirectory, "registry.json"), Path.Combine(tempRoot, "athletes", "registry.json"));
        foreach (var file in Directory.GetFiles(Path.Combine(source.AthletesDirectory, slug)))
        {
            File.Copy(file, Path.Combine(tempRoot, "athletes", slug, Path.GetFileName(file)));
        }
        return tempRoot;
    }
}
