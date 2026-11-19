using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class WellnessAndCoachHistoryContractTests
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
    public void LoadCoachHistoryFromRealWorkspaceDeserializesStrictly(string slug)
    {
        var layout = ResolveRepositoryLayout();

        var entries = StrictJsonLoader.LoadCoachHistory(layout, slug);

        Assert.That(entries, Is.Not.Empty);
        Assert.That(entries.All(e => !string.IsNullOrWhiteSpace(e.Id)), Is.True);
        Assert.That(entries.All(e => !string.IsNullOrWhiteSpace(e.Status)), Is.True);
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void LoadIntervalsWellnessFromRealWorkspaceDeserializesStrictly(string slug)
    {
        var layout = ResolveRepositoryLayout();

        var entries = StrictJsonLoader.LoadIntervalsWellness(layout, slug);

        Assert.That(entries, Is.Not.Empty);
        Assert.That(entries.All(e => e.Id != default), Is.True);
    }

    [Test]
    public void LoadIntervalsWellnessExposesBerniVsBerniTestNullabilityDivergence()
    {
        var layout = ResolveRepositoryLayout();

        var berniWellness = StrictJsonLoader.LoadIntervalsWellness(layout, "berni");
        var berniTestWellness = StrictJsonLoader.LoadIntervalsWellness(layout, "berni-test");

        Assert.That(berniWellness.All(w => w.Ctl.HasValue && w.Atl.HasValue && w.RestingHr.HasValue), Is.True);
        Assert.That(berniTestWellness.Any(w => !w.Ctl.HasValue || !w.RestingHr.HasValue), Is.True);
    }

    [Test]
    public void StrictDeserializerRejectsUnknownWellnessField()
    {
        const string jsonWithDrift = """
            [
              {
                "id": "2026-10-12",
                "updated": "2026-10-13T05:43:06.849+00:00",
                "tempWeight": false,
                "tempRestingHR": false,
                "unexpected_wellness_metric": 42
              }
            ]
            """;

        Assert.Throws<JsonException>(() => StrictJsonLoader.Deserialize<List<IntervalsWellnessEntry>>(jsonWithDrift));
    }

    [Test]
    public void StrictDeserializerRejectsNullRunIdInCoachHistory()
    {
        const string jsonWithNullRunId = """
            [
              {
                "id": "cycle-001",
                "date": "2026-10-05",
                "recommendation": "Easy run",
                "target_type": "easy_run",
                "target_hr_max": 158,
                "status": "erfüllt",
                "actual": "Done",
                "effect": "Good",
                "evaluated_run_ids": [null]
              }
            ]
            """;

        Assert.Throws<JsonException>(() => StrictJsonLoader.Deserialize<List<CoachHistoryEntry>>(jsonWithNullRunId));
    }
}
