using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class IntervalsActivityContractTests
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

    [Test]
    public void LoadIntervalsActivitiesForBerniDeserializesAllActivitiesStrictly()
    {
        var layout = ResolveRepositoryLayout();

        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, "berni");

        Assert.That(activities, Is.Not.Empty);
        Assert.That(activities.All(a => !string.IsNullOrWhiteSpace(a.Id)), Is.True);
        Assert.That(activities.All(a => !string.IsNullOrWhiteSpace(a.Source)), Is.True);
        Assert.That(activities.Any(a => a.Type == "Run"), Is.True);
        Assert.That(activities.Any(a => a.Type is null), Is.True);
        Assert.That(activities.Any(a => a.IcuHrr is not null), Is.True);
    }

    [Test]
    public void LoadIntervalsActivitiesForBerniTestReturnsEmptyList()
    {
        var layout = ResolveRepositoryLayout();

        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, "berni-test");

        Assert.That(activities, Is.Empty);
    }

    [Test]
    public void StrictDeserializerRejectsUnknownActivityField()
    {
        const string jsonWithDrift = """
            [
              {
                "id": "i999",
                "unexpected_activity_field": 123
              }
            ]
            """;

        Assert.Throws<JsonException>(() => StrictJsonLoader.Deserialize<List<IntervalsActivityEntry>>(jsonWithDrift));
    }
}
