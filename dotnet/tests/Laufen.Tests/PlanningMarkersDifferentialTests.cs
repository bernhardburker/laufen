using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class PlanningMarkersDifferentialTests
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

    private static JsonDocument RunPythonExtractMarkers(
        WorkspaceLayout layout,
        string slug,
        string refDateIso,
        string targetDateIso)
    {
        var pythonExec = Path.Combine(layout.RootDirectory, ".venv", "bin", "python");
        Assert.That(File.Exists(pythonExec), Is.True, $"Python executable not found at {pythonExec}");

        const string script = """
            import datetime, json, sys
            from pathlib import Path
            from src.planner.extract_markers import extract_planning_markers

            athlete_dir = Path(sys.argv[1])
            ref_d = datetime.date.fromisoformat(sys.argv[2])
            target_d = datetime.date.fromisoformat(sys.argv[3])
            res = extract_planning_markers(
                activities_file=athlete_dir / "intervals_activities.json",
                wellness_file=athlete_dir / "intervals_wellness.json",
                profile_file=athlete_dir / "athlete.json",
                history_file=athlete_dir / "coach_history.json",
                events_data=[],
                ref_date=ref_d,
                target_date=target_d,
            )
            print(json.dumps(res))
            """;

        var athleteDir = Path.Combine(layout.AthletesDirectory, slug);
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
        startInfo.ArgumentList.Add(athleteDir);
        startInfo.ArgumentList.Add(refDateIso);
        startInfo.ArgumentList.Add(targetDateIso);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Python process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.That(process.ExitCode, Is.EqualTo(0), $"Python exited with {process.ExitCode}: {stderr}");
        return JsonDocument.Parse(stdout);
    }

    [TestCase("berni", "2026-11-11", "2026-11-12")]
    [TestCase("berni", "2026-11-10", "2026-11-11")]
    [TestCase("berni-test", "2026-11-11", "2026-11-12")]
    public void PlanningMarkersMatchPythonExtractMarkersOnRealData(
        string slug,
        string refDateIso,
        string targetDateIso)
    {
        var layout = ResolveRepositoryLayout();
        var profileDoc = StrictJsonLoader.LoadAthleteProfile(layout, slug);
        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, slug);
        var wellness = StrictJsonLoader.LoadIntervalsWellness(layout, slug);
        var coachHistory = StrictJsonLoader.LoadCoachHistory(layout, slug);

        var refDate = DateOnly.Parse(refDateIso, CultureInfo.InvariantCulture);
        var targetDate = DateOnly.Parse(targetDateIso, CultureInfo.InvariantCulture);

        var cs = PlanningMarkersCalculator.ExtractPlanningMarkers(
            profileDoc.Athlete,
            activities,
            wellness,
            coachHistory,
            CalendarContextMarkers.Empty,
            refDate,
            targetDate);

        using var pyDoc = RunPythonExtractMarkers(layout, slug, refDateIso, targetDateIso);
        var py = pyDoc.RootElement;

        // Athlete info
        var pyAth = py.GetProperty("athlete");
        Assert.That(cs.Athlete.Classification, Is.EqualTo(pyAth.GetProperty("classification").GetString()));
        Assert.That(cs.Athlete.TrainingMode, Is.EqualTo(pyAth.GetProperty("training_mode").GetString()));
        Assert.That(cs.Athlete.Z2MaxHr, Is.EqualTo(pyAth.GetProperty("z2_max_hr").GetInt32()));
        Assert.That(cs.Athlete.Lthr, Is.EqualTo(pyAth.GetProperty("lthr").GetInt32()));

        // Readiness
        var pyRead = py.GetProperty("readiness");
        Assert.That(cs.Readiness.Tsb, Is.EqualTo(pyRead.GetProperty("tsb").GetDouble()).Within(0.1));
        Assert.That(cs.Readiness.Atl, Is.EqualTo(pyRead.GetProperty("atl").GetDouble()).Within(0.1));
        Assert.That(cs.Readiness.Ctl, Is.EqualTo(pyRead.GetProperty("ctl").GetDouble()).Within(0.1));
        Assert.That(cs.Readiness.SleepHoursLastNight, Is.EqualTo(pyRead.GetProperty("sleep_hours_last_night").GetDouble()).Within(0.01));
        Assert.That(cs.Readiness.SleepScoreLastNight, Is.EqualTo(pyRead.GetProperty("sleep_score_last_night").GetDouble()).Within(0.1));
        Assert.That(cs.Readiness.SleepIsToday, Is.EqualTo(pyRead.GetProperty("sleep_is_today").GetBoolean()));
        Assert.That(cs.Readiness.RestingHrToday, Is.EqualTo(pyRead.GetProperty("resting_hr_today").GetInt32()));
        Assert.That(cs.Readiness.RestingHrBaseline, Is.EqualTo(pyRead.GetProperty("resting_hr_baseline").GetDouble()).Within(0.1));
        Assert.That(cs.Readiness.ElevatedRhr, Is.EqualTo(pyRead.GetProperty("elevated_rhr").GetBoolean()));
        Assert.That(cs.Readiness.HrvToday, Is.EqualTo(pyRead.GetProperty("hrv_today").GetDouble()).Within(0.1));
        Assert.That(cs.Readiness.HealthWarning, Is.EqualTo(pyRead.GetProperty("health_warning").GetBoolean()));

        // History
        var pyHist = py.GetProperty("history");
        Assert.That(cs.History.DaysSinceLastRun, Is.EqualTo(pyHist.GetProperty("days_since_last_run").GetInt32()));
        Assert.That(cs.History.TargetDaysSinceLastRun, Is.EqualTo(pyHist.GetProperty("target_days_since_last_run").GetInt32()));
        Assert.That(cs.History.DaysSinceLastQuality, Is.EqualTo(pyHist.GetProperty("days_since_last_quality").GetInt32()));
        Assert.That(cs.History.DaysSinceLastBaseRun, Is.EqualTo(pyHist.GetProperty("days_since_last_base_run").GetInt32()));
        Assert.That(cs.History.BaseAnchorCompleted, Is.EqualTo(pyHist.GetProperty("base_anchor_completed").GetBoolean()));
        Assert.That(cs.History.WeeklyRunsCompleted, Is.EqualTo(pyHist.GetProperty("weekly_runs_completed").GetInt32()));
        Assert.That(cs.History.LastWeekTotalKm, Is.EqualTo(pyHist.GetProperty("last_week_total_km").GetDouble()).Within(0.01));
        Assert.That(cs.History.LastWeekZ1Z2Percentage, Is.EqualTo(pyHist.GetProperty("last_week_z1_z2_pct").GetDouble()).Within(0.1));
        Assert.That(cs.History.ConsecutiveBuildWeeks, Is.EqualTo(pyHist.GetProperty("consecutive_build_weeks").GetInt32()));
        Assert.That(cs.History.BaseAnchorsInLast14Days, Is.EqualTo(pyHist.GetProperty("base_anchors_in_last_14d").GetInt32()));

        // Periodization
        var pyPer = py.GetProperty("periodization");
        Assert.That(cs.Periodization.TargetDayName, Is.EqualTo(pyPer.GetProperty("target_day_name").GetString()));
        Assert.That(cs.Periodization.SlotId, Is.EqualTo(pyPer.GetProperty("slot_id").GetInt32()));
        Assert.That(cs.Periodization.Role, Is.EqualTo(pyPer.GetProperty("role").GetString()));
        Assert.That(cs.Periodization.PreferredType, Is.EqualTo(pyPer.GetProperty("preferred_type").GetString()));
        Assert.That(cs.Periodization.RequiresBaseAnchor, Is.EqualTo(pyPer.GetProperty("requires_base_anchor").GetBoolean()));

        // Coach context
        var pyCoach = py.GetProperty("coach_context");
        Assert.That(cs.CoachContext.HasOpenCycle, Is.EqualTo(pyCoach.GetProperty("has_open_cycle").GetBoolean()));
        Assert.That(cs.CoachContext.RunsCompletedSinceCycle, Is.EqualTo(pyCoach.GetProperty("runs_completed_since_cycle").GetInt32()));
        Assert.That(cs.CoachContext.UnfulfilledBaseDirective, Is.EqualTo(pyCoach.GetProperty("unfulfilled_base_directive").GetBoolean()));

        // Guardrails
        var pyGuard = py.GetProperty("guardrails");
        Assert.That(cs.Guardrails.QualityDiscouraged, Is.EqualTo(pyGuard.GetProperty("quality_discouraged").GetBoolean()));
        Assert.That(cs.Guardrails.FatigueWarning, Is.EqualTo(pyGuard.GetProperty("fatigue_warning").GetBoolean()));
        Assert.That(cs.Guardrails.RestDayRecommended, Is.EqualTo(pyGuard.GetProperty("rest_day_recommended").GetBoolean()));
        Assert.That(cs.Guardrails.VolumeSpikeAlert, Is.EqualTo(pyGuard.GetProperty("volume_spike_alert").GetBoolean()));
        Assert.That(cs.Guardrails.HealthWarning, Is.EqualTo(pyGuard.GetProperty("health_warning").GetBoolean()));
        Assert.That(cs.Guardrails.BaseAnchorMissing, Is.EqualTo(pyGuard.GetProperty("base_anchor_missing").GetBoolean()));
        Assert.That(cs.Guardrails.CoachDirectiveOverride, Is.EqualTo(pyGuard.GetProperty("coach_directive_override").GetBoolean()));
        Assert.That(cs.Guardrails.IsDeloadWeek, Is.EqualTo(pyGuard.GetProperty("is_deload_week").GetBoolean()));
        Assert.That(cs.Guardrails.ReentryRecommended, Is.EqualTo(pyGuard.GetProperty("reentry_recommended").GetBoolean()));
        Assert.That(cs.Guardrails.CalendarRestDay, Is.EqualTo(pyGuard.GetProperty("calendar_rest_day").GetBoolean()));
        Assert.That(cs.Guardrails.SlotAllowsThreshold, Is.EqualTo(pyGuard.GetProperty("slot_allows_threshold").GetBoolean()));
    }

    [Test]
    public void HistoryUsesAthleteProfileTargetRunsPerWeekWithoutHardcoding()
    {
        var layout = ResolveRepositoryLayout();
        var profileDoc = StrictJsonLoader.LoadAthleteProfile(layout, "berni");
        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, "berni");

        var history = PlanningMarkersCalculator.GetRunHistoryMarkers(
            activities,
            new DateOnly(2026, 11, 11),
            new DateOnly(2026, 11, 12),
            profileDoc.Athlete);

        Assert.That(history.TargetRunsPerWeek, Is.EqualTo(profileDoc.Athlete.TargetRunsPerWeek));
    }
}
