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
        Assert.That(stdout.ToString(), Does.Contain("project-plan"));
        Assert.That(stdout.ToString(), Does.Contain("coach-plan"));
        Assert.That(stdout.ToString(), Does.Contain("simulate"));
        Assert.That(stdout.ToString(), Does.Contain("narrate"));
        Assert.That(stdout.ToString(), Does.Contain("review"));
        Assert.That(stdout.ToString(), Does.Contain("sync-calendar"));
        Assert.That(stdout.ToString(), Does.Contain("telegram-briefing"));
        Assert.That(stdout.ToString(), Does.Contain("telegram-override"));
        Assert.That(stdout.ToString(), Does.Contain("autopilot-run"));
        Assert.That(stdout.ToString(), Does.Contain("sync-data"));
        Assert.That(stdout.ToString(), Does.Contain("sync-profile"));
        Assert.That(stdout.ToString(), Does.Contain("note"));
        Assert.That(stdout.ToString(), Does.Contain("dashboard"));
        Assert.That(stdout.ToString(), Does.Contain("daemon"));
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

    [Test]
    public void RunProjectPlanValidatesReconcilesAndRejectsPrematureReentryQualityOnBerni()
    {
        var tempValid = Path.Combine(Path.GetTempPath(), $"laufen-autopilot-valid-{Guid.NewGuid():N}.json");
        var tempInvalid = Path.Combine(Path.GetTempPath(), $"laufen-autopilot-invalid-{Guid.NewGuid():N}.json");
        try
        {
            const string validJson = """
                {
                  "trigger": "morning_sync",
                  "reference_date": "2026-11-25",
                  "horizon_days": 14,
                  "health_state": "ReEntry",
                  "operations": [
                    { "date": "2026-11-25", "operation": "delete", "event_id": 160591207 },
                    {
                      "date": "2026-11-25",
                      "operation": "upsert",
                      "workout": {
                        "title": "Re-Entry Aerobic Base 45m",
                        "session_type": "easy_run",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } },
                          { "kind": "steady", "duration_seconds": 1800, "target": { "modality": "hr_zone", "hr_zone_start": 2 } },
                          { "kind": "cooldown", "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    },
                    {
                      "date": "2026-11-28",
                      "operation": "upsert",
                      "workout": {
                        "title": "Aerobic Long Run 60m",
                        "session_type": "long_run",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } },
                          { "kind": "steady", "duration_seconds": 2700, "target": { "modality": "hr_zone", "hr_zone_start": 2 } },
                          { "kind": "cooldown", "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    },
                    {
                      "date": "2026-12-01",
                      "operation": "upsert",
                      "workout": {
                        "title": "Threshold 4x5m Z4",
                        "session_type": "threshold_intervals",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1, "hr_zone_end": 2 } },
                          {
                            "kind": "repeat",
                            "reps": 4,
                            "work": { "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 4 } },
                            "recovery": { "duration_seconds": 120, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                          },
                          { "kind": "cooldown", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    }
                  ],
                  "rationale": {
                    "observations": ["1 clean comeback run completed after 10d back break"],
                    "tradeoff_weighed": "Post-break consolidation before threshold",
                    "progression_intent": "Complete 2nd clean Z2 run today, 60m long run Saturday, threshold next Tuesday",
                    "rejected_alternative": "Threshold today rejected due to single post-break session"
                  },
                  "updated_compass": {
                    "updated_at": "2026-11-25",
                    "current_block_focus": "Re-entry aerobic build",
                    "base_milestone": "60m Z2 Long Run",
                    "quality_milestone": "4x5m Z4 Threshold on Dec 1",
                    "observed_athlete_patterns": ["Clean comeback Z1/Z2"],
                    "last_decision_summary": "Deferred threshold to Dec 1 after 2 clean base runs",
                    "health_state": "ReEntry",
                    "auto_event_ids": [160591207, 160252764]
                  }
                }
                """;
            File.WriteAllText(tempValid, validJson);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = LaufenCliRunner.Run(
                ["project-plan", "--input", tempValid, "--athlete", "berni", "--ref-date", "2026-11-25", "--auto-event-ids", "160591207,160252764"],
                stdout,
                stderr,
                AppContext.BaseDirectory);

            Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
            var text = stdout.ToString();
            Assert.That(text, Does.Contain("14-Day Autopilot Projection: berni"));
            Assert.That(text, Does.Contain("Days 1-7 Watch Commit Window (3 session(s))"));
            Assert.That(text, Does.Contain("Banister 14d Projection: CTL"));

            using var jsonOut = new StringWriter();
            using var jsonErr = new StringWriter();
            var jsonExit = LaufenCliRunner.Run(
                ["project-plan", "--input", tempValid, "--athlete", "berni", "--ref-date", "2026-11-25", "--auto-event-ids", "160591207,160252764", "--json"],
                jsonOut,
                jsonErr,
                AppContext.BaseDirectory);

            Assert.That(jsonExit, Is.EqualTo(0), jsonErr.ToString());
            using var doc = JsonDocument.Parse(jsonOut.ToString());
            Assert.That(doc.RootElement.GetProperty("isValid").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("schedule").GetProperty("watchCommitWindow").GetArrayLength(), Is.EqualTo(3));

            const string invalidJson = """
                {
                  "trigger": "morning_sync",
                  "reference_date": "2026-11-25",
                  "horizon_days": 14,
                  "health_state": "ReEntry",
                  "operations": [
                    { "date": "2026-11-25", "operation": "delete", "event_id": 160252764 },
                    {
                      "date": "2026-11-25",
                      "operation": "upsert",
                      "workout": {
                        "title": "Premature Threshold 4x5m Z4",
                        "session_type": "threshold_intervals",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1, "hr_zone_end": 2 } },
                          {
                            "kind": "repeat",
                            "reps": 4,
                            "work": { "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 4 } },
                            "recovery": { "duration_seconds": 120, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                          },
                          { "kind": "cooldown", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    }
                  ],
                  "rationale": {
                    "observations": ["1 clean comeback run completed"],
                    "tradeoff_weighed": "Immediate threshold",
                    "progression_intent": "Force threshold on day 0",
                    "rejected_alternative": "Base run"
                  },
                  "updated_compass": {
                    "updated_at": "2026-11-25",
                    "current_block_focus": "Test",
                    "base_milestone": "Test",
                    "quality_milestone": "Test",
                    "observed_athlete_patterns": [],
                    "last_decision_summary": "Premature threshold"
                  }
                }
                """;
            File.WriteAllText(tempInvalid, invalidJson);

            using var errOut = new StringWriter();
            using var errStderr = new StringWriter();
            var invalidExit = LaufenCliRunner.Run(
                ["project-plan", "--input", tempInvalid, "--athlete", "berni", "--ref-date", "2026-11-25", "--auto-event-ids", "160591207,160252764"],
                errOut,
                errStderr,
                AppContext.BaseDirectory);

            Assert.That(invalidExit, Is.EqualTo(1));
            Assert.That(errStderr.ToString(), Does.Contain("reentry_requires_base_before_quality"));
        }
        finally
        {
            if (File.Exists(tempValid))
            {
                File.Delete(tempValid);
            }
            if (File.Exists(tempInvalid))
            {
                File.Delete(tempInvalid);
            }
        }
    }

    [Test]
    public void RunCoachPlanDryRunWithStubProviderProducesReconciledPlanWithoutWritingState()
    {
        var root = ResolveWorkspaceRoot();
        var compassPath = Path.Combine(root, "athletes", "berni", "block_compass.json");
        var tracePath = Path.Combine(root, "athletes", "berni", "decision_trace_dotnet.jsonl");
        var compassExistedBefore = File.Exists(compassPath);
        var traceExistedBefore = File.Exists(tracePath);
        var tempStub = Path.Combine(Path.GetTempPath(), $"laufen-stub-coach-{Guid.NewGuid():N}.json");

        try
        {
            const string stubJson = """
                {
                  "trigger": "morning_sync",
                  "reference_date": "2026-11-25",
                  "horizon_days": 14,
                  "health_state": "ReEntry",
                  "operations": [
                    { "date": "2026-11-25", "operation": "delete", "event_id": 160591207 },
                    {
                      "date": "2026-11-25",
                      "operation": "upsert",
                      "workout": {
                        "title": "Re-Entry Base 45m",
                        "session_type": "easy_run",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } },
                          { "kind": "steady", "duration_seconds": 1800, "target": { "modality": "hr_zone", "hr_zone_start": 2 } },
                          { "kind": "cooldown", "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    },
                    {
                      "date": "2026-11-28",
                      "operation": "upsert",
                      "workout": {
                        "title": "Aerobic Long Run 60m",
                        "session_type": "long_run",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } },
                          { "kind": "steady", "duration_seconds": 2700, "target": { "modality": "hr_zone", "hr_zone_start": 2 } },
                          { "kind": "cooldown", "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    },
                    {
                      "date": "2026-12-01",
                      "operation": "upsert",
                      "workout": {
                        "title": "Threshold 4x5m Z4",
                        "session_type": "threshold_intervals",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1, "hr_zone_end": 2 } },
                          {
                            "kind": "repeat",
                            "reps": 4,
                            "work": { "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 4 } },
                            "recovery": { "duration_seconds": 120, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                          },
                          { "kind": "cooldown", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    }
                  ],
                  "rationale": {
                    "observations": ["1 clean comeback run completed"],
                    "tradeoff_weighed": "Base ramp-up before threshold",
                    "progression_intent": "2nd base run today, 60m Sat, threshold Dec 1",
                    "rejected_alternative": "Threshold today"
                  },
                  "updated_compass": {
                    "updated_at": "2026-11-25",
                    "current_block_focus": "Re-entry base build",
                    "base_milestone": "60m Z2",
                    "quality_milestone": "4x5m Z4",
                    "observed_athlete_patterns": ["Clean comeback"],
                    "last_decision_summary": "Easy 45m Z2 today",
                    "health_state": "ReEntry",
                    "auto_event_ids": [160591207, 160252764]
                  }
                }
                """;
            File.WriteAllText(tempStub, stubJson);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = LaufenCliRunner.Run(
                ["coach-plan", "--athlete", "berni", "--ref-date", "2026-11-25", "--provider", "stub", "--stub-response", tempStub, "--auto-event-ids", "160591207,160252764", "--dry-run"],
                stdout,
                stderr,
                AppContext.BaseDirectory);

            Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
            var text = stdout.ToString();
            Assert.That(text, Does.Contain("Autopilot Coach Plan: berni"));
            Assert.That(text, Does.Contain("Compiled Intervals.icu DSL Upserts (3):"));
            Assert.That(text, Does.Contain("Banister 14d Projection: CTL"));
            Assert.That(File.Exists(compassPath), Is.EqualTo(compassExistedBefore));
            Assert.That(File.Exists(tracePath), Is.EqualTo(traceExistedBefore));

            using var jsonOut = new StringWriter();
            using var jsonErr = new StringWriter();
            var jsonExit = LaufenCliRunner.Run(
                ["coach-plan", "--athlete", "berni", "--ref-date", "2026-11-25", "--provider", "stub", "--stub-response", tempStub, "--auto-event-ids", "160591207,160252764", "--dry-run", "--json"],
                jsonOut,
                jsonErr,
                AppContext.BaseDirectory);

            Assert.That(jsonExit, Is.EqualTo(0), jsonErr.ToString());
            using var doc = JsonDocument.Parse(jsonOut.ToString());
            Assert.That(doc.RootElement.GetProperty("dryRun").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("outcome").GetProperty("success").GetBoolean(), Is.True);
        }
        finally
        {
            if (File.Exists(tempStub))
            {
                File.Delete(tempStub);
            }
        }
    }

    [Test]
    public void RunSimulateAllStubScenariosPassesAndWritesIsolatedArtifacts()
    {
        var tempOutDir = Path.Combine(Path.GetTempPath(), $"laufen-sim-cli-{Guid.NewGuid():N}");
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = LaufenCliRunner.Run(
                ["simulate", "--scenario", "all", "--athlete", "berni", "--provider", "stub", "--dry-run"],
                stdout,
                stderr,
                AppContext.BaseDirectory);

            Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
            var text = stdout.ToString();
            Assert.That(text, Does.Contain("Autopilot Multi-Day Simulation: berni"));
            Assert.That(text, Does.Contain("passed=5/5"));

            using var jsonOut = new StringWriter();
            using var jsonErr = new StringWriter();
            var jsonExit = LaufenCliRunner.Run(
                ["simulate", "--scenario", "telegram_user_locked_override", "--athlete", "berni", "--provider", "stub", "--output-dir", tempOutDir, "--json"],
                jsonOut,
                jsonErr,
                AppContext.BaseDirectory);

            Assert.That(jsonExit, Is.EqualTo(0), jsonErr.ToString());
            using var doc = JsonDocument.Parse(jsonOut.ToString());
            Assert.That(doc.RootElement.GetProperty("allPassed").GetBoolean(), Is.True);
            Assert.That(File.Exists(Path.Combine(tempOutDir, "telegram_user_locked_override", "report.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(tempOutDir, "telegram_user_locked_override", "timeline.json")), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempOutDir))
            {
                Directory.Delete(tempOutDir, recursive: true);
            }
        }
    }

    [Test]
    public void RunNarrateRespectsEventGateAndDryRunWithStubProvider()
    {
        var root = ResolveWorkspaceRoot();
        var summaryPath = Path.Combine(root, "athletes", "berni", "narrative_summary_dotnet.json");
        var commentaryPath = Path.Combine(root, "athletes", "berni", "narrative_commentary_dotnet.md");
        var summaryExisted = File.Exists(summaryPath);
        var commentaryExisted = File.Exists(commentaryPath);

        // 1. Routine morning_sync with unchanged state is suppressed
        using var supOut = new StringWriter();
        using var supErr = new StringWriter();
        var supExit = LaufenCliRunner.Run(
            ["narrate", "--athlete", "berni", "--ref-date", "2026-11-25", "--trigger", "morning_sync", "--provider", "stub", "--dry-run", "--json"],
            supOut,
            supErr,
            AppContext.BaseDirectory);

        Assert.That(supExit, Is.EqualTo(0), supErr.ToString());
        using (var supDoc = JsonDocument.Parse(supOut.ToString()))
        {
            Assert.That(supDoc.RootElement.GetProperty("skipped").GetBoolean(), Is.True);
            Assert.That(supDoc.RootElement.GetProperty("narrated").GetBoolean(), Is.False);
        }

        // 2. post_run triggers narrative generation cleanly without writing state under --dry-run
        using var postOut = new StringWriter();
        using var postErr = new StringWriter();
        var postExit = LaufenCliRunner.Run(
            ["narrate", "--athlete", "berni", "--ref-date", "2026-11-25", "--trigger", "post_run", "--provider", "stub", "--dry-run"],
            postOut,
            postErr,
            AppContext.BaseDirectory);

        Assert.That(postExit, Is.EqualTo(0), postErr.ToString());
        Assert.That(postOut.ToString(), Does.Contain("Progress Narrator: berni"));
        Assert.That(postOut.ToString(), Does.Contain("Insights ("));
        Assert.That(File.Exists(summaryPath), Is.EqualTo(summaryExisted));
        Assert.That(File.Exists(commentaryPath), Is.EqualTo(commentaryExisted));

        // 3. --force works even on morning_sync and emits strict JSON
        using var forceOut = new StringWriter();
        using var forceErr = new StringWriter();
        var forceExit = LaufenCliRunner.Run(
            ["narrate", "--athlete", "berni-test", "--ref-date", "2026-11-25", "--trigger", "morning_sync", "--force", "--provider", "stub", "--dry-run", "--json"],
            forceOut,
            forceErr,
            AppContext.BaseDirectory);

        Assert.That(forceExit, Is.EqualTo(0), forceErr.ToString());
        using var forceDoc = JsonDocument.Parse(forceOut.ToString());
        Assert.That(forceDoc.RootElement.GetProperty("narrated").GetBoolean(), Is.True);
        Assert.That(forceDoc.RootElement.GetProperty("outcome").GetProperty("success").GetBoolean(), Is.True);
    }

    [Test]
    public void RunReviewEvaluatesRecentBerniRunsInDryRunWithoutWritingDiskFiles()
    {
        var root = ResolveWorkspaceRoot();
        var dotNetReviewsPath = Path.Combine(root, "athletes", "berni", "run_reviews_dotnet.json");
        var reviewsExisted = File.Exists(dotNetReviewsPath);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = LaufenCliRunner.Run(
            ["review", "--athlete", "berni", "--limit", "3", "--dry-run"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        var text = stdout.ToString();
        Assert.That(text, Does.Contain("4-Stage Post-Run Reviews: berni"));
        Assert.That(text, Does.Contain("i213105633"));
        Assert.That(text, Does.Contain("Z2-GAP 07:58/km"));
        Assert.That(File.Exists(dotNetReviewsPath), Is.EqualTo(reviewsExisted));

        using var jsonOut = new StringWriter();
        using var jsonErr = new StringWriter();
        var jsonExit = LaufenCliRunner.Run(
            ["review", "--athlete", "berni", "--activity-id", "i209021118", "--dry-run", "--json"],
            jsonOut,
            jsonErr,
            AppContext.BaseDirectory);

        Assert.That(jsonExit, Is.EqualTo(0), jsonErr.ToString());
        using var doc = JsonDocument.Parse(jsonOut.ToString());
        Assert.That(doc.RootElement.GetProperty("count").GetInt32(), Is.EqualTo(1));
        Assert.That(doc.RootElement.GetProperty("reviews")[0].GetProperty("id").GetString(), Is.EqualTo("i209021118"));
    }

    [Test]
    public void RunSyncCalendarDryRunBuildsPlanAndBlocksProductionApplyWithoutOverride()
    {
        var tempDecision = Path.Combine(Path.GetTempPath(), $"laufen-sync-decision-{Guid.NewGuid():N}.json");
        try
        {
            const string decisionJson = """
                {
                  "trigger": "morning_sync",
                  "reference_date": "2026-11-25",
                  "horizon_days": 14,
                  "health_state": "ReEntry",
                  "operations": [
                    { "date": "2026-11-25", "operation": "delete", "event_id": 160591207 },
                    {
                      "date": "2026-11-25",
                      "operation": "upsert",
                      "workout": {
                        "title": "Re-Entry Base 45m",
                        "session_type": "easy_run",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } },
                          { "kind": "steady", "duration_seconds": 1800, "target": { "modality": "hr_zone", "hr_zone_start": 2 } },
                          { "kind": "cooldown", "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    }
                  ],
                  "rationale": {
                    "observations": ["1 clean comeback run completed"],
                    "tradeoff_weighed": "Base ramp-up",
                    "progression_intent": "2nd base run today",
                    "rejected_alternative": "Threshold today"
                  },
                  "updated_compass": {
                    "updated_at": "2026-11-25",
                    "current_block_focus": "Re-entry",
                    "base_milestone": "60m Z2",
                    "quality_milestone": "4x5m Z4",
                    "observed_athlete_patterns": ["Clean comeback"],
                    "last_decision_summary": "Easy 45m Z2 today",
                    "health_state": "ReEntry",
                    "auto_event_ids": [160591207, 160252764]
                  }
                }
                """;
            File.WriteAllText(tempDecision, decisionJson);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = LaufenCliRunner.Run(
                ["sync-calendar", "--athlete", "berni", "--input", tempDecision, "--ref-date", "2026-11-25", "--auto-event-ids", "160591207,160252764", "--dry-run", "--json"],
                stdout,
                stderr,
                AppContext.BaseDirectory);

            Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.That(doc.RootElement.GetProperty("dryRun").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("result").GetProperty("applied").GetBoolean(), Is.False);
            Assert.That(doc.RootElement.GetProperty("result").GetProperty("plan").GetProperty("createCount").GetInt32(), Is.EqualTo(1));

            using var blockOut = new StringWriter();
            using var blockErr = new StringWriter();
            var blockExit = LaufenCliRunner.Run(
                ["sync-calendar", "--athlete", "berni", "--input", tempDecision, "--ref-date", "2026-11-25", "--auto-event-ids", "160591207,160252764", "--apply"],
                blockOut,
                blockErr,
                AppContext.BaseDirectory);

            Assert.That(blockExit, Is.EqualTo(1));
            Assert.That(blockErr.ToString(), Does.Contain("SAFETY BLOCK"));
        }
        finally
        {
            if (File.Exists(tempDecision))
            {
                File.Delete(tempDecision);
            }
        }
    }

    [Test]
    public void RunTelegramBriefingAndOverrideCommandsRespectDryRunAndStagingSafety()
    {
        using var briefOut = new StringWriter();
        using var briefErr = new StringWriter();
        var briefExit = LaufenCliRunner.Run(
            ["telegram-briefing", "--athlete", "berni", "--ref-date", "2026-11-25", "--dry-run", "--json"],
            briefOut,
            briefErr,
            AppContext.BaseDirectory);

        Assert.That(briefExit, Is.EqualTo(0), briefErr.ToString());
        using (var briefDoc = JsonDocument.Parse(briefOut.ToString()))
        {
            Assert.That(briefDoc.RootElement.GetProperty("dryRun").GetBoolean(), Is.True);
            Assert.That(briefDoc.RootElement.GetProperty("sent").GetBoolean(), Is.False);
            Assert.That(briefDoc.RootElement.GetProperty("message").GetProperty("messageKind").GetString(), Is.EqualTo("morning_briefing"));
        }

        using var blockOut = new StringWriter();
        using var blockErr = new StringWriter();
        var blockExit = LaufenCliRunner.Run(
            ["telegram-briefing", "--athlete", "berni", "--ref-date", "2026-11-25", "--send"],
            blockOut,
            blockErr,
            AppContext.BaseDirectory);
        Assert.That(blockExit, Is.EqualTo(1));
        Assert.That(blockErr.ToString(), Does.Contain("SAFETY BLOCK"));

        using var acceptOut = new StringWriter();
        using var acceptErr = new StringWriter();
        var acceptExit = LaufenCliRunner.Run(
            ["telegram-override", "--athlete", "berni", "--ref-date", "2026-11-25", "--callback", "accept:2026-11-26", "--dry-run", "--json"],
            acceptOut,
            acceptErr,
            AppContext.BaseDirectory);

        Assert.That(acceptExit, Is.EqualTo(0), acceptErr.ToString());
        using (var acceptDoc = JsonDocument.Parse(acceptOut.ToString()))
        {
            Assert.That(acceptDoc.RootElement.GetProperty("llmInvoked").GetBoolean(), Is.False);
            Assert.That(acceptDoc.RootElement.GetProperty("route").GetProperty("locksDate").GetBoolean(), Is.True);
        }

        var tempStub = Path.Combine(Path.GetTempPath(), $"laufen-tg-override-stub-{Guid.NewGuid():N}.json");
        try
        {
            const string stubJson = """
                {
                  "trigger": "telegram_override",
                  "reference_date": "2026-11-25",
                  "horizon_days": 14,
                  "health_state": "ReEntry",
                  "operations": [
                    { "date": "2026-11-25", "operation": "delete", "event_id": 160591207 },
                    {
                      "date": "2026-11-25",
                      "operation": "upsert",
                      "workout": {
                        "title": "Locker 30m Z1-Z2",
                        "session_type": "easy_run",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } },
                          { "kind": "steady", "duration_seconds": 900, "target": { "modality": "hr_zone", "hr_zone_start": 2 } },
                          { "kind": "cooldown", "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    },
                    {
                      "date": "2026-11-28",
                      "operation": "upsert",
                      "workout": {
                        "title": "Aerobic Long Run 60m",
                        "session_type": "long_run",
                        "steps": [
                          { "kind": "warmup", "duration_seconds": 600, "target": { "modality": "hr_zone", "hr_zone_start": 1 } },
                          { "kind": "steady", "duration_seconds": 2700, "target": { "modality": "hr_zone", "hr_zone_start": 2 } },
                          { "kind": "cooldown", "duration_seconds": 300, "target": { "modality": "hr_zone", "hr_zone_start": 1 } }
                        ]
                      }
                    }
                  ],
                  "rationale": {
                    "observations": ["Athlete requested shorter/easier day via Telegram"],
                    "tradeoff_weighed": "Preserved recovery over volume during ReEntry",
                    "progression_intent": "Short aerobic flush today, long run Saturday",
                    "rejected_alternative": "45m standard easy run"
                  },
                  "updated_compass": {
                    "updated_at": "2026-11-25",
                    "current_block_focus": "Re-entry base build",
                    "base_milestone": "60m Z2",
                    "quality_milestone": "4x5m Z4",
                    "observed_athlete_patterns": ["Responds well to short flexibility overrides"],
                    "last_decision_summary": "Shortened 2026-11-25 to 30m Z1-Z2 easy per Telegram callback",
                    "health_state": "ReEntry"
                  }
                }
                """;
            File.WriteAllText(tempStub, stubJson);

            using var overrideOut = new StringWriter();
            using var overrideErr = new StringWriter();
            var overrideExit = LaufenCliRunner.Run(
                ["telegram-override", "--athlete", "berni", "--ref-date", "2026-11-25", "--auto-event-ids", "160591207,160252764", "--callback", "easier:2026-11-25", "--provider", "stub", "--stub-response", tempStub, "--dry-run", "--json"],
                overrideOut,
                overrideErr,
                AppContext.BaseDirectory);

            Assert.That(overrideExit, Is.EqualTo(0), overrideErr.ToString() + overrideOut.ToString());
            using var overrideDoc = JsonDocument.Parse(overrideOut.ToString());
            Assert.That(overrideDoc.RootElement.GetProperty("llmInvoked").GetBoolean(), Is.True);
            Assert.That(overrideDoc.RootElement.GetProperty("outcome").GetProperty("success").GetBoolean(), Is.True);
        }
        finally
        {
            if (File.Exists(tempStub))
            {
                File.Delete(tempStub);
            }
        }
    }

    [Test]
    public void RunAutopilotRunExecutesUnifiedCycleInDryRunAndBlocksProductionApply()
    {
        using var dryOut = new StringWriter();
        using var dryErr = new StringWriter();
        var dryExit = LaufenCliRunner.Run(
            ["autopilot-run", "--athlete", "berni", "--ref-date", "2026-11-25", "--provider", "stub", "--dry-run", "--json"],
            dryOut,
            dryErr,
            AppContext.BaseDirectory);

        Assert.That(dryExit, Is.EqualTo(0), dryErr.ToString() + dryOut.ToString());
        using (var doc = JsonDocument.Parse(dryOut.ToString()))
        {
            Assert.That(doc.RootElement.GetProperty("dryRun").GetBoolean(), Is.True);
            var cycleResult = doc.RootElement.GetProperty("cycleResult");
            Assert.That(cycleResult.GetProperty("succeeded").GetBoolean(), Is.True);
            Assert.That(cycleResult.GetProperty("hysteresis").GetProperty("shouldReplan").GetBoolean(), Is.True);
            Assert.That(cycleResult.GetProperty("telegramPreview").GetProperty("messageKind").GetString(), Is.EqualTo("morning_briefing"));
        }

        using var blockOut = new StringWriter();
        using var blockErr = new StringWriter();
        var blockExit = LaufenCliRunner.Run(
            ["autopilot-run", "--athlete", "berni", "--ref-date", "2026-11-25", "--apply"],
            blockOut,
            blockErr,
            AppContext.BaseDirectory);

        Assert.That(blockExit, Is.EqualTo(1));
        Assert.That(blockErr.ToString(), Does.Contain("SAFETY BLOCK"));
    }

    [Test]
    public void RunNoteDryRunEvaluatesComplaintAndRejectsProductionPushWithoutFlag()
    {
        var root = ResolveWorkspaceRoot();
        var dotNetReviewsPath = Path.Combine(root, "athletes", "berni", "run_reviews_dotnet.json");
        var reviewsExisted = File.Exists(dotNetReviewsPath);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = LaufenCliRunner.Run(
            ["note", "--athlete", "berni", "--note", "Wadenkrampf ab km 5", "--feel", "2", "--rpe", "7", "--dry-run", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using (var doc = JsonDocument.Parse(stdout.ToString()))
        {
            Assert.That(doc.RootElement.GetProperty("dryRun").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("reviewRecomputed").GetBoolean(), Is.True);
            Assert.That(
                doc.RootElement.GetProperty("updatedReview").GetProperty("review").GetProperty("stages").GetProperty("stage4_next_run").GetProperty("recovery_hours").GetInt32(),
                Is.EqualTo(48));
        }
        Assert.That(File.Exists(dotNetReviewsPath), Is.EqualTo(reviewsExisted));

        using var blockOut = new StringWriter();
        using var blockErr = new StringWriter();
        var blockExit = LaufenCliRunner.Run(
            ["note", "--athlete", "berni", "--note", "Test", "--push"],
            blockOut,
            blockErr,
            AppContext.BaseDirectory);

        Assert.That(blockExit, Is.EqualTo(1));
        Assert.That(blockErr.ToString(), Does.Contain("SAFETY BLOCK"));
    }

    [Test]
    public void RunDaemonOnceDryRunExecutesCleanTickAndReturnsJsonSummary()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = LaufenCliRunner.Run(
            ["daemon", "--once", "--dry-run", "--ref-date", "2026-11-25", "--provider", "stub", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("dryRun").GetBoolean(), Is.True);
        Assert.That(doc.RootElement.GetProperty("errors").GetArrayLength(), Is.EqualTo(0));
        Assert.That(doc.RootElement.GetProperty("morningBriefingsTriggered").GetInt32(), Is.GreaterThanOrEqualTo(1));
    }
}
