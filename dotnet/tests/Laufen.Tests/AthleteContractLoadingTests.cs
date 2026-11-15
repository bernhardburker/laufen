using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class AthleteContractLoadingTests
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
    public void LoadRegistryFromRealWorkspaceDeserializesAllTenants()
    {
        var layout = ResolveRepositoryLayout();

        var registry = StrictJsonLoader.LoadRegistry(layout);

        Assert.That(registry.DefaultAthlete, Is.EqualTo("berni"));
        Assert.That(registry.Athletes.ContainsKey("berni"), Is.True);
        Assert.That(registry.Athletes.ContainsKey("berni-test"), Is.True);

        var berni = registry.Athletes["berni"];
        Assert.That(berni.IntervalsId, Is.EqualTo("i697752"));
        Assert.That(berni.Active, Is.True);
        Assert.That(berni.TelegramChatId, Is.EqualTo("157754568"));
        Assert.That(berni.BriefingEnabled, Is.True);

        var berniTest = registry.Athletes["berni-test"];
        Assert.That(berniTest.IntervalsId, Is.EqualTo("i771119"));
        Assert.That(berniTest.Active, Is.False);
        Assert.That(berniTest.TelegramChatId, Is.Null);
        Assert.That(berniTest.BriefingEnabled, Is.Null);
    }

    [Test]
    public void LoadAthleteProfileForBerniDeserializesZonesAndWeeklySlots()
    {
        var layout = ResolveRepositoryLayout();

        var document = StrictJsonLoader.LoadAthleteProfile(layout, "berni");
        var athlete = document.Athlete;

        Assert.That(athlete.Id, Is.EqualTo("i697752"));
        Assert.That(athlete.Name, Is.EqualTo("BerniBurker"));
        Assert.That(athlete.DisplayName, Is.Null);
        Assert.That(athlete.TargetRunsPerWeek, Is.EqualTo(3));
        Assert.That(athlete.WeeklySlots, Is.Not.Null);
        Assert.That(athlete.WeeklySlots!, Has.Count.EqualTo(3));

        Assert.That(athlete.HeartRate.RestingHr, Is.EqualTo(46));
        Assert.That(athlete.HeartRate.MaxHr, Is.EqualTo(204));
        Assert.That(athlete.HeartRate.Lthr, Is.EqualTo(178));
        Assert.That(athlete.HeartRate.Zones, Has.Count.EqualTo(7));
        Assert.That(athlete.HeartRate.RawHrZones, Has.Count.EqualTo(7));
        Assert.That(athlete.HeartRate.RawZoneNames, Has.Count.EqualTo(7));
        Assert.That(athlete.PaceTargetsMinPerKm, Is.Not.Null);
        Assert.That(athlete.PaceTargetsMinPerKm!, Has.Count.EqualTo(4));
    }

    [Test]
    public void LoadAthleteProfileForBerniTestDeserializesMinimalProfileWithoutWeeklySlots()
    {
        var layout = ResolveRepositoryLayout();

        var document = StrictJsonLoader.LoadAthleteProfile(layout, "berni-test");
        var athlete = document.Athlete;

        Assert.That(athlete.Id, Is.EqualTo("i771119"));
        Assert.That(athlete.DisplayName, Is.EqualTo("Berni Test"));
        Assert.That(athlete.WeeklySlots, Is.Null);
        Assert.That(athlete.TrainingMode, Is.Null);
        Assert.That(athlete.HeartRate.MaxHr, Is.EqualTo(202));
        Assert.That(athlete.HeartRate.Zones, Has.Count.EqualTo(7));
    }

    [Test]
    public void StrictDeserializerRejectsUnknownJsonMembers()
    {
        const string jsonWithDrift = """
            {
              "default_athlete": "berni",
              "unexpected_field": 123,
              "athletes": {}
            }
            """;

        Assert.Throws<JsonException>(() => StrictJsonLoader.Deserialize<AthleteRegistry>(jsonWithDrift));
    }

    [Test]
    public void StrictDeserializerRejectsNullDictionaryEntryInAthletes()
    {
        const string jsonWithNullEntry = """
            {
              "default_athlete": "berni",
              "athletes": {
                "berni": null
              }
            }
            """;

        Assert.Throws<JsonException>(() => StrictJsonLoader.Deserialize<AthleteRegistry>(jsonWithNullEntry));
    }
}
