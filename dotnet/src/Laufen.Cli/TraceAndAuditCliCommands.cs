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

    private static int ExecuteParityAudit(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);

        IReadOnlyList<string> slugs = parsed.AllAthletes
            ? registry.Athletes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
            : AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, allActive: false);

        var refDate = parsed.RefDate ?? DateOnly.FromDateTime(DateTime.Today);
        var inputs = new List<CutoverAuditInput>(slugs.Count);

        foreach (var slug in slugs)
        {
            var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
            var streams = new Dictionary<string, ActivityStreamData>(StringComparer.Ordinal);
            var recentRunIds = PhysiologicalCalculator.FilterValidRuns(data.Activities)
                .Where(a => DateOnly.FromDateTime(a.StartDateLocal) <= refDate)
                .OrderByDescending(a => a.StartDateLocal)
                .Select(a => a.Id)
                .Take(5);

            foreach (var runId in recentRunIds)
            {
                var stream = StrictJsonLoader.LoadActivityStream(layout, slug, runId);
                if (stream is not null)
                {
                    streams[runId] = stream;
                }
            }

            inputs.Add(new CutoverAuditInput
            {
                Slug = slug,
                RegistryEntry = data.RegistryEntry,
                Profile = data.Profile,
                Activities = data.Activities,
                WellnessRecords = data.WellnessRecords,
                CoachHistory = data.CoachHistory,
                CalendarEvents = data.CalendarEvents,
                RunReviews = data.RunReviews,
                TelegramState = data.TelegramState,
                StreamsByActivityId = streams,
                ReferenceDate = refDate
            });
        }

        var report = CutoverAuditEvaluator.EvaluateAsync(
            inputs,
            new SimulationStubLlmClient(),
            new NarrativeStubLlmClient()).GetAwaiter().GetResult();

        if (parsed.AsJson)
        {
            stdout.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            var text = parsed.Markdown
                ? CutoverAuditEvaluator.FormatMarkdown(report)
                : CutoverAuditEvaluator.FormatTerminal(report);
            stdout.Write(text);
        }

        _ = stderr;
        return report.AllPassed ? 0 : 1;
    }

    private static int ExecuteStagingSoak(
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
        var refDate = parsed.RefDate ?? DateOnly.FromDateTime(DateTime.Today);

        var report = StagingSoakHarness.RunOfflineEndToEndSoakAsync(
            layout,
            slug,
            refDate,
            parsed.OutputDir).GetAwaiter().GetResult();

        if (parsed.AsJson)
        {
            stdout.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            stdout.WriteLine($"=== .NET 11 End-to-End Staging Soak: {report.AthleteSlug} (mode={report.Mode}, passed={report.Passed}) ===");
            foreach (var step in report.Steps)
            {
                stdout.WriteLine($"  [{(step.Succeeded ? "PASS" : "FAIL")}] {step.StepName,-18} ({step.ElapsedMs,3}ms) | {step.Summary}");
            }
        }

        _ = stderr;
        return report.Passed ? 0 : 1;
    }
}
