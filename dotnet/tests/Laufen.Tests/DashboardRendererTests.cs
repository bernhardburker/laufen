using System.Text.Json;
using Laufen.Cli;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class DashboardRendererTests
{
    private static readonly string[] ExpectedIsoWeeks = ["2026-W53", "2027-W01", "2027-W02", "2027-W03"];

    [Test]
    public void RacePredictionCalculatorProducesMonotonicTimesAndHandlesEmptyFallback()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var registry = AthleteDataRepository.LoadRegistry(layout);

        var berni = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var validMetrics = PhysiologicalCalculator.FilterValidRuns(berni.Activities)
            .Select(PhysiologicalCalculator.CalculateRunMetrics)
            .ToList();

        var berniPreds = RacePredictionCalculator.Predict(validMetrics, berni.Profile);
        Assert.That(berniPreds, Has.Count.EqualTo(4));
        Assert.That(berniPreds[0].PredictedSeconds, Is.LessThan(berniPreds[1].PredictedSeconds));
        Assert.That(berniPreds[1].PredictedSeconds, Is.LessThan(berniPreds[2].PredictedSeconds));
        Assert.That(berniPreds[2].PredictedSeconds, Is.LessThan(berniPreds[3].PredictedSeconds));

        var berniTest = AthleteDataRepository.LoadAthleteData(layout, registry, "berni-test");
        var emptyPreds = RacePredictionCalculator.Predict([], berniTest.Profile);
        Assert.That(emptyPreds, Has.Count.EqualTo(4));
        Assert.That(emptyPreds[0].Basis, Does.Contain("Profil-Richtwert"));
        Assert.That(emptyPreds[0].PredictedSeconds, Is.GreaterThan(0));
    }

    [Test]
    public void VolumeForecastCalculatorHandlesIsoWeek53BoundaryWithoutSkippingWeek()
    {
        var trends = new List<WeeklyTrendSummary>
        {
            new()
            {
                IsoWeek = "2026-W52",
                RunsCount = 3,
                TotalDistanceKm = 24.0,
                LongestRunKm = 10.0,
                TotalTimeSeconds = 9600,
                TotalLoad = 180.0,
                AveragePaceSecondsPerKm = 400.0,
                AverageHeartRate = 148.0,
                Z1Z2Percentage = 85.0,
                AerobicEfficiencyFactor = 1.65,
                BaseAerobicEfficiencyFactor = 1.65,
                BaseAveragePaceSecondsPerKm = 400.0,
                BaseRunsCount = 3,
                ZoneBreakdownSeconds = [2000, 6200, 1400, 0, 0],
                ZoneBreakdownPercentages = [20.8, 64.6, 14.6, 0.0, 0.0]
            }
        };

        var forecast = VolumeForecastCalculator.Generate(trends, new DateOnly(2026, 12, 24), weeks: 4);
        Assert.That(forecast.Select(w => w.IsoWeek).ToList(), Is.EqualTo(ExpectedIsoWeeks));
    }

    [Test]
    public void DashboardSnapshotAndHtmlRendererRenderBerniAndBerniTestWithXssAndModalitySafety()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var registry = AthleteDataRepository.LoadRegistry(layout);

        var berni = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);
        var snapshot = DashboardSnapshotBuilder.Build(
            "berni",
            berni.RegistryEntry.DashboardSlug,
            berni.Profile,
            berni.Activities,
            berni.WellnessRecords,
            berni.RunReviews,
            berni.CalendarEvents,
            refDate);

        var html = DashboardHtmlRenderer.Render(snapshot);
        Assert.That(html, Does.StartWith("<!DOCTYPE html>"));
        Assert.That(html, Does.Contain("<html lang=\"de\">"));
        Assert.That(html, Does.Contain("<svg"));
        Assert.That(html, Does.Contain("Kalibrierte Herzfrequenzzonen"));
        Assert.That(html, Does.Not.Contain("cdn.jsdelivr.net"));
        Assert.That(html, Does.Not.Contain("tag-amber"));

        // Verify XSS escaping & aerobic pace scrubbing on planned easy workout
        var injectedProfile = berni.Profile with { DisplayName = "<script>alert(1)</script>" };
        var leakedEvent = new CalendarEventEntry
        {
            Id = 999999,
            AthleteId = berni.Profile.Id,
            Category = "WORKOUT",
            StartDateLocal = new DateTime(2026, 11, 26, 7, 0, 0, DateTimeKind.Unspecified),
            Name = "Easy Z2 06:45/km <img src=x>",
            ExternalId = "laufen-auto-2026-11-26"
        };
        var safeSnapshot = DashboardSnapshotBuilder.Build(
            "berni",
            "berni",
            injectedProfile,
            berni.Activities,
            berni.WellnessRecords,
            berni.RunReviews,
            [leakedEvent],
            refDate);
        var safeHtml = DashboardHtmlRenderer.Render(safeSnapshot);
        Assert.That(safeHtml, Does.Not.Contain("<script>alert(1)</script>"));
        Assert.That(safeHtml, Does.Contain("&lt;script&gt;alert(1)&lt;/script&gt;"));
        Assert.That(safeHtml, Does.Not.Contain("06:45/km"));

        // Verify graceful empty state on berni-test
        var berniTest = AthleteDataRepository.LoadAthleteData(layout, registry, "berni-test");
        var emptySnapshot = DashboardSnapshotBuilder.Build(
            "berni-test",
            berniTest.RegistryEntry.DashboardSlug,
            berniTest.Profile,
            berniTest.Activities,
            berniTest.WellnessRecords,
            null,
            null,
            refDate);
        var emptyHtml = DashboardHtmlRenderer.Render(emptySnapshot);
        Assert.That(emptyHtml, Does.Contain("Keine exportierten Laufaktivitaeten"));
        Assert.That(emptyHtml, Does.Contain("Keine Laufaktivitaeten gefunden."));
    }

    [Test]
    public void DashboardCliWritesIsolatedHtmlAndSupportsDryRunJson()
    {
        var tempHtml = Path.Combine(Path.GetTempPath(), $"laufen-dash-{Guid.NewGuid():N}.html");
        try
        {
            using var dryOut = new StringWriter();
            using var dryErr = new StringWriter();
            var dryExit = LaufenCliRunner.Run(
                ["dashboard", "--athlete", "berni", "--ref-date", "2026-11-25", "--output", tempHtml, "--dry-run", "--json"],
                dryOut,
                dryErr,
                AppContext.BaseDirectory);

            Assert.That(dryExit, Is.EqualTo(0), dryErr.ToString());
            Assert.That(File.Exists(tempHtml), Is.False);
            using (var doc = JsonDocument.Parse(dryOut.ToString()))
            {
                Assert.That(doc.RootElement.GetProperty("dryRun").GetBoolean(), Is.True);
                Assert.That(doc.RootElement.GetProperty("htmlBytes").GetInt32(), Is.GreaterThan(2000));
            }

            using var writeOut = new StringWriter();
            using var writeErr = new StringWriter();
            var writeExit = LaufenCliRunner.Run(
                ["dashboard", "--athlete", "berni", "--ref-date", "2026-11-25", "--output", tempHtml],
                writeOut,
                writeErr,
                AppContext.BaseDirectory);

            Assert.That(writeExit, Is.EqualTo(0), writeErr.ToString());
            Assert.That(File.Exists(tempHtml), Is.True);
            var content = File.ReadAllText(tempHtml);
            Assert.That(content, Does.Contain("<!DOCTYPE html>"));
            Assert.That(content, Does.Contain("Lauf- &amp; Leistungs-Dashboard"));
        }
        finally
        {
            if (File.Exists(tempHtml))
            {
                File.Delete(tempHtml);
            }
        }
    }
}
