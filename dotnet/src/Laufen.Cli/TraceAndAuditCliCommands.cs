using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;

namespace Laufen.Cli;

public static partial class LaufenCliRunner
{
    private static int ExecuteTrace(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, allActive: false);
        var slug = slugs[0];
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
        var refDate = parsed.RefDate ?? DateOnly.FromDateTime(DateTime.Today);
        var compass = AutopilotStateStore.LoadBlockCompass(layout, slug);
        var mergedLocks = ResolveMergedLockedDates(layout, slug, data, parsed.LockedDates);

        var dossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            parsed.TargetDate ?? refDate,
            parsed.AutoEventIds,
            mergedLocks,
            currentCompass: compass);

        var limit = parsed.RecentLimit > 0 ? parsed.RecentLimit : 10;
        var traces = AutopilotStateStore.LoadDecisionTraces(layout, slug, limit, parsed.TraceTriggerFilter);

        if (parsed.AsJson)
        {
            var payload = new
            {
                slug,
                referenceDate = refDate,
                milestones = dossier.Milestones,
                count = traces.Count,
                traces
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return 0;
        }

        var formatted = parsed.Markdown
            ? DecisionTracePresenter.FormatMarkdown(slug, traces, dossier)
            : DecisionTracePresenter.FormatTerminal(slug, traces, dossier);
        stdout.Write(formatted);

        _ = stderr;
        return 0;
    }
}
