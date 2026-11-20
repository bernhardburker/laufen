using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class PhysiologicalMetricsDifferentialTests
{
    private static WorkspaceLayout ResolveRepositoryLayout()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "athletes", "registry.json");
            if (File.Exists(candidate))
            {
                return WorkspaceLayout.FromRoot(current.FullName);
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing 'athletes/registry.json'.");
    }

    private static JsonDocument RunPythonScript(WorkspaceLayout layout, params string[] arguments)
    {
        var pythonExec = Path.Combine(layout.RootDirectory, ".venv", "bin", "python");
        Assert.That(File.Exists(pythonExec), Is.True, $"Python executable not found at {pythonExec}");

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExec,
            WorkingDirectory = layout.RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Python process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.That(process.ExitCode, Is.EqualTo(0), $"Python exited with {process.ExitCode}: {stderr}");
        return JsonDocument.Parse(stdout);
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void WeeklyTrendsMatchPythonTrendsJsonOutput(string slug)
    {
        var layout = ResolveRepositoryLayout();
        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, slug);
        var dotnetTrends = PhysiologicalCalculator.AggregateWeeklyTrends(activities, maxWeeks: 12);

        var activitiesPath = Path.Combine(layout.AthletesDirectory, slug, "intervals_activities.json");
        using var pyDoc = RunPythonScript(layout, "src/analysis/trends.py", "--input", activitiesPath, "--json");
        var pyTrends = pyDoc.RootElement.GetProperty("trends");

        Assert.That(dotnetTrends.Count, Is.EqualTo(pyTrends.GetArrayLength()));

        for (var i = 0; i < dotnetTrends.Count; i++)
        {
            var cs = dotnetTrends[i];
            var py = pyTrends[i];

            Assert.That(cs.IsoWeek, Is.EqualTo(py.GetProperty("iso_week").GetString()));
            Assert.That(cs.RunsCount, Is.EqualTo(py.GetProperty("runs_count").GetInt32()));
            Assert.That(cs.TotalDistanceKm, Is.EqualTo(py.GetProperty("total_distance_km").GetDouble()).Within(0.01));
            Assert.That(cs.LongestRunKm, Is.EqualTo(py.GetProperty("longest_run_km").GetDouble()).Within(0.01));
            Assert.That(cs.TotalTimeSeconds, Is.EqualTo(py.GetProperty("total_time_s").GetInt32()));
            Assert.That(cs.TotalLoad, Is.EqualTo(py.GetProperty("total_load").GetDouble()).Within(0.1));
            Assert.That(cs.AveragePaceSecondsPerKm, Is.EqualTo(py.GetProperty("avg_pace_s").GetDouble()).Within(0.01));
            Assert.That(cs.AverageHeartRate, Is.EqualTo(py.GetProperty("avg_hr").GetDouble()).Within(0.1));
            Assert.That(cs.BaseRunsCount, Is.EqualTo(py.GetProperty("base_runs_count").GetInt32()));

            var pyZ1Z2 = py.GetProperty("z1_z2_pct");
            Assert.That(cs.Z1Z2Percentage, pyZ1Z2.ValueKind == JsonValueKind.Null
                ? Is.Null
                : Is.EqualTo(pyZ1Z2.GetDouble()).Within(0.1));

            var pyEf = py.GetProperty("aerobic_ef");
            Assert.That(cs.AerobicEfficiencyFactor, pyEf.ValueKind == JsonValueKind.Null
                ? Is.Null
                : Is.EqualTo(pyEf.GetDouble()).Within(0.001));

            var pyBaseEf = py.GetProperty("base_ef");
            Assert.That(cs.BaseAerobicEfficiencyFactor, pyBaseEf.ValueKind == JsonValueKind.Null
                ? Is.Null
                : Is.EqualTo(pyBaseEf.GetDouble()).Within(0.001));

            var pyBasePace = py.GetProperty("base_avg_pace_s");
            Assert.That(cs.BaseAveragePaceSecondsPerKm, pyBasePace.ValueKind == JsonValueKind.Null
                ? Is.Null
                : Is.EqualTo(pyBasePace.GetDouble()).Within(0.01));

            var pyZonesSec = py.GetProperty("zone_breakdown_s");
            var expectedZonesSec = Enumerable.Range(0, 5).Select(idx => pyZonesSec[idx].GetInt32()).ToArray();
            Assert.That(cs.ZoneBreakdownSeconds, Is.EqualTo(expectedZonesSec));

            var pyZonesPct = py.GetProperty("zone_breakdown_pct");
            for (var z = 0; z < 5; z++)
            {
                Assert.That(cs.ZoneBreakdownPercentages[z], Is.EqualTo(pyZonesPct[z].GetDouble()).Within(0.1));
            }
        }
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void RecentRunsMatchPythonRecentRunsJsonOutput(string slug)
    {
        var layout = ResolveRepositoryLayout();
        var profileDoc = StrictJsonLoader.LoadAthleteProfile(layout, slug);
        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, slug);
        var wellness = StrictJsonLoader.LoadIntervalsWellness(layout, slug);

        var snapshot = PhysiologicalCalculator.CreateReportSnapshot(
            profileDoc.Athlete,
            activities,
            wellness,
            asOfDate: new DateOnly(2026, 11, 11),
            recentLimit: 5,
            maxWeeks: 12);

        var activitiesPath = Path.Combine(layout.AthletesDirectory, slug, "intervals_activities.json");
        using var pyDoc = RunPythonScript(layout, "src/analysis/recent_runs.py", "--limit", "5", "--input", activitiesPath, "--json");
        var pyRuns = pyDoc.RootElement.GetProperty("runs");

        Assert.That(snapshot.RecentRuns.Count, Is.EqualTo(pyRuns.GetArrayLength()));

        for (var i = 0; i < snapshot.RecentRuns.Count; i++)
        {
            var cs = snapshot.RecentRuns[i];
            var py = pyRuns[i];

            Assert.That(cs.Id, Is.EqualTo(py.GetProperty("id").GetString()));
            Assert.That(cs.Name, Is.EqualTo(py.GetProperty("name").GetString()));
            Assert.That(cs.DistanceKm, Is.EqualTo(py.GetProperty("distance_km").GetDouble()).Within(0.01));
            Assert.That(cs.MovingTimeSeconds, Is.EqualTo(py.GetProperty("moving_time_s").GetInt32()));
            Assert.That(cs.TrainingLoad, Is.EqualTo(py.GetProperty("training_load").GetDouble()).Within(0.1));

            var pyZ1Z2 = py.GetProperty("z1_z2_pct");
            Assert.That(cs.Z1Z2Percentage, pyZ1Z2.ValueKind == JsonValueKind.Null
                ? Is.Null
                : Is.EqualTo(pyZ1Z2.GetDouble()).Within(0.1));
        }
    }

    [TestCase("berni", "2026-11-11", "wellness")]
    [TestCase("berni", "2026-11-20", "decayed")]
    [TestCase("berni-test", "2026-11-11", "decayed")]
    public void FitnessFormTrendMatchesPythonForecastCalculation(string slug, string asOfIso, string expectedSource)
    {
        var layout = ResolveRepositoryLayout();
        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, slug);
        var wellness = StrictJsonLoader.LoadIntervalsWellness(layout, slug);
        var asOfDate = DateOnly.Parse(asOfIso, CultureInfo.InvariantCulture);

        var csForm = PhysiologicalCalculator.CalculateFitnessFormTrend(activities, wellness, asOfDate);

        var activitiesPath = Path.Combine(layout.AthletesDirectory, slug, "intervals_activities.json");
        var wellnessPath = Path.Combine(layout.AthletesDirectory, slug, "intervals_wellness.json");

        const string pythonSnippet = """
            import json, sys
            from pathlib import Path
            from src.analysis.forecast import calculate_fitness_form_trend
            activities = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
            res = calculate_fitness_form_trend(activities, wellness_file=Path(sys.argv[2]), as_of_date=sys.argv[3])
            print(json.dumps(res))
            """;

        using var pyDoc = RunPythonScript(layout, "-c", pythonSnippet, activitiesPath, wellnessPath, asOfIso);
        var py = pyDoc.RootElement;

        Assert.That(csForm.DataSource, Is.EqualTo(expectedSource));
        Assert.That(csForm.DataSource, Is.EqualTo(py.GetProperty("data_source").GetString()));
        Assert.That(csForm.LatestCtl, Is.EqualTo(py.GetProperty("latest_ctl").GetDouble()).Within(0.1));
        Assert.That(csForm.LatestAtl, Is.EqualTo(py.GetProperty("latest_atl").GetDouble()).Within(0.1));
        Assert.That(csForm.CurrentTsb, Is.EqualTo(py.GetProperty("current_tsb").GetDouble()).Within(0.1));
        Assert.That(csForm.TsbLevel, Is.EqualTo(py.GetProperty("tsb_level").GetString()));
        Assert.That(csForm.Timeline.Count, Is.EqualTo(py.GetProperty("timeline").GetArrayLength()));
    }

    [Test]
    public void ReportSnapshotIntegratesAthleteProfileWithPureMetrics()
    {
        var layout = ResolveRepositoryLayout();
        var profileDoc = StrictJsonLoader.LoadAthleteProfile(layout, "berni");
        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, "berni");
        var wellness = StrictJsonLoader.LoadIntervalsWellness(layout, "berni");

        var snapshot = PhysiologicalCalculator.CreateReportSnapshot(
            profileDoc.Athlete,
            activities,
            wellness,
            asOfDate: new DateOnly(2026, 11, 11));

        Assert.That(snapshot.AthleteId, Is.EqualTo(profileDoc.Athlete.Id));
        Assert.That(snapshot.Z2MaxHeartRate, Is.InRange(130, 170));
        Assert.That(snapshot.RecentRuns, Has.Count.EqualTo(5));
        Assert.That(snapshot.WeeklyTrends, Has.Count.EqualTo(12));
        Assert.That(snapshot.FitnessForm.Timeline, Is.Not.Empty);
    }
}
