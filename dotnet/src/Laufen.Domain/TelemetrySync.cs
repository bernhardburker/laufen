using System.Globalization;
using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Tolerant DTO for Intervals.icu <c>sportSettings</c> entries returned by <c>GET /api/v1/athlete/{id}</c>.
/// </summary>
public sealed record IntervalsSportSettingDto
{
    [JsonPropertyName("types")]
    public IReadOnlyList<string>? Types { get; init; }

    [JsonPropertyName("lthr")]
    public int? Lthr { get; init; }

    [JsonPropertyName("max_hr")]
    public int? MaxHr { get; init; }

    [JsonPropertyName("hr_zones")]
    public IReadOnlyList<int>? HrZones { get; init; }

    [JsonPropertyName("hr_zone_names")]
    public IReadOnlyList<string>? HrZoneNames { get; init; }
}

/// <summary>
/// Tolerant DTO for Intervals.icu <c>GET /api/v1/athlete/{id}</c> responses.
/// </summary>
public sealed record IntervalsAthleteProfileDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("icu_resting_hr")]
    public int? IcuRestingHr { get; init; }

    [JsonPropertyName("resting_hr")]
    public int? RestingHr { get; init; }

    [JsonPropertyName("lthr")]
    public int? Lthr { get; init; }

    [JsonPropertyName("max_hr")]
    public int? MaxHr { get; init; }

    [JsonPropertyName("sportSettings")]
    public IReadOnlyList<IntervalsSportSettingDto>? SportSettings { get; init; }
}

/// <summary>
/// Canonical Intervals.icu payload for <c>PUT /api/v1/activity/{id}</c> subjective note updates.
/// </summary>
public sealed record ActivityNoteUpdateRequest
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("feel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Feel { get; init; }

    [JsonPropertyName("perceived_exertion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PerceivedExertion { get; init; }
}

public sealed record TelemetrySyncOptions
{
    public int ActivitiesLookbackDays { get; init; } = 180;
    public int WellnessLookbackDays { get; init; } = 90;
    public int CalendarPastDays { get; init; } = 14;
    public int CalendarFutureDays { get; init; } = 21;
    public bool SyncProfile { get; init; } = true;
    public bool SyncStreams { get; init; } = true;
    public bool DryRun { get; init; } = true;
    public bool WriteToWorkspace { get; init; }
}

public sealed record TelemetrySyncSummary
{
    public required string AthleteSlug { get; init; }
    public required string IntervalsAthleteId { get; init; }
    public required DateOnly ReferenceDate { get; init; }
    public required string TargetDirectory { get; init; }
    public required bool DryRun { get; init; }
    public required bool ProfileSynced { get; init; }
    public required int ActivitiesCount { get; init; }
    public required int WellnessCount { get; init; }
    public required int CalendarEventsCount { get; init; }
    public required int StreamsDownloaded { get; init; }
    public required int StreamsSkippedExisting { get; init; }
    public required int StreamsFailed { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record ActivityNoteResult
{
    public required string AthleteSlug { get; init; }
    public required string ActivityId { get; init; }
    public required string ActivityName { get; init; }
    public required string NoteText { get; init; }
    public int? Feel { get; init; }
    public int? PerceivedExertion { get; init; }
    public required bool RemoteUpdated { get; init; }
    public required bool ReviewRecomputed { get; init; }
    public RunReviewRecord? UpdatedReview { get; init; }
    public required bool DryRun { get; init; }
}

/// <summary>
/// Pure mapper transforming Intervals.icu athlete profiles into <see cref="AthleteProfileDocument"/>
/// while preserving all athlete-specific periodization slots, goals, and time budgets.
/// </summary>
public static class AthleteProfileSyncMapper
{
    private static readonly Dictionary<string, string> DefaultPaceTargets = new(StringComparer.Ordinal)
    {
        ["easy_run"] = "06:30 - 07:00",
        ["long_run"] = "06:40 - 07:15",
        ["tempo_run"] = "05:45 - 06:00",
        ["intervals_1k"] = "05:00 - 05:20"
    };

    public static AthleteProfileDocument MapToDocument(
        IntervalsAthleteProfileDto remote,
        AthleteProfile? existing = null,
        string? fallbackAthleteId = null)
    {
        ArgumentNullException.ThrowIfNull(remote);

        var runSettings = (remote.SportSettings ?? [])
            .FirstOrDefault(s => (s.Types ?? [])
                .Any(t => string.Equals(t, "Run", StringComparison.OrdinalIgnoreCase)));

        var lthr = runSettings?.Lthr is > 0
            ? runSettings.Lthr.Value
            : (remote.Lthr is > 0 ? remote.Lthr.Value : (existing?.HeartRate.Lthr is > 0 ? existing.HeartRate.Lthr : 168));

        var maxHr = runSettings?.MaxHr is > 0
            ? runSettings.MaxHr.Value
            : (remote.MaxHr is > 0 ? remote.MaxHr.Value : (existing?.HeartRate.MaxHr is > 0 ? existing.HeartRate.MaxHr : 185));

        var restingHr = remote.IcuRestingHr is > 0
            ? remote.IcuRestingHr.Value
            : (remote.RestingHr is > 0 ? remote.RestingHr.Value : (existing?.HeartRate.RestingHr is > 0 ? existing.HeartRate.RestingHr : 55));

        var rawCutoffs = (runSettings?.HrZones ?? [])
            .Where(z => z > 0)
            .ToList();
        var rawNames = (runSettings?.HrZoneNames ?? [])
            .Select(n => n?.Trim() ?? string.Empty)
            .ToList();

        var zones = BuildZonesDictionary(rawCutoffs, rawNames, lthr, maxHr);

        var resolvedId = !string.IsNullOrWhiteSpace(remote.Id)
            ? remote.Id.Trim()
            : (!string.IsNullOrWhiteSpace(existing?.Id) ? existing.Id : (fallbackAthleteId ?? "0"));

        var resolvedName = !string.IsNullOrWhiteSpace(remote.Name)
            ? remote.Name.Trim()
            : (!string.IsNullOrWhiteSpace(existing?.Name) ? existing.Name : "Runner");

        var paceTargets = existing?.PaceTargetsMinPerKm is { Count: > 0 }
            ? new Dictionary<string, string>(existing.PaceTargetsMinPerKm, StringComparer.Ordinal)
            : new Dictionary<string, string>(DefaultPaceTargets, StringComparer.Ordinal);

        return new AthleteProfileDocument
        {
            Athlete = new AthleteProfile
            {
                Id = resolvedId,
                Name = resolvedName,
                DisplayName = existing?.DisplayName,
                Classification = existing?.Classification,
                TrainingMode = existing?.TrainingMode,
                TrainingModelName = existing?.TrainingModelName,
                TargetRunsPerWeek = existing?.TargetRunsPerWeek,
                AllowConsecutiveRunDays = existing?.AllowConsecutiveRunDays,
                WeeklySlots = existing?.WeeklySlots,
                HeartRate = new HeartRateProfile
                {
                    RestingHr = restingHr,
                    MaxHr = maxHr,
                    Lthr = lthr,
                    Zones = zones,
                    RawHrZones = rawCutoffs,
                    RawZoneNames = rawNames
                },
                PaceTargetsMinPerKm = paceTargets,
                Goals = existing?.Goals,
                TimeBudgets = existing?.TimeBudgets,
                PreferredRunDays = existing?.PreferredRunDays
            }
        };
    }

    private static Dictionary<string, HeartRateZone> BuildZonesDictionary(
        List<int> rawCutoffs,
        List<string> rawNames,
        int lthr,
        int maxHr)
    {
        var zones = new Dictionary<string, HeartRateZone>(StringComparer.Ordinal);
        if (rawCutoffs.Count > 0)
        {
            var prevCutoff = 0;
            for (var i = 0; i < rawCutoffs.Count; i++)
            {
                var zNum = i + 1;
                var cutoff = rawCutoffs[i];
                var zName = i < rawNames.Count && !string.IsNullOrWhiteSpace(rawNames[i])
                    ? rawNames[i]
                    : string.Create(CultureInfo.InvariantCulture, $"Zone {zNum}");
                var normalized = zName.ToLowerInvariant().Replace(' ', '_');
                var key = string.Create(CultureInfo.InvariantCulture, $"z{zNum}_{normalized}");

                zones[key] = new HeartRateZone
                {
                    Zone = zNum,
                    Name = zName,
                    Min = prevCutoff > 0 ? prevCutoff + 1 : 0,
                    Max = cutoff
                };
                prevCutoff = cutoff;
            }

            return zones;
        }

        var z1Max = (int)(lthr * 0.85);
        var z2Max = (int)(lthr * 0.89);
        var z3Max = (int)(lthr * 0.94);
        var z4Max = (int)(lthr * 1.05);

        zones["z1_recovery"] = new HeartRateZone { Zone = 1, Name = "Recovery", Min = 0, Max = z1Max };
        zones["z2_aerobic"] = new HeartRateZone { Zone = 2, Name = "Aerobic", Min = z1Max + 1, Max = z2Max };
        zones["z3_tempo"] = new HeartRateZone { Zone = 3, Name = "Tempo", Min = z2Max + 1, Max = z3Max };
        zones["z4_threshold"] = new HeartRateZone { Zone = 4, Name = "Threshold", Min = z3Max + 1, Max = z4Max };
        zones["z5_anaerobic"] = new HeartRateZone { Zone = 5, Name = "Anaerobic", Min = z4Max + 1, Max = maxHr };
        return zones;
    }
}
