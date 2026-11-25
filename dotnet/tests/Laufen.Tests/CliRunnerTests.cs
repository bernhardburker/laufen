using System.Text.Json;
using Laufen.Cli;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class CliRunnerTests
{
    private static string ResolveWorkspaceRoot() =>
        WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory).RootDirectory;

    [Test]
    public void RunWithHealthFlagReturnsOkJson()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(["--health"], stdout, stderr);
        using var doc = JsonDocument.Parse(stdout.ToString().Trim());

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr.ToString(), Is.Empty);
        Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("ok"));
    }

    [Test]
    public void RunHelpReturnsZeroAndListsCommands()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(["help"], stdout, stderr);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stdout.ToString(), Does.Contain("report"));
        Assert.That(stdout.ToString(), Does.Contain("markers"));
        Assert.That(stdout.ToString(), Does.Contain("dossier"));
        Assert.That(stdout.ToString(), Does.Contain("validate-workout"));
        Assert.That(stdout.ToString(), Does.Contain("athletes"));
    }

    [Test]
    public void RunAthletesJsonReturnsRegisteredTenants()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["athletes", "--json", "--workspace", ResolveWorkspaceRoot()],
            stdout,
            stderr);

        Assert.That(exitCode, Is.EqualTo(0));
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("defaultAthlete").GetString(), Is.EqualTo("berni"));
        Assert.That(doc.RootElement.GetProperty("athletes").GetArrayLength(), Is.GreaterThanOrEqualTo(2));
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void RunReportJsonForTenantProducesValidSnapshot(string slug)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["report", "--athlete", slug, "--limit", "3", "--weeks", "4", "--as-of", "2026-11-11", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("slug").GetString(), Is.EqualTo(slug));

        var report = doc.RootElement.GetProperty("report");
        Assert.That(report.GetProperty("recentRuns").GetArrayLength(), Is.LessThanOrEqualTo(3));
        Assert.That(report.GetProperty("weeklyTrends").GetArrayLength(), Is.LessThanOrEqualTo(4));
        Assert.That(report.GetProperty("fitnessForm").GetProperty("latestCtl").GetDouble(), Is.InRange(0.0, 200.0));
    }

    [Test]
    public void RunReportTextForDefaultAthleteFormatsReadableOutput()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["report", "--limit", "2", "--as-of", "2026-11-11"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        var text = stdout.ToString();
        Assert.That(text, Does.Contain("=== Report:"));
        Assert.That(text, Does.Contain("Fitness/Form:"));
        Assert.That(text, Does.Contain("Recent Runs"));
        Assert.That(text, Does.Contain("Weekly Trends"));
    }

    [Test]
    public void RunReportAllJsonReturnsActiveAthletes()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["report", "--all", "--limit", "2", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("athletes").GetArrayLength(), Is.GreaterThanOrEqualTo(1));
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void RunMarkersJsonForTenantProducesValidSnapshot(string slug)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["markers", "--athlete", slug, "--ref-date", "2026-11-11", "--target-date", "2026-11-12", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("slug").GetString(), Is.EqualTo(slug));

        var markers = doc.RootElement.GetProperty("markers");
        Assert.That(markers.GetProperty("referenceDate").GetString(), Is.EqualTo("2026-11-11"));
        Assert.That(markers.GetProperty("targetDate").GetString(), Is.EqualTo("2026-11-12"));
        Assert.That(markers.GetProperty("guardrails").TryGetProperty("activeWarnings", out _), Is.True);
    }

    [Test]
    public void RunMarkersTextFormatsReadableOutput()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["markers", "--athlete", "berni", "--ref-date", "2026-11-11"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        var text = stdout.ToString();
        Assert.That(text, Does.Contain("=== Planning Markers: berni"));
        Assert.That(text, Does.Contain("Readiness:"));
        Assert.That(text, Does.Contain("Periodization:"));
        Assert.That(text, Does.Contain("Guardrails:"));
    }

    [Test]
    public void RunWithUnknownAthleteReturnsErrorExitCode()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["report", "--athlete", "nonexistent-athlete"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(stderr.ToString(), Does.Contain("Unknown athlete slug"));
    }

    [Test]
    public void RunWithInvalidDateReturnsErrorExitCode()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["markers", "--ref-date", "11-11-2026"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(stderr.ToString(), Does.Contain("Expected YYYY-MM-DD"));
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void RunDossierJsonForTenantProducesValidSnapshot(string slug)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["dossier", "--athlete", slug, "--ref-date", "2026-11-25", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("slug").GetString(), Is.EqualTo(slug));

        var dossier = doc.RootElement.GetProperty("dossier");
        Assert.That(dossier.GetProperty("referenceDate").GetString(), Is.EqualTo("2026-11-25"));
        Assert.That(dossier.GetProperty("stimulusProgression").TryGetProperty("ledger", out _), Is.True);
        Assert.That(dossier.GetProperty("readiness").TryGetProperty("medicalRedFlags", out _), Is.True);
    }

    [Test]
    public void RunDossierTextFormatsReadableOutput()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["dossier", "--athlete", "berni", "--ref-date", "2026-11-25"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        var text = stdout.ToString();
        Assert.That(text, Does.Contain("=== Intelligence Dossier:"));
        Assert.That(text, Does.Contain("Base Progression Chain"));
        Assert.That(text, Does.Contain("Quality Progression Chain"));
        Assert.That(text, Does.Contain("Data Quality Notes"));
    }

    [Test]
    public void RunValidateWorkoutValidatesAndCompilesValidDecisionJson()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"laufen-valid-decision-{Guid.NewGuid():N}.json");
        try
        {
            const string json = """
                {
                  "action": "single_workout",
                  "target_date": "2026-11-25",
                  "primary_workout": {
                    "title": "Re-Entry Base 45m",
                    "session_type": "easy_run",
                    "steps": [
                      { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } },
                      { "kind": "steady", "duration_seconds": 1800, "target": { "modality": "hr_zone", "hr_zone_start": 2 } },
                      { "kind": "cooldown", "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                    ]
                  },
                  "rationale": {
                    "observations": ["1 clean comeback run completed"],
                    "tradeoff_weighed": "Post-break consolidation vs immediate intensity",
                    "progression_intent": "Second clean aerobic anchor",
                    "rejected_alternative": "Threshold intervals deferred"
                  },
                  "updated_compass": {
                    "updated_at": "2026-11-25",
                    "current_block_focus": "Aerobic re-entry",
                    "base_milestone": "2 clean Z2 runs",
                    "quality_milestone": "Resume threshold after 2 base runs",
                    "observed_athlete_patterns": ["Clean comeback at 100% Z1/Z2"],
                    "last_decision_summary": "Scheduled 45m Z2 run"
                  }
                }
                """;
            File.WriteAllText(tempFile, json);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = LaufenCliRunner.Run(
                ["validate-workout", "--input", tempFile, "--athlete", "berni", "--ref-date", "2026-11-25"],
                stdout,
                stderr,
                AppContext.BaseDirectory);

            Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
            var text = stdout.ToString();
            Assert.That(text, Does.Contain("Validation passed"));
            Assert.That(text, Does.Contain("Main Set\n- 30m Z2 HR"));

            using var jsonOut = new StringWriter();
            using var jsonErr = new StringWriter();
            var jsonExit = LaufenCliRunner.Run(
                ["validate-workout", "--input", tempFile, "--athlete", "berni", "--ref-date", "2026-11-25", "--json"],
                jsonOut,
                jsonErr,
                AppContext.BaseDirectory);

            Assert.That(jsonExit, Is.EqualTo(0), jsonErr.ToString());
            using var doc = JsonDocument.Parse(jsonOut.ToString());
            Assert.That(doc.RootElement.GetProperty("isValid").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("compiledPrimary").GetProperty("dslText").GetString(), Does.Contain("Main Set\n- 30m Z2 HR"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Test]
    public void RunValidateWorkoutRejectsInvalidModalityAndReturnsExitCodeOne()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"laufen-invalid-decision-{Guid.NewGuid():N}.json");
        try
        {
            const string json = """
                {
                  "action": "single_workout",
                  "target_date": "2026-11-25",
                  "primary_workout": {
                    "title": "Forbidden Pace Easy Run",
                    "session_type": "easy_run",
                    "steps": [
                      { "kind": "steady", "duration_seconds": 2400, "target": { "modality": "pace_range", "pace_fast_sec_per_km": 390, "pace_slow_sec_per_km": 410 } }
                    ]
                  },
                  "rationale": {
                    "observations": ["Testing validator"],
                    "tradeoff_weighed": "None",
                    "progression_intent": "None",
                    "rejected_alternative": "None"
                  },
                  "updated_compass": {
                    "updated_at": "2026-11-25",
                    "current_block_focus": "Test",
                    "base_milestone": "Test",
                    "quality_milestone": "Test",
                    "observed_athlete_patterns": [],
                    "last_decision_summary": "Test"
                  }
                }
                """;
            File.WriteAllText(tempFile, json);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = LaufenCliRunner.Run(
                ["validate-workout", "--input", tempFile],
                stdout,
                stderr,
                AppContext.BaseDirectory);

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr.ToString(), Does.Contain("aerobic_session_forbids_non_hr_or_high_zone"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
