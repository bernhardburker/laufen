using System.Diagnostics;
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
        Assert.That(snapshot.AllRuns, Has.Count.EqualTo(snapshot.TotalRuns));
        Assert.That(html, Does.StartWith("<!DOCTYPE html>"));
        Assert.That(html, Does.Contain("<html lang=\"de\">"));
        Assert.That(html, Does.Contain("<svg"));
        Assert.That(html, Does.Contain("Kalibrierte Herzfrequenzzonen"));
        Assert.That(html, Does.Contain("data-range=\"12w\""));
        Assert.That(html, Does.Contain("data-range=\"4w\""));
        Assert.That(html, Does.Contain("data-range=\"custom\""));
        Assert.That(html, Does.Contain("id=\"zone-filter\""));
        Assert.That(html, Does.Contain("id=\"chart-tooltip\""));
        Assert.That(html, Does.Contain("id=\"dashboard-data\""));
        Assert.That(html, Does.Contain("id=\"chart-weekly-volume\""));
        Assert.That(html, Does.Contain("id=\"chart-zone-dist\""));
        Assert.That(html, Does.Contain("id=\"chart-form-history\""));
        Assert.That(html, Does.Contain("id=\"chart-ef-pace\""));
        Assert.That(html, Does.Contain("id=\"chart-zone-pace\""));
        Assert.That(html, Does.Contain("class=\"table-wrap\""));
        Assert.That(html, Does.Contain("min-width: 0"));
        Assert.That(html, Does.Contain("minmax(min(100%,"));
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

        var scheduleStart = safeHtml.IndexOf("14-Tage Autopilot", StringComparison.Ordinal);
        var scheduleEnd = safeHtml.IndexOf("14-Tage Banister", StringComparison.Ordinal);
        Assert.That(scheduleStart, Is.GreaterThan(0));
        Assert.That(scheduleEnd, Is.GreaterThan(scheduleStart));
        var scheduleHtml = safeHtml[scheduleStart..scheduleEnd];
        Assert.That(scheduleHtml, Does.Contain("Easy Z2 &lt;img src=x&gt;"));
        Assert.That(scheduleHtml, Does.Not.Contain("<img src=x>"));
        Assert.That(scheduleHtml, Does.Not.Contain("06:45/km"));

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

    [Test]
    public void RacePredictionsAndVolumeForecastMatchPythonForecastOnRealBerniData()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var berni = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var validMetrics = PhysiologicalCalculator.FilterValidRuns(berni.Activities)
            .OrderByDescending(r => r.StartDateLocal)
            .Select(PhysiologicalCalculator.CalculateRunMetrics)
            .ToList();
        var weeklyTrends = PhysiologicalCalculator.AggregateWeeklyTrends(berni.Activities, maxWeeks: 12);

        var dnPredictions = RacePredictionCalculator.Predict(validMetrics, berni.Profile);
        var dnForecast = VolumeForecastCalculator.Generate(weeklyTrends, new DateOnly(2026, 11, 26), weeks: 4);

        var pythonExec = Path.Combine(layout.RootDirectory, ".venv", "bin", "python");
        Assert.That(File.Exists(pythonExec), Is.True, $"Python executable not found at {pythonExec}");

        const string script = """
            import json, sys
            from pathlib import Path
            from src.analysis.trends import load_activities, filter_runs, calculate_run_metrics, aggregate_weekly_trends
            from src.analysis.forecast import predict_race_times, generate_volume_forecast

            athlete_dir = Path(sys.argv[1])
            activities = load_activities(athlete_dir / "intervals_activities.json")
            with open(athlete_dir / "athlete.json", "r", encoding="utf-8") as f:
                profile = json.load(f)
            processed = [calculate_run_metrics(r) for r in filter_runs(activities)]
            trends = aggregate_weekly_trends(activities, max_weeks=12)
            payload = {
                "predictions": predict_race_times(processed, profile),
                "volume_forecast": generate_volume_forecast(trends, num_weeks=4),
            }
            print(json.dumps(payload))
            """;

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExec,
            WorkingDirectory = layout.RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(Path.Combine(layout.AthletesDirectory, "berni"));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Python process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.That(process.ExitCode, Is.EqualTo(0), $"Python exited with {process.ExitCode}: {stderr}");
        using var pyDoc = JsonDocument.Parse(stdout);
        var pyPreds = pyDoc.RootElement.GetProperty("predictions");
        var pyForecast = pyDoc.RootElement.GetProperty("volume_forecast");

        Assert.That(dnPredictions.Count, Is.EqualTo(pyPreds.GetArrayLength()));
        for (var i = 0; i < dnPredictions.Count; i++)
        {
            var dn = dnPredictions[i];
            var py = pyPreds[i];
            Assert.That(dn.Key, Is.EqualTo(py.GetProperty("key").GetString()));
            Assert.That(dn.PredictedSeconds, Is.EqualTo(py.GetProperty("predicted_seconds").GetInt32()));
            Assert.That(dn.PredictedTimeText, Is.EqualTo(py.GetProperty("predicted_time_str").GetString()));
            Assert.That(dn.TargetPaceText, Is.EqualTo(py.GetProperty("target_pace_str").GetString()));
            Assert.That(dn.Basis, Is.EqualTo(py.GetProperty("basis").GetString()));
        }

        Assert.That(dnForecast.Count, Is.EqualTo(pyForecast.GetArrayLength()));
        for (var i = 0; i < dnForecast.Count; i++)
        {
            var dn = dnForecast[i];
            var py = pyForecast[i];
            Assert.That(dn.IsoWeek, Is.EqualTo(py.GetProperty("iso_week").GetString()));
            Assert.That(dn.ProjectedDistanceKm, Is.EqualTo(py.GetProperty("projected_distance_km").GetDouble()).Within(0.05));
            Assert.That(dn.ProjectedLoad, Is.EqualTo(py.GetProperty("projected_load").GetDouble()).Within(0.5));
        }
    }
}
