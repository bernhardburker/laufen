using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Aggregated athlete data loaded from disk for physiological reporting and planning marker evaluation.
/// </summary>
public sealed record AthleteWorkspaceData
{
    public required string Slug { get; init; }
    public required AthleteRegistryEntry RegistryEntry { get; init; }
    public required AthleteProfile Profile { get; init; }
    public required IReadOnlyList<IntervalsActivityEntry> Activities { get; init; }
    public required IReadOnlyList<IntervalsWellnessEntry> WellnessRecords { get; init; }
    public required IReadOnlyList<CoachHistoryEntry> CoachHistory { get; init; }
    public required IReadOnlyList<CalendarEventEntry> CalendarEvents { get; init; }
}

/// <summary>
/// Loads validated multi-tenant athlete datasets from <see cref="WorkspaceLayout"/>.
/// </summary>
public static class AthleteDataRepository
{
    public static AthleteRegistry LoadRegistry(WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return StrictJsonLoader.LoadRegistry(layout);
    }

    public static IReadOnlyList<string> ResolveTargetSlugs(
        AthleteRegistry registry,
        string? requestedSlug,
        bool allActive)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (allActive)
        {
            return registry.Athletes
                .Where(kvp => kvp.Value.Active)
                .Select(kvp => kvp.Key)
                .ToList();
        }

        var slug = string.IsNullOrWhiteSpace(requestedSlug)
            ? registry.DefaultAthlete
            : requestedSlug.Trim();

        if (!registry.Athletes.ContainsKey(slug))
        {
            var available = string.Join(", ", registry.Athletes.Keys.OrderBy(k => k, StringComparer.Ordinal));
            throw new KeyNotFoundException($"Unknown athlete slug '{slug}'. Available athletes: {available}");
        }

        return [slug];
    }

    public static AthleteWorkspaceData LoadAthleteData(WorkspaceLayout layout, AthleteRegistry registry, string slug)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        if (!registry.Athletes.TryGetValue(slug, out var registryEntry))
        {
            throw new KeyNotFoundException($"Unknown athlete slug '{slug}'.");
        }

        var athleteDir = Path.Combine(layout.AthletesDirectory, slug);
        var profileDoc = StrictJsonLoader.LoadAthleteProfile(layout, slug);
        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, slug);

        var wellnessPath = Path.Combine(athleteDir, "intervals_wellness.json");
        var wellness = File.Exists(wellnessPath)
            ? StrictJsonLoader.LoadIntervalsWellness(layout, slug)
            : [];

        var historyPath = Path.Combine(athleteDir, "coach_history.json");
        var history = File.Exists(historyPath)
            ? StrictJsonLoader.LoadCoachHistory(layout, slug)
            : [];

        var eventsPath = Path.Combine(athleteDir, "calendar_events.json");
        var calendarEvents = File.Exists(eventsPath)
            ? StrictJsonLoader.LoadCalendarEvents(layout, slug)
            : [];

        return new AthleteWorkspaceData
        {
            Slug = slug,
            RegistryEntry = registryEntry,
            Profile = profileDoc.Athlete,
            Activities = activities,
            WellnessRecords = wellness,
            CoachHistory = history,
            CalendarEvents = calendarEvents
        };
    }
}
