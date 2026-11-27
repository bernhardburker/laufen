using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Pulls athlete profiles, activities, incremental streams, wellness, and calendar events from Intervals.icu,
/// persisting outputs atomically into an isolated <c>.dotnet_sync/</c> directory by default, and supports
/// attaching subjective activity notes with 4-stage review recomputation.
/// </summary>
public static class TelemetrySyncService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<TelemetrySyncSummary> SyncAthleteAsync(
        WorkspaceLayout layout,
        AthleteRegistryEntry entry,
        string slug,
        IIntervalsIcuClient client,
        DateOnly refDate,
        TelemetrySyncOptions options,
        bool allowProductionWrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var workspaceAthleteDir = Path.Combine(layout.AthletesDirectory, slug);
        var targetDir = options.WriteToWorkspace
            ? workspaceAthleteDir
            : (!string.IsNullOrWhiteSpace(options.OutputDirectory)
                ? Path.GetFullPath(options.OutputDirectory)
                : Path.Combine(workspaceAthleteDir, ".dotnet_sync"));

        if (options.WriteToWorkspace && !options.DryRun)
        {
            var permission = StagingSafetyGuard.EvaluateWritePermission(slug, entry, allowProductionWrite);
            if (!permission.Allowed)
            {
                throw new InvalidOperationException(permission.Reason);
            }
        }

        var warnings = new List<string>();
        AthleteProfileDocument? existingDoc = null;
        var existingProfilePath = Path.Combine(workspaceAthleteDir, "athlete.json");
        if (File.Exists(existingProfilePath))
        {
            existingDoc = StrictJsonLoader.LoadAthleteProfile(layout, slug);
        }

        AthleteProfileDocument? syncedProfileDoc = null;
        var profileSynced = false;
        if (options.SyncProfile)
        {
            var remoteProfile = await client.GetAthleteProfileAsync(entry.IntervalsId, cancellationToken).ConfigureAwait(false);
            syncedProfileDoc = AthleteProfileSyncMapper.MapToDocument(remoteProfile, existingDoc?.Athlete, entry.IntervalsId);
            profileSynced = true;
        }

        var activitiesOldest = options.ActivitiesOldest
            ?? (options.ActivitiesLookbackDays.HasValue
                ? refDate.AddDays(-Math.Max(1, options.ActivitiesLookbackDays.Value))
                : new DateOnly(2000, 1, 1));
        var activitiesNewest = refDate.AddDays(1);
        var activities = await client.GetActivitiesAsync(entry.IntervalsId, activitiesOldest, activitiesNewest, cancellationToken).ConfigureAwait(false);

        var wellnessOldest = refDate.AddDays(-Math.Max(1, options.WellnessLookbackDays));
        var wellness = await client.GetWellnessAsync(entry.IntervalsId, wellnessOldest, refDate, cancellationToken).ConfigureAwait(false);

        var eventsOldest = refDate.AddDays(-Math.Max(0, options.CalendarPastDays));
        var eventsNewest = refDate.AddDays(Math.Max(1, options.CalendarFutureDays));
        var calendarEvents = await client.GetCalendarEventsAsync(entry.IntervalsId, eventsOldest, eventsNewest, cancellationToken).ConfigureAwait(false);

        var streamsDownloaded = 0;
        var streamsSkippedExisting = 0;
        var streamsFailed = 0;
        var fetchedStreams = new Dictionary<string, ActivityStreamData>(StringComparer.Ordinal);
        var workspaceStreamsDir = Path.Combine(workspaceAthleteDir, "streams");
        var targetStreamsDir = Path.Combine(targetDir, "streams");

        if (options.SyncStreams)
        {
            foreach (var act in activities)
            {
                if (!string.Equals(act.Type, "Run", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(act.Id)
                    || (!act.AverageHeartrate.HasValue && act.HasHeartrate != true))
                {
                    continue;
                }

                var targetStreamFile = Path.Combine(targetStreamsDir, $"{act.Id}.json");
                var workspaceStreamFile = Path.Combine(workspaceStreamsDir, $"{act.Id}.json");
                if (File.Exists(targetStreamFile) || File.Exists(workspaceStreamFile))
                {
                    streamsSkippedExisting++;
                    if (!options.DryRun && !File.Exists(targetStreamFile) && File.Exists(workspaceStreamFile))
                    {
                        Directory.CreateDirectory(targetStreamsDir);
                        File.Copy(workspaceStreamFile, targetStreamFile, overwrite: false);
                    }

                    continue;
                }

                try
                {
                    var stream = await client.GetActivityStreamAsync(act.Id, cancellationToken).ConfigureAwait(false);
                    if (stream is not null)
                    {
                        fetchedStreams[act.Id] = stream;
                        streamsDownloaded++;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
                {
                    streamsFailed++;
                    warnings.Add($"Stream fetch failed for '{act.Id}': {ex.Message}");
                }
            }
        }

        if (!options.DryRun)
        {
            Directory.CreateDirectory(targetDir);
            WriteJsonAtomically(Path.Combine(targetDir, "intervals_activities.json"), activities, IndentedJsonOptions);
            WriteJsonAtomically(Path.Combine(targetDir, "intervals_wellness.json"), wellness, IndentedJsonOptions);
            WriteJsonAtomically(Path.Combine(targetDir, "calendar_events.json"), calendarEvents, IndentedJsonOptions);

            if (syncedProfileDoc is not null)
            {
                WriteJsonAtomically(Path.Combine(targetDir, "athlete.json"), syncedProfileDoc, IndentedJsonOptions);
            }

            if (fetchedStreams.Count > 0)
            {
                Directory.CreateDirectory(targetStreamsDir);
                foreach (var (actId, stream) in fetchedStreams)
                {
                    WriteJsonAtomically(Path.Combine(targetStreamsDir, $"{actId}.json"), stream, CompactJsonOptions);
                }
            }
        }

        return new TelemetrySyncSummary
        {
            AthleteSlug = slug,
            IntervalsAthleteId = entry.IntervalsId,
            ReferenceDate = refDate,
            TargetDirectory = targetDir,
            DryRun = options.DryRun,
            ProfileSynced = profileSynced,
            ActivitiesCount = activities.Count,
            WellnessCount = wellness.Count,
            CalendarEventsCount = calendarEvents.Count,
            StreamsDownloaded = streamsDownloaded,
            StreamsSkippedExisting = streamsSkippedExisting,
            StreamsFailed = streamsFailed,
            Warnings = warnings
        };
    }

    public static async Task<ActivityNoteResult> AttachNoteAsync(
        WorkspaceLayout layout,
        AthleteRegistry registry,
        string slug,
        string noteText,
        string? activityId = null,
        int? feel = null,
        int? perceivedExertion = null,
        IIntervalsIcuClient? client = null,
        bool pushRemote = false,
        bool dryRun = true,
        bool allowProductionWrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(noteText);

        var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
        var validRuns = PhysiologicalCalculator.FilterValidRuns(data.Activities)
            .OrderByDescending(a => a.StartDateLocal)
            .ToList();

        if (validRuns.Count == 0)
        {
            throw new InvalidOperationException($"No valid running activities found for '{slug}'.");
        }

        var targetRun = string.IsNullOrWhiteSpace(activityId)
            ? validRuns[0]
            : validRuns.FirstOrDefault(r => string.Equals(r.Id, activityId.Trim(), StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"Activity '{activityId}' not found among valid runs for '{slug}'.");

        var trimmedNote = noteText.Trim();
        var remoteUpdated = false;

        if (pushRemote && !dryRun)
        {
            if (client is null)
            {
                throw new InvalidOperationException("Intervals.icu API client is required when --push is requested.");
            }

            var permission = StagingSafetyGuard.EvaluateWritePermission(slug, data.RegistryEntry, allowProductionWrite);
            if (!permission.Allowed)
            {
                throw new InvalidOperationException(permission.Reason);
            }

            var updateRequest = new ActivityNoteUpdateRequest
            {
                Description = trimmedNote,
                Feel = feel,
                PerceivedExertion = perceivedExertion
            };
            await client.UpdateActivityNoteAsync(data.RegistryEntry.IntervalsId, targetRun.Id, updateRequest, cancellationToken).ConfigureAwait(false);
            remoteUpdated = true;
        }

        var updatedRun = targetRun with
        {
            Description = trimmedNote,
            Feel = feel.HasValue ? feel.Value : targetRun.Feel,
            PerceivedExertion = perceivedExertion.HasValue
                ? JsonSerializer.SerializeToElement(perceivedExertion.Value)
                : targetRun.PerceivedExertion
        };

        var stream = StrictJsonLoader.LoadActivityStream(layout, slug, targetRun.Id);
        CalendarEventEntry? pairedEvt = null;
        if (targetRun.PairedEventId.HasValue)
        {
            pairedEvt = data.CalendarEvents.FirstOrDefault(e => e.Id == targetRun.PairedEventId.Value);
        }

        var openCycle = data.CoachHistory
            .Where(c => string.Equals(c.Status, "offen", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Date)
            .FirstOrDefault();

        var recomputedReview = PostRunReviewCalculator.EvaluateRun(
            updatedRun,
            data.Profile,
            stream,
            pairedEvt,
            openCycle);

        if (!dryRun)
        {
            var existingReviews = AutopilotStateStore.LoadDotNetRunReviews(layout, slug);
            var nextReviews = new Dictionary<string, RunReviewRecord>(existingReviews, StringComparer.Ordinal)
            {
                [targetRun.Id] = recomputedReview
            };
            AutopilotStateStore.SaveDotNetRunReviews(layout, slug, nextReviews);
        }

        return new ActivityNoteResult
        {
            AthleteSlug = slug,
            ActivityId = targetRun.Id,
            ActivityName = string.IsNullOrWhiteSpace(targetRun.Name) ? "Lauf" : targetRun.Name.Trim(),
            NoteText = trimmedNote,
            Feel = feel,
            PerceivedExertion = perceivedExertion,
            RemoteUpdated = remoteUpdated,
            ReviewRecomputed = true,
            UpdatedReview = recomputedReview,
            DryRun = dryRun
        };
    }

    private static void WriteJsonAtomically<T>(string destinationPath, T payload, JsonSerializerOptions options)
    {
        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(payload, options) + "\n";
        File.WriteAllText(tempPath, json, Utf8NoBom);
        File.Move(tempPath, destinationPath, overwrite: true);
    }
}
