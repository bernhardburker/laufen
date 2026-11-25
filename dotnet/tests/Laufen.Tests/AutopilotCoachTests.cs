using System.Net;
using System.Text;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class AutopilotCoachTests
{
    private static WorkspaceLayout ResolveRepositoryLayout() =>
        WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);

    private const string ValidBerniReentryPlanJson = """
        {
          "trigger": "morning_sync",
          "reference_date": "2026-11-25",
          "horizon_days": 14,
          "health_state": "ReEntry",
          "operations": [
            { "date": "2026-11-25", "operation": "delete", "event_id": 160591207, "reason": "Defer threshold until 2 clean post-break base runs" },
            {
              "date": "2026-11-25",
              "operation": "upsert",
              "workout": {
                "title": "Re-Entry Aerobic Base 45m",
                "session_type": "easy_run",
                "coaching_notes": "Keep effort strictly in Zone 2 HR and take walking breaks on hills.",
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
                "title": "Controlled Threshold 4x5m Z4",
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
            "observations": ["10d break ended Nov 22 with 1 clean 43m Z2 comeback run", "Wellness is 3d stale"],
            "tradeoff_weighed": "Re-entry durability before resuming Z4 threshold",
            "progression_intent": "Complete 2nd Z2 session today, 60m Long Run Sat, resume 4x5m Z4 Dec 1",
            "rejected_alternative": "Threshold today rejected by re-entry ramp-up requirement"
          },
          "updated_compass": {
            "updated_at": "2026-11-25",
            "current_block_focus": "Post-break aerobic consolidation -> threshold reintroduction",
            "base_milestone": "Progress long run to 60m Z2",
            "quality_milestone": "Reintroduce 4x5m Z4 after 2 clean base runs",
            "observed_athlete_patterns": ["Back pause ended Nov 22 with clean 100% Z1/Z2 comeback"],
            "last_decision_summary": "Replaced split A/B with 45m Z2 today and scheduled threshold Dec 1",
            "health_state": "ReEntry",
            "auto_event_ids": [160591207, 160252764]
          }
        }
        """;

    private const string PrematureThresholdJson = """
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
            "observations": ["TSB +4.9"],
            "tradeoff_weighed": "Fresh TSB vs recent break",
            "progression_intent": "Immediate threshold",
            "rejected_alternative": "Easy run"
          },
          "updated_compass": {
            "updated_at": "2026-11-25",
            "current_block_focus": "Threshold",
            "base_milestone": "60m",
            "quality_milestone": "4x5m",
            "observed_athlete_patterns": [],
            "last_decision_summary": "Threshold today",
            "health_state": "ReEntry",
            "auto_event_ids": [160591207, 160252764]
          }
        }
        """;

    private static CoachIntelligenceDossier BuildRealBerniDossier()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var berni = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);

        return CoachIntelligenceDossierBuilder.Build(
            berni.Profile,
            berni.Activities,
            berni.WellnessRecords,
            berni.RunReviews,
            berni.CalendarEvents,
            refDate,
            refDate,
            autoEventIds: new HashSet<long> { 160591207, 160252764 });
    }

    [Test]
    public async Task RollingAutopilotStrategyPlansValid14DayReentryScheduleOnRealBerniData()
    {
        var dossier = BuildRealBerniDossier();
        var stub = new StubLlmClient([ValidBerniReentryPlanJson]);
        var strategy = new RollingAutopilotStrategy(stub);

        var outcome = await strategy.PlanAsync(new CoachPlanningContext
        {
            Dossier = dossier,
            Trigger = "morning_sync",
            TimestampUtc = new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero)
        });

        Assert.That(outcome.Success, Is.True, outcome.FailureReason);
        Assert.That(outcome.Attempts, Has.Count.EqualTo(1));
        Assert.That(outcome.ReconciledSchedule, Is.Not.Null);
        Assert.That(outcome.ReconciledSchedule!.WatchCommitWindow, Has.Count.EqualTo(3));
        Assert.That(outcome.TraceEntry.Success, Is.True);
        Assert.That(outcome.TraceEntry.UpsertCount, Is.EqualTo(3));

        var userPrompt = stub.RecordedRequests[0].UserPrompt;
        Assert.That(userPrompt, Does.Contain("160591207"));
        Assert.That(userPrompt, Does.Contain("143,152,160,169,192"));
        Assert.That(userPrompt, Does.Contain("Wellness data is stale"));
    }

    [Test]
    public async Task RollingAutopilotStrategySelfCorrectsAfterGatekeeperRejection()
    {
        var dossier = BuildRealBerniDossier();
        var stub = new StubLlmClient([PrematureThresholdJson, ValidBerniReentryPlanJson]);
        var strategy = new RollingAutopilotStrategy(stub);

        var outcome = await strategy.PlanAsync(new CoachPlanningContext
        {
            Dossier = dossier,
            Trigger = "morning_sync",
            MaxValidationRetries = 1
        });

        Assert.That(outcome.Success, Is.True, outcome.FailureReason);
        Assert.That(outcome.Attempts, Has.Count.EqualTo(2));
        Assert.That(outcome.Attempts[0].ValidationErrors.Select(e => e.Code), Does.Contain("reentry_requires_base_before_quality"));
        Assert.That(outcome.Attempts[1].ValidationErrors, Is.Empty);
        Assert.That(stub.RecordedRequests, Has.Count.EqualTo(2));
        Assert.That(stub.RecordedRequests[1].UserPrompt, Does.Contain("reentry_requires_base_before_quality"));
    }

    [Test]
    public async Task RollingAutopilotStrategyFailsSafelyWhenRetriesExhaustedOrLlmErrors()
    {
        var dossier = BuildRealBerniDossier();

        var failingValidationStub = new StubLlmClient([PrematureThresholdJson, PrematureThresholdJson]);
        var failedOutcome = await new RollingAutopilotStrategy(failingValidationStub).PlanAsync(new CoachPlanningContext
        {
            Dossier = dossier,
            Trigger = "morning_sync",
            MaxValidationRetries = 1
        });

        Assert.That(failedOutcome.Success, Is.False);
        Assert.That(failedOutcome.ReconciledSchedule, Is.Null);
        Assert.That(failedOutcome.Attempts, Has.Count.EqualTo(2));
        Assert.That(failedOutcome.TraceEntry.Success, Is.False);
        Assert.That(failedOutcome.FailureReason, Does.Contain("reentry_requires_base_before_quality"));

        var errorStub = new StubLlmClient([new LlmResponse(false, string.Empty, "Simulated timeout", "stub", 1.2)]);
        var errorOutcome = await new RollingAutopilotStrategy(errorStub).PlanAsync(new CoachPlanningContext
        {
            Dossier = dossier,
            Trigger = "morning_sync",
            MaxValidationRetries = 0
        });

        Assert.That(errorOutcome.Success, Is.False);
        Assert.That(errorOutcome.FailureReason, Does.Contain("Simulated timeout"));
        Assert.That(errorOutcome.TraceEntry.Success, Is.False);
    }

    [Test]
    public void PromptBuilderExtractsJsonFromMarkdownCodeFencesAndProse()
    {
        const string fenced = """
            Here is your updated 14-day schedule:
            ```json
            {
              "note": "Brace } inside string should not break extraction",
              "horizon_days": 14
            }
            ```
            Hope this helps!
            """;

        var extracted = AutopilotPromptBuilder.ExtractJsonPayload(fenced);
        Assert.That(extracted, Does.StartWith("{"));
        Assert.That(extracted, Does.EndWith("}"));
        Assert.That(extracted, Does.Contain("\"horizon_days\": 14"));
    }

    [Test]
    public void OpenMeteoParserMapsDailyForecastAndPaceFriendliness()
    {
        const string sampleJson = """
            {
              "daily": {
                "time": ["2026-11-24", "2026-11-25", "2026-11-26"],
                "weather_code": [2, 95, 0],
                "temperature_2m_max": [11.4, 15.2, 26.5],
                "temperature_2m_min": [4.1, 7.8, 16.0],
                "precipitation_sum": [0.0, 14.5, 0.0],
                "wind_gusts_10m_max": [18.0, 48.0, 12.0]
              }
            }
            """;

        var dossier = OpenMeteoWeatherClient.ParseOpenMeteoJson(sampleJson, new DateOnly(2026, 11, 25), "Gerasdorf");

        Assert.That(dossier.LocationLabel, Is.EqualTo("Gerasdorf"));
        Assert.That(dossier.Days, Has.Count.EqualTo(2));
        Assert.That(dossier.Days[0].Summary, Is.EqualTo("Storm"));
        Assert.That(dossier.Days[0].PaceIntervalFriendly, Is.False);
        Assert.That(dossier.Days[1].Summary, Is.EqualTo("Clear"));
        Assert.That(dossier.Days[1].PaceIntervalFriendly, Is.False);
    }

    [Test]
    public void AutopilotStateStorePersistsCompassAndTraceInIsolatedTempDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"laufen-state-test-{Guid.NewGuid():N}");
        var athletesDir = Path.Combine(tempRoot, "athletes");
        Directory.CreateDirectory(Path.Combine(athletesDir, "berni"));
        var layout = new WorkspaceLayout(tempRoot, athletesDir);

        try
        {
            Assert.That(AutopilotStateStore.LoadBlockCompass(layout, "berni"), Is.Null);

            var compass = new BlockCompassSnapshot
            {
                UpdatedAt = new DateOnly(2026, 11, 25),
                CurrentBlockFocus = "Aerobic consolidation",
                BaseMilestone = "60m Z2",
                QualityMilestone = "4x5m Z4",
                ObservedAthletePatterns = ["Clean comeback"],
                LastDecisionSummary = "Easy 45m Z2 today",
                HealthState = "ReEntry",
                AutoEventIds = [160591207]
            };

            AutopilotStateStore.SaveBlockCompass(layout, "berni", compass);
            var loaded = AutopilotStateStore.LoadBlockCompass(layout, "berni");
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.HealthState, Is.EqualTo("ReEntry"));

            var trace = new AutopilotDecisionTraceEntry
            {
                Timestamp = new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero),
                AthleteId = "i697752",
                ReferenceDate = new DateOnly(2026, 11, 25),
                Trigger = "morning_sync",
                Success = true,
                Model = "stub-model",
                AttemptCount = 1,
                HealthState = "ReEntry",
                UpsertCount = 3,
                DeleteCount = 1,
                WatchWindowDates = [new DateOnly(2026, 11, 25)],
                PreviewWindowDates = [],
                ProjectedEndingCtl = 13.5,
                ProjectedEndingAtl = 14.0,
                ProjectedMinTsb = -4.2,
                ValidationErrors = [],
                Rationale = null,
                FailureReason = null
            };

            AutopilotStateStore.AppendDecisionTrace(layout, "berni", trace);
            var tracePath = Path.Combine(athletesDir, "berni", "decision_trace_dotnet.jsonl");
            Assert.That(File.Exists(tracePath), Is.True);
            var lines = File.ReadAllLines(tracePath);
            Assert.That(lines, Has.Length.EqualTo(1));
            Assert.That(lines[0], Does.Contain("\"athlete_id\":\"i697752\""));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task GeminiApiClientParsesMockedHttpResponseCleanly()
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"candidates":[{"content":{"parts":[{"text":"{\"ok\":true}"}]}}]}""",
                Encoding.UTF8,
                "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new GeminiApiLlmClient("fake-key", "gemini-2.5-flash", httpClient);

        var resp = await client.GenerateAsync(new LlmRequest("sys", "user"));
        Assert.That(resp.Success, Is.True);
        Assert.That(resp.Text, Is.EqualTo("{\"ok\":true}"));
    }

    [Test]
    public void AgyLlmClientBuildsNonInteractiveArgumentsWithoutBarePrintFlag()
    {
        var client = new AgyLlmClient(model: "test-model", useSandbox: true);
        var args = client.BuildArguments(120);

        Assert.That(args, Does.Not.Contain("--print"));
        Assert.That(args, Does.Contain("--input-format"));
        Assert.That(args, Does.Contain("--output-format"));
        Assert.That(args, Does.Contain("--print-timeout"));
        Assert.That(args, Does.Contain("--model"));
        Assert.That(args, Does.Contain("test-model"));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
