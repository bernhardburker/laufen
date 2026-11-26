using System.Text;
using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;

namespace Laufen.Cli;

public static partial class LaufenCliRunner
{
    private static int ExecuteDashboard(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, parsed.AllAthletes);
        var refDate = parsed.RefDate ?? DateOnly.FromDateTime(DateTime.Today);

        var results = new List<object>(slugs.Count);
        foreach (var slug in slugs)
        {
            var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
            var compass = AutopilotStateStore.LoadBlockCompass(layout, slug);
            var narrative = AutopilotStateStore.LoadNarrativeSummary(layout, slug);
            var traces = AutopilotStateStore.LoadDecisionTraces(layout, slug, limit: 10);
            var dotnetReviews = AutopilotStateStore.LoadDotNetRunReviews(layout, slug);
            var mergedLocks = ResolveMergedLockedDates(layout, slug, data, parsed.LockedDates);

            var effectiveReviews = dotnetReviews.Count > 0 ? dotnetReviews : data.RunReviews;

            var snapshot = DashboardSnapshotBuilder.Build(
                slug,
                data.RegistryEntry.DashboardSlug,
                data.Profile,
                data.Activities,
                data.WellnessRecords,
                effectiveReviews,
                data.CalendarEvents,
                refDate,
                maxWeeks: parsed.MaxWeeks,
                currentCompass: compass,
                latestNarrative: narrative,
                recentTraces: traces,
                autoEventIds: parsed.AutoEventIds,
                userLockedDates: mergedLocks);

            var html = DashboardHtmlRenderer.Render(snapshot);
            var outputFile = !string.IsNullOrWhiteSpace(parsed.OutputPath) && !parsed.AllAthletes
                ? Path.GetFullPath(parsed.OutputPath)
                : Path.Combine(layout.RootDirectory, "dist-dotnet", data.RegistryEntry.DashboardSlug, "index.html");

            if (!parsed.DryRun)
            {
                var dir = Path.GetDirectoryName(outputFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(outputFile, html, Encoding.UTF8);

                if (string.IsNullOrWhiteSpace(parsed.OutputPath)
                    && string.Equals(slug, registry.DefaultAthlete, StringComparison.Ordinal))
                {
                    var rootIndex = Path.Combine(layout.RootDirectory, "dist-dotnet", "index.html");
                    File.WriteAllText(rootIndex, html, Encoding.UTF8);
                }
            }

            if (parsed.AsJson)
            {
                results.Add(new
                {
                    slug,
                    dryRun = parsed.DryRun,
                    outputFile,
                    htmlBytes = Encoding.UTF8.GetByteCount(html),
                    snapshot
                });
            }
            else
            {
                stdout.WriteLine($"=== .NET Dashboard: {slug} (ref={refDate:yyyy-MM-dd}, dryRun={parsed.DryRun}) ===");
                stdout.WriteLine($"Output: {outputFile} ({Encoding.UTF8.GetByteCount(html)} bytes, runs={snapshot.TotalRuns}, reviews={snapshot.RecentReviews.Count})");
            }
        }

        if (parsed.AsJson)
        {
            object payload = parsed.AllAthletes ? new { athletes = results } : results[0];
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }

        _ = stderr;
        return 0;
    }
}
