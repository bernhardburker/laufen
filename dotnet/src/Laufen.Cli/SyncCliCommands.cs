using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;

namespace Laufen.Cli;

public static partial class LaufenCliRunner
{
    private static int ExecuteSyncData(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory,
        bool profileOnly)
    {
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, parsed.AllAthletes);
        var refDate = parsed.RefDate ?? DateOnly.FromDateTime(DateTime.Today);
        var effectiveDryRun = parsed.DryRun || !parsed.Apply;

        var apiKey = DotEnvLoader.ResolveSecret(layout, "INTERVALS_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            stderr.WriteLine("Error: INTERVALS_API_KEY is not set in environment or .env.");
            return 1;
        }

        var apiBase = DotEnvLoader.ResolveSecret(layout, "INTERVALS_API_BASE") ?? "https://intervals.icu/api/v1";
        var client = new IntervalsIcuHttpClient(apiKey, apiBase);

        var results = new List<object>(slugs.Count);
        foreach (var slug in slugs)
        {
            var entry = registry.Athletes[slug];
            if (parsed.WriteWorkspace && !effectiveDryRun)
            {
                var permission = StagingSafetyGuard.EvaluateWritePermission(slug, entry, parsed.AllowProductionWrite);
                if (!permission.Allowed)
                {
                    stderr.WriteLine(permission.Reason);
                    return 1;
                }
            }

            if (profileOnly)
            {
                var remote = client.GetAthleteProfileAsync(entry.IntervalsId).GetAwaiter().GetResult();
                var existingPath = Path.Combine(layout.AthletesDirectory, slug, "athlete.json");
                var existingDoc = File.Exists(existingPath)
                    ? StrictJsonLoader.LoadAthleteProfile(layout, slug)
                    : null;
                var mappedDoc = AthleteProfileSyncMapper.MapToDocument(remote, existingDoc?.Athlete, entry.IntervalsId);

                var targetDir = parsed.WriteWorkspace
                    ? Path.Combine(layout.AthletesDirectory, slug)
                    : Path.Combine(layout.AthletesDirectory, slug, ".dotnet_sync");
                var targetFile = Path.Combine(targetDir, "athlete.json");

                if (!effectiveDryRun)
                {
                    Directory.CreateDirectory(targetDir);
                    var tmpFile = targetFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(tmpFile, JsonSerializer.Serialize(mappedDoc, JsonOptions) + "\n");
                    File.Move(tmpFile, targetFile, overwrite: true);
                }

                if (parsed.AsJson)
                {
                    results.Add(new
                    {
                        slug,
                        dryRun = effectiveDryRun,
                        targetFile,
                        profile = mappedDoc.Athlete
                    });
                }
                else
                {
                    WriteSyncProfileText(stdout, slug, effectiveDryRun, targetFile, mappedDoc.Athlete);
                }
            }
            else
            {
                var options = new TelemetrySyncOptions
                {
                    SyncProfile = true,
                    SyncStreams = true,
                    DryRun = effectiveDryRun,
                    WriteToWorkspace = parsed.WriteWorkspace
                };
                var summary = TelemetrySyncService.SyncAthleteAsync(
                    layout,
                    entry,
                    slug,
                    client,
                    refDate,
                    options,
                    parsed.AllowProductionWrite).GetAwaiter().GetResult();

                if (parsed.AsJson)
                {
                    results.Add(new { slug, summary });
                }
                else
                {
                    WriteSyncDataText(stdout, summary);
                }
            }
        }

        if (parsed.AsJson)
        {
            object payload = parsed.AllAthletes ? new { athletes = results } : results[0];
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }

        return 0;
    }

    private static int ExecuteNote(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(parsed.NoteText))
        {
            stderr.WriteLine("Missing required '--note <text>' option for 'note'.");
            return 1;
        }

        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, allActive: false);
        var slug = slugs[0];
        var entry = registry.Athletes[slug];
        var effectiveDryRun = parsed.DryRun || (!parsed.Apply && !parsed.Push);

        IIntervalsIcuClient? client = null;
        if (parsed.Push && !effectiveDryRun)
        {
            var permission = StagingSafetyGuard.EvaluateWritePermission(slug, entry, parsed.AllowProductionWrite);
            if (!permission.Allowed)
            {
                stderr.WriteLine(permission.Reason);
                return 1;
            }

            var apiKey = DotEnvLoader.ResolveSecret(layout, "INTERVALS_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                stderr.WriteLine("Error: INTERVALS_API_KEY is not set in environment or .env.");
                return 1;
            }

            var apiBase = DotEnvLoader.ResolveSecret(layout, "INTERVALS_API_BASE") ?? "https://intervals.icu/api/v1";
            client = new IntervalsIcuHttpClient(apiKey, apiBase);
        }

        var result = TelemetrySyncService.AttachNoteAsync(
            layout,
            registry,
            slug,
            parsed.NoteText,
            parsed.ActivityId,
            parsed.Feel,
            parsed.Rpe,
            client,
            parsed.Push,
            effectiveDryRun,
            parsed.AllowProductionWrite).GetAwaiter().GetResult();

        if (parsed.AsJson)
        {
            stdout.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }

        WriteNoteResultText(stdout, result);
        return 0;
    }

    private static void WriteSyncProfileText(
        TextWriter stdout,
        string slug,
        bool dryRun,
        string targetFile,
        AthleteProfile profile)
    {
        stdout.WriteLine($"=== Profile Sync: {slug} ({profile.Id}, dryRun={dryRun}) ===");
        stdout.WriteLine($"Target: {targetFile}");
        stdout.WriteLine($"HR: resting={profile.HeartRate.RestingHr} bpm | LTHR={profile.HeartRate.Lthr} bpm | max={profile.HeartRate.MaxHr} bpm | zones={profile.HeartRate.Zones.Count}");
    }

    private static void WriteSyncDataText(TextWriter stdout, TelemetrySyncSummary summary)
    {
        stdout.WriteLine($"=== Telemetry Sync: {summary.AthleteSlug} ({summary.IntervalsAthleteId}, ref={summary.ReferenceDate:yyyy-MM-dd}, dryRun={summary.DryRun}) ===");
        stdout.WriteLine($"Target: {summary.TargetDirectory}");
        stdout.WriteLine($"Counts: activities={summary.ActivitiesCount}, wellness={summary.WellnessCount}, calendar={summary.CalendarEventsCount}, profileSynced={summary.ProfileSynced}");
        stdout.WriteLine($"Streams: downloaded={summary.StreamsDownloaded}, skippedExisting={summary.StreamsSkippedExisting}, failed={summary.StreamsFailed}");
        foreach (var warning in summary.Warnings)
        {
            stdout.WriteLine($"  [WARN] {warning}");
        }
    }

    private static void WriteNoteResultText(TextWriter stdout, ActivityNoteResult result)
    {
        stdout.WriteLine($"=== Run Note Evaluation: {result.AthleteSlug} (dryRun={result.DryRun}) ===");
        stdout.WriteLine($"Run: [{result.ActivityId}] {result.ActivityName}");
        stdout.WriteLine($"Note: {result.NoteText}");
        if (result.Feel.HasValue || result.PerceivedExertion.HasValue)
        {
            stdout.WriteLine($"Feel: {result.Feel?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}/5 | RPE: {result.PerceivedExertion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}/10");
        }
        stdout.WriteLine($"RemoteUpdated={result.RemoteUpdated} | ReviewRecomputed={result.ReviewRecomputed}");
        if (result.UpdatedReview is { } review)
        {
            stdout.WriteLine($"Stage 4 Directive: recovery={review.Review.Stages.Stage4NextRun.RecoveryHours}h | {review.Review.Stages.Stage4NextRun.Directive}");
        }
    }
}
