using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class RunReviewContractTests
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

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void LoadRunReviewsFromRealWorkspaceDeserializesStrictly(string slug)
    {
        var layout = ResolveRepositoryLayout();

        var reviews = StrictJsonLoader.LoadRunReviews(layout, slug);

        Assert.That(reviews, Is.Not.Empty);
        Assert.That(reviews.All(kvp => !string.IsNullOrWhiteSpace(kvp.Key)), Is.True);
        Assert.That(reviews.All(kvp => kvp.Key == kvp.Value.Id), Is.True);
        Assert.That(reviews.All(kvp => kvp.Value.Review.SchemaVersion == 2), Is.True);
    }

    [Test]
    public void LoadRunReviewsExposesBerniVsBerniTestSchemaDivergence()
    {
        var layout = ResolveRepositoryLayout();

        var berniReviews = StrictJsonLoader.LoadRunReviews(layout, "berni");
        var berniTestReviews = StrictJsonLoader.LoadRunReviews(layout, "berni-test");

        Assert.That(
            berniReviews.Values.Any(r => r.Review.Stages.Stage3Optimizations.Any(o => !string.IsNullOrWhiteSpace(o.ActionableAdvice))),
            Is.True);
        Assert.That(
            berniTestReviews.Values.All(r => r.Review.Stages.Stage3Optimizations.All(o => o.ActionableAdvice is null)),
            Is.True);
    }

    [Test]
    public void StrictDeserializerRejectsUnknownRunReviewField()
    {
        const string jsonWithDrift = """
            {
              "i123": {
                "id": "i123",
                "date": "2026-10-12T08:30:00",
                "name": "Morning Run",
                "distance_km": 5.0,
                "moving_time_s": 1800,
                "moving_time_str": "30m",
                "pace_str": "6:00/km",
                "avg_hr": 140,
                "max_hr": 152,
                "cadence_spm": 168.0,
                "elevation_gain_m": 20.0,
                "training_load": 35.0,
                "z1_z2_pct": 95.0,
                "zone_breakdown": {},
                "intervals": [],
                "intensity_class": "easy",
                "analyzed_at": "2026-10-12T10:00:00+00:00",
                "unexpected_review_field": true,
                "review": {
                  "schema_version": 2,
                  "rating": "ok",
                  "rating_label": "OK",
                  "summary": "Good",
                  "coach_tip": "Keep going",
                  "cadence_feedback": "Steady",
                  "zone_distribution_text": "Z2",
                  "stages": {
                    "stage1_physiology": {
                      "headline": "Solid",
                      "parsed_intervals": [],
                      "summary": "Good"
                    },
                    "stage2_compliance": {
                      "status": "ok",
                      "status_label": "OK",
                      "intended_type": "easy_run",
                      "intent_source": "slot",
                      "planned_slot": "Slot 1",
                      "target_comparison": "Match",
                      "compliance_notes": "Clean"
                    },
                    "stage3_optimizations": [],
                    "stage4_next_run": {
                      "recovery_hours": 24,
                      "tsb_context": "Neutral",
                      "next_slot": "Slot 2",
                      "next_workout_type": "easy_run",
                      "target_hr_max": 158,
                      "duration_min": 45,
                      "directive": "Easy",
                      "optimization_focus": "Base"
                    }
                  }
                }
              }
            }
            """;

        Assert.Throws<JsonException>(() => StrictJsonLoader.Deserialize<Dictionary<string, RunReviewRecord>>(jsonWithDrift));
    }
}
