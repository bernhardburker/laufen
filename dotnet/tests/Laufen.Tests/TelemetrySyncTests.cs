using System.Net;
using System.Text;
using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class TelemetrySyncTests
{
    private static readonly string[] ExpectedStreamRequests = ["run_new", "run_fail"];

    [Test]
    public void AthleteProfileSyncMapperPreservesSlotsAndHandlesMissingHrZones()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var existingBerni = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;

        var remoteWithRunSettings = new IntervalsAthleteProfileDto
        {
            Id = "i697752",
            Name = "BerniBurker",
            IcuRestingHr = 47,
            SportSettings =
            [
                new IntervalsSportSettingDto
                {
                    Types = ["Ride"],
                    Lthr = 160,
                    MaxHr = 180,
                    HrZones = [130, 145, 160],
                    HrZoneNames = ["Z1", "Z2", "Z3"]
                },
                new IntervalsSportSettingDto
                {
                    Types = ["Run"],
                    Lthr = 179,
                    MaxHr = 194,
                    HrZones = [151, 160, 169, 178, 194],
                    HrZoneNames = ["Zone 1", "Zone 2", "Zone 3", "Zone 4", "Zone 5"]
                }
            ]
        };

        var mapped = AthleteProfileSyncMapper.MapToDocument(remoteWithRunSettings, existingBerni);
        Assert.That(mapped.Athlete.HeartRate.RestingHr, Is.EqualTo(47));
        Assert.That(mapped.Athlete.HeartRate.Lthr, Is.EqualTo(179));
        Assert.That(mapped.Athlete.HeartRate.MaxHr, Is.EqualTo(194));
        Assert.That(mapped.Athlete.HeartRate.Zones, Has.Count.EqualTo(5));
        Assert.That(mapped.Athlete.HeartRate.Zones.Values.First(z => z.Zone == 2).Max, Is.EqualTo(160));
        Assert.That(mapped.Athlete.WeeklySlots, Has.Count.EqualTo(existingBerni.WeeklySlots!.Count));
        Assert.That(mapped.Athlete.TargetRunsPerWeek, Is.EqualTo(existingBerni.TargetRunsPerWeek));
        Assert.That(mapped.Athlete.TrainingMode, Is.EqualTo(existingBerni.TrainingMode));

        var remoteWithoutZones = new IntervalsAthleteProfileDto
        {
            Id = "i771119",
            Name = "Fallback Runner",
            RestingHr = 52,
            Lthr = 170,
            MaxHr = 190,
            SportSettings = []
        };

        var fallbackMapped = AthleteProfileSyncMapper.MapToDocument(remoteWithoutZones, existing: null);
        Assert.That(fallbackMapped.Athlete.HeartRate.Zones, Has.Count.EqualTo(5));
        Assert.That(fallbackMapped.Athlete.HeartRate.Zones["z2_aerobic"].Max, Is.EqualTo((int)(170 * 0.89)));
        Assert.That(fallbackMapped.Athlete.PaceTargetsMinPerKm, Is.Not.Null);
    }

    [Test]
    public async Task IntervalsIcuHttpClientDeserializesTolerantProfileAndPutsCanonicalNote()
    {
        string? capturedPutBody = null;
        string? capturedPutUrl = null;

        using var handler = new MockHttpMessageHandler(async req =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && url.EndsWith("/athlete/i771119", StringComparison.Ordinal))
            {
                const string profileJson = """
                    {
                      "id": "i771119",
                      "name": "Berni Test",
                      "icu_resting_hr": 49,
                      "unmapped_billing_tier": "pro",
                      "ui_preferences": { "theme": "dark" },
                      "sportSettings": [
                        {
                          "types": ["Run"],
                          "lthr": 176,
                          "max_hr": 192,
                          "hr_zones": [149, 158, 167, 176, 192],
                          "hr_zone_names": ["Zone 1", "Zone 2", "Zone 3", "Zone 4", "Zone 5"],
                          "extra_setting": true
                        }
                      ]
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(profileJson, Encoding.UTF8, "application/json")
                };
            }

            if (req.Method == HttpMethod.Put && url.Contains("/activity/i213105633", StringComparison.Ordinal))
            {
                capturedPutUrl = url;
                capturedPutBody = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
                const string actJson = """
                    {
                      "id": "i213105633",
                      "icu_athlete_id": "i771119",
                      "source": "GARMIN_CONNECT",
                      "start_date_local": "2026-11-22T09:15:00",
                      "type": "Run",
                      "name": "Lockerer Testlauf",
                      "description": "Wadenkrampf ab km 5",
                      "feel": 2
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(actJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(handler);
        var client = new IntervalsIcuHttpClient("secret-key", "https://intervals.icu/api/v1", httpClient);

        var profile = await client.GetAthleteProfileAsync("i771119");
        Assert.That(profile.Id, Is.EqualTo("i771119"));
        Assert.That(profile.IcuRestingHr, Is.EqualTo(49));
        Assert.That(profile.SportSettings, Has.Count.EqualTo(1));

        var updated = await client.UpdateActivityNoteAsync(
            "i771119",
            "i213105633",
            new ActivityNoteUpdateRequest { Description = "Wadenkrampf ab km 5", Feel = 2, PerceivedExertion = 7 });

        Assert.That(updated.Id, Is.EqualTo("i213105633"));
        Assert.That(capturedPutUrl, Does.EndWith("/activity/i213105633"));
        Assert.That(capturedPutBody, Does.Contain("\"description\":\"Wadenkrampf ab km 5\""));
        Assert.That(capturedPutBody, Does.Contain("\"feel\":2"));
        Assert.That(capturedPutBody, Does.Contain("\"perceived_exertion\":7"));
        Assert.That(capturedPutBody, Does.Not.Contain("athlete_note"));
    }

    [Test]
    public async Task TelemetrySyncServiceIsolatesShadowSyncSkipsExistingStreamsAndHandlesStreamErrors()
    {
        var tempRoot = CreateTempWorkspaceCopy("berni");
        try
        {
            var layout = WorkspaceLayout.FromRoot(tempRoot);
            var registry = AthleteDataRepository.LoadRegistry(layout);
            var berniEntry = registry.Athletes["berni"];

            var existingStreamPath = Path.Combine(layout.AthletesDirectory, "berni", "streams", "run_cached.json");
            Directory.CreateDirectory(Path.GetDirectoryName(existingStreamPath)!);
            File.WriteAllText(existingStreamPath, """{"time":[0,1],"heartrate":[140,141]}""");

            var originalActivities = File.ReadAllText(Path.Combine(layout.AthletesDirectory, "berni", "intervals_activities.json"));

            var streamRequests = new List<string>();
            using var handler = new MockHttpMessageHandler(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.EndsWith("/athlete/i697752", StringComparison.Ordinal))
                {
                    const string profJson = """
                        {
                          "id": "i697752",
                          "name": "BerniBurker",
                          "icu_resting_hr": 46,
                          "sportSettings": [
                            { "types": ["Run"], "lthr": 178, "max_hr": 192, "hr_zones": [150, 159, 168, 177, 192], "hr_zone_names": ["Zone 1", "Zone 2", "Zone 3", "Zone 4", "Zone 5"] }
                          ]
                        }
                        """;
                    return Task.FromResult(JsonResponse(profJson));
                }

                if (url.Contains("/activities", StringComparison.Ordinal))
                {
                    const string actsJson = """
                        [
                          { "id": "run_cached", "icu_athlete_id": "i697752", "source": "GARMIN_CONNECT", "start_date_local": "2026-11-24T08:00:00", "type": "Run", "name": "Cached", "average_heartrate": 144 },
                          { "id": "run_new", "icu_athlete_id": "i697752", "source": "GARMIN_CONNECT", "start_date_local": "2026-11-25T08:00:00", "type": "Run", "name": "New Run", "average_heartrate": 146 },
                          { "id": "run_fail", "icu_athlete_id": "i697752", "source": "GARMIN_CONNECT", "start_date_local": "2026-11-25T16:00:00", "type": "Run", "name": "Fail Stream", "has_heartrate": true }
                        ]
                        """;
                    return Task.FromResult(JsonResponse(actsJson));
                }

                if (url.Contains("/wellness", StringComparison.Ordinal))
                {
                    const string wellJson = """[{"id":"2026-11-25","updated":"2026-11-25T06:00:00Z","restingHR":46,"hrv":70.0,"tempWeight":false,"tempRestingHR":false}]""";
                    return Task.FromResult(JsonResponse(wellJson));
                }

                if (url.Contains("/events", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse("[]"));
                }

                if (url.Contains("/activity/run_new/streams", StringComparison.Ordinal))
                {
                    streamRequests.Add("run_new");
                    return Task.FromResult(JsonResponse("""[{"type":"time","data":[0,1,2]},{"type":"heartrate","data":[142,143,144]}]"""));
                }

                if (url.Contains("/activity/run_fail/streams", StringComparison.Ordinal))
                {
                    streamRequests.Add("run_fail");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("stream error", Encoding.UTF8, "text/plain")
                    });
                }

                if (url.Contains("/activity/run_cached/streams", StringComparison.Ordinal))
                {
                    streamRequests.Add("run_cached");
                    return Task.FromResult(JsonResponse("[]"));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            using var httpClient = new HttpClient(handler);
            var client = new IntervalsIcuHttpClient("secret-key", "https://intervals.icu/api/v1", httpClient);
            var refDate = new DateOnly(2026, 11, 25);

            // 1. WriteToWorkspace blocked on production tenant without allowProductionWrite
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await TelemetrySyncService.SyncAthleteAsync(
                    layout,
                    berniEntry,
                    "berni",
                    client,
                    refDate,
                    new TelemetrySyncOptions { DryRun = false, WriteToWorkspace = true },
                    allowProductionWrite: false));

            // 2. Default shadow sync writes cleanly to .dotnet_sync/ without modifying workspace files
            var summary = await TelemetrySyncService.SyncAthleteAsync(
                layout,
                berniEntry,
                "berni",
                client,
                refDate,
                new TelemetrySyncOptions { DryRun = false, WriteToWorkspace = false });

            Assert.That(summary.TargetDirectory, Does.EndWith(Path.Combine("berni", ".dotnet_sync")));
            Assert.That(summary.ProfileSynced, Is.True);
            Assert.That(summary.ActivitiesCount, Is.EqualTo(3));
            Assert.That(summary.WellnessCount, Is.EqualTo(1));
            Assert.That(summary.StreamsDownloaded, Is.EqualTo(1));
            Assert.That(summary.StreamsSkippedExisting, Is.EqualTo(1));
            Assert.That(summary.StreamsFailed, Is.EqualTo(1));
            Assert.That(summary.Warnings, Has.Count.EqualTo(1));
            Assert.That(streamRequests, Is.EquivalentTo(ExpectedStreamRequests));

            Assert.That(File.Exists(Path.Combine(summary.TargetDirectory, "intervals_activities.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(summary.TargetDirectory, "athlete.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(summary.TargetDirectory, "streams", "run_new.json")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(layout.AthletesDirectory, "berni", "intervals_activities.json")), Is.EqualTo(originalActivities));

            // Verify synced profile preserved weekly_slots from workspace athlete.json
            var syncedProfile = StrictJsonLoader.LoadFromFile<AthleteProfileDocument>(Path.Combine(summary.TargetDirectory, "athlete.json"));
            Assert.That(syncedProfile.Athlete.WeeklySlots, Is.Not.Null.And.Not.Empty);
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
    public async Task AttachNoteRecomputesComplaintRecoveryWithoutMutatingPythonFiles()
    {
        var tempRoot = CreateTempWorkspaceCopy("berni");
        try
        {
            var layout = WorkspaceLayout.FromRoot(tempRoot);
            var registry = AthleteDataRepository.LoadRegistry(layout);
            var originalActivities = File.ReadAllText(Path.Combine(layout.AthletesDirectory, "berni", "intervals_activities.json"));

            var result = await TelemetrySyncService.AttachNoteAsync(
                layout,
                registry,
                "berni",
                "Wadenkrampf ab km 5",
                activityId: null,
                feel: 2,
                perceivedExertion: 7,
                client: null,
                pushRemote: false,
                dryRun: false);

            Assert.That(result.ReviewRecomputed, Is.True);
            Assert.That(result.RemoteUpdated, Is.False);
            Assert.That(result.UpdatedReview, Is.Not.Null);
            Assert.That(result.UpdatedReview!.Review.Stages.Stage4NextRun.RecoveryHours, Is.EqualTo(48));
            Assert.That(result.UpdatedReview.Review.Stages.Stage4NextRun.NextWorkoutType, Is.EqualTo("recovery_run"));
            Assert.That(File.ReadAllText(Path.Combine(layout.AthletesDirectory, "berni", "intervals_activities.json")), Is.EqualTo(originalActivities));

            var savedDotNetReviews = AutopilotStateStore.LoadDotNetRunReviews(layout, "berni");
            Assert.That(savedDotNetReviews.ContainsKey(result.ActivityId), Is.True);
            Assert.That(savedDotNetReviews[result.ActivityId].AthleteNote, Is.EqualTo("Wadenkrampf ab km 5"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string CreateTempWorkspaceCopy(string slug)
    {
        var source = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"laufen-sync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "athletes", slug));

        File.Copy(
            Path.Combine(source.AthletesDirectory, "registry.json"),
            Path.Combine(tempRoot, "athletes", "registry.json"));

        foreach (var file in Directory.GetFiles(Path.Combine(source.AthletesDirectory, slug)))
        {
            File.Copy(file, Path.Combine(tempRoot, "athletes", slug, Path.GetFileName(file)));
        }

        return tempRoot;
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request);
    }
}
