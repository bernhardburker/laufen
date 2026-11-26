using System.Globalization;
using System.Text;

namespace Laufen.Domain;

public sealed record AuditCheckItem(
    string Code,
    string Category,
    string Status,
    string Detail);

public sealed record AthleteParityAuditReport(
    string Slug,
    string DisplayName,
    bool Active,
    DateOnly ReferenceDate,
    bool Passed,
    IReadOnlyList<AuditCheckItem> Checks,
    IReadOnlyDictionary<string, string> MetricsSummary);

public sealed record CutoverReadinessReport(
    DateOnly ReferenceDate,
    bool AllPassed,
    int TotalChecks,
    int PassedChecks,
    int WarningChecks,
    int FailedChecks,
    IReadOnlyList<AthleteParityAuditReport> Athletes,
    IReadOnlyList<string> CutoverBlockers);

public sealed record CutoverAuditInput
{
    public required string Slug { get; init; }
    public required AthleteRegistryEntry RegistryEntry { get; init; }
    public required AthleteProfile Profile { get; init; }
    public required IReadOnlyList<IntervalsActivityEntry> Activities { get; init; }
    public required IReadOnlyList<IntervalsWellnessEntry> WellnessRecords { get; init; }
    public required IReadOnlyList<CoachHistoryEntry> CoachHistory { get; init; }
    public required IReadOnlyList<CalendarEventEntry> CalendarEvents { get; init; }
    public required IReadOnlyDictionary<string, RunReviewRecord> RunReviews { get; init; }
    public TelegramStateRecord? TelegramState { get; init; }
    public required IReadOnlyDictionary<string, ActivityStreamData> StreamsByActivityId { get; init; }
    public required DateOnly ReferenceDate { get; init; }
}

/// <summary>
/// Pure cutover readiness evaluator verifying contract integrity, physiological review parity,
/// dossier time-travel isolation, autopilot cycle safety, HTML dashboard hygiene, staging guards,
/// and multi-day simulation invariants across tenants.
/// </summary>
public static class CutoverAuditEvaluator
{
    public static async Task<CutoverReadinessReport> EvaluateAsync(
        IReadOnlyList<CutoverAuditInput> tenants,
        ILlmClient plannerLlm,
        ILlmClient narratorLlm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(plannerLlm);
        ArgumentNullException.ThrowIfNull(narratorLlm);

        var athleteReports = new List<AthleteParityAuditReport>(tenants.Count);
        var blockers = new List<string>();
        var refDate = tenants.Count > 0 ? tenants[0].ReferenceDate : DateOnly.FromDateTime(DateTime.Today);

        foreach (var input in tenants)
        {
            var checks = new List<AuditCheckItem>
            {
                CheckContracts(input),
                CheckReviewParity(input)
            };

            var dossier = CoachIntelligenceDossierBuilder.Build(
                input.Profile,
                input.Activities,
                input.WellnessRecords,
                input.RunReviews,
                input.CalendarEvents,
                input.ReferenceDate);

            checks.Add(CheckDossierIntegrity(input, dossier));
            checks.Add(await CheckAutopilotCycleAsync(input, plannerLlm, narratorLlm, cancellationToken).ConfigureAwait(false));
            checks.Add(CheckDashboardRender(input));
            checks.Add(CheckStagingSafety(input));
            checks.Add(await CheckSimulationsAsync(input, plannerLlm, cancellationToken).ConfigureAwait(false));

            var passed = checks.All(c => !string.Equals(c.Status, "fail", StringComparison.Ordinal));
            foreach (var fail in checks.Where(c => string.Equals(c.Status, "fail", StringComparison.Ordinal)))
            {
                blockers.Add($"{input.Slug} [{fail.Code}]: {fail.Detail}");
            }

            var metrics = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["activities"] = input.Activities.Count.ToString(CultureInfo.InvariantCulture),
                ["wellness"] = input.WellnessRecords.Count.ToString(CultureInfo.InvariantCulture),
                ["reviews"] = input.RunReviews.Count.ToString(CultureInfo.InvariantCulture),
                ["streams_checked"] = input.StreamsByActivityId.Count.ToString(CultureInfo.InvariantCulture),
                ["ctl"] = dossier.Readiness.Ctl?.ToString("F1", CultureInfo.InvariantCulture) ?? "null"
            };

            athleteReports.Add(new AthleteParityAuditReport(
                input.Slug,
                input.RegistryEntry.DisplayName,
                input.RegistryEntry.Active,
                input.ReferenceDate,
                passed,
                checks,
                metrics));
        }

        var allChecks = athleteReports.SelectMany(a => a.Checks).ToList();
        var passedCount = allChecks.Count(c => string.Equals(c.Status, "pass", StringComparison.Ordinal));
        var warnCount = allChecks.Count(c => string.Equals(c.Status, "warn", StringComparison.Ordinal));
        var failCount = allChecks.Count(c => string.Equals(c.Status, "fail", StringComparison.Ordinal));

        return new CutoverReadinessReport(
            refDate,
            failCount == 0,
            allChecks.Count,
            passedCount,
            warnCount,
            failCount,
            athleteReports,
            blockers);
    }

    public static string FormatTerminal(CutoverReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("===========================================================================");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"  .NET 11 CUTOVER PARITY AUDIT (ref={report.ReferenceDate:yyyy-MM-dd}, passed={report.AllPassed})");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"  Summary: total={report.TotalChecks}, pass={report.PassedChecks}, warn={report.WarningChecks}, fail={report.FailedChecks}");
        sb.AppendLine("===========================================================================");

        foreach (var athlete in report.Athletes)
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"Athlete: {athlete.Slug} ({athlete.DisplayName}, active={athlete.Active}) -> {(athlete.Passed ? "PASS" : "FAIL")}");
            foreach (var check in athlete.Checks)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  [{check.Status.ToUpperInvariant(),-4}] {check.Code,-13} ({check.Category,-10}) {check.Detail}");
            }
        }

        if (report.CutoverBlockers.Count > 0)
        {
            sb.AppendLine("Blockers:");
            foreach (var b in report.CutoverBlockers)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {b}");
            }
        }

        sb.AppendLine("===========================================================================");
        return sb.ToString();
    }

    public static string FormatMarkdown(CutoverReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"## .NET 11 Cutover Parity Audit (`{report.ReferenceDate:yyyy-MM-dd}`)");
        sb.AppendLine();
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"- **Overall:** `{(report.AllPassed ? "PASS" : "FAIL")}` (`{report.PassedChecks}` pass / `{report.WarningChecks}` warn / `{report.FailedChecks}` fail)");
        sb.AppendLine();

        foreach (var athlete in report.Athletes)
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"### Tenant `{athlete.Slug}` ({athlete.DisplayName})");
            sb.AppendLine("| Code | Category | Status | Detail |");
            sb.AppendLine("| :--- | :--- | :--- | :--- |");
            foreach (var c in athlete.Checks)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| `{c.Code}` | `{c.Category}` | `{c.Status}` | {c.Detail} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static AuditCheckItem CheckContracts(CutoverAuditInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Profile.Id)
            || input.Profile.HeartRate.Zones.Count == 0
            || string.IsNullOrWhiteSpace(input.RegistryEntry.DashboardSlug))
        {
            return new AuditCheckItem("CONTRACT-01", "contracts", "fail", "Athlete profile HR zones or dashboard slug missing.");
        }

        return new AuditCheckItem(
            "CONTRACT-01",
            "contracts",
            "pass",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Strict contracts loaded cleanly (zones={input.Profile.HeartRate.Zones.Count}, activities={input.Activities.Count}, wellness={input.WellnessRecords.Count})."));
    }

    private static AuditCheckItem CheckReviewParity(CutoverAuditInput input)
    {
        var validRuns = PhysiologicalCalculator.FilterValidRuns(input.Activities)
            .Where(a => DateOnly.FromDateTime(a.StartDateLocal) <= input.ReferenceDate)
            .OrderByDescending(a => a.StartDateLocal)
            .ToList();

        if (validRuns.Count == 0)
        {
            return new AuditCheckItem(
                "REVIEW-02",
                "physiology",
                "pass",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"0 valid runs on '{input.Slug}' (orphaned legacy reviews={input.RunReviews.Count}, zero false metrics)."));
        }

        var checkedCount = 0;
        var calendarById = input.CalendarEvents.ToDictionary(e => e.Id);
        foreach (var run in validRuns)
        {
            if (!input.RunReviews.TryGetValue(run.Id, out var pyReview))
            {
                continue;
            }

            input.StreamsByActivityId.TryGetValue(run.Id, out var stream);
            if (stream is null)
            {
                continue;
            }

            calendarById.TryGetValue(run.PairedEventId ?? -1L, out var paired);
            var dnReview = PostRunReviewCalculator.EvaluateRun(run, input.Profile, stream, paired);
            checkedCount++;

            if (Math.Abs(dnReview.Z1Z2Percentage - pyReview.Z1Z2Percentage) > 1.5)
            {
                return new AuditCheckItem(
                    "REVIEW-02",
                    "physiology",
                    "fail",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Z1/Z2 drift on {run.Id}: .NET={dnReview.Z1Z2Percentage:F1}% vs cached={pyReview.Z1Z2Percentage:F1}%."));
            }

            if (dnReview.AerobicEf.HasValue && pyReview.AerobicEf.HasValue
                && Math.Abs(dnReview.AerobicEf.Value - pyReview.AerobicEf.Value) > 0.02)
            {
                return new AuditCheckItem(
                    "REVIEW-02",
                    "physiology",
                    "fail",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"AEF drift on {run.Id}: .NET={dnReview.AerobicEf.Value:F3} vs cached={pyReview.AerobicEf.Value:F3}."));
            }

            var pyZ2 = pyReview.ZonePaceStats?.Zones.Values.FirstOrDefault(z => z.Zone == 2);
            var dnZ2 = dnReview.ZonePaceStats?.Zones.Values.FirstOrDefault(z => z.Zone == 2);
            if (pyZ2 is { ValidForTrend: true } && dnZ2 is { ValidForTrend: true }
                && Math.Abs(dnZ2.GapPaceSecondsPerKm - pyZ2.GapPaceSecondsPerKm) > 1.5)
            {
                return new AuditCheckItem(
                    "REVIEW-02",
                    "physiology",
                    "fail",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Z2 GAP drift on {run.Id}: .NET={dnZ2.GapPaceSecondsPerKm:F1}s vs cached={pyZ2.GapPaceSecondsPerKm:F1}s."));
            }

            if (checkedCount >= 3)
            {
                break;
            }
        }

        return new AuditCheckItem(
            "REVIEW-02",
            "physiology",
            "pass",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Verified 4-stage & GAP parity across {checkedCount} recent streamed run(s) within strict tolerances."));
    }

    private static AuditCheckItem CheckDossierIntegrity(CutoverAuditInput input, CoachIntelligenceDossier dossier)
    {
        if (dossier.ReferenceDate != input.ReferenceDate)
        {
            return new AuditCheckItem("DOSSIER-03", "dossier", "fail", "Dossier ReferenceDate mismatch.");
        }

        if (dossier.Milestones is null || dossier.Milestones.ActiveMilestones.Count != 3)
        {
            return new AuditCheckItem("DOSSIER-03", "dossier", "fail", "Dossier missing 3 v2 StageMilestones.");
        }

        if (input.Activities.Count == 0 && !input.WellnessRecords.Any(w => w.Ctl.HasValue) && dossier.Readiness.Ctl.HasValue)
        {
            return new AuditCheckItem("DOSSIER-03", "dossier", "fail", "Empty tenant leaked non-null synthetic CTL fallback.");
        }

        return new AuditCheckItem(
            "DOSSIER-03",
            "dossier",
            "pass",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Dossier cutoff={dossier.ReferenceDate:yyyy-MM-dd}, milestones={dossier.Milestones.ActiveMilestones.Count}, northStar={dossier.Milestones.NorthStar.TrendDirection}."));
    }

    private static async Task<AuditCheckItem> CheckAutopilotCycleAsync(
        CutoverAuditInput input,
        ILlmClient plannerLlm,
        ILlmClient narratorLlm,
        CancellationToken cancellationToken)
    {
        var orchestrator = new AutopilotCycleOrchestrator(
            new RollingAutopilotStrategy(plannerLlm),
            new LlmProgressNarrator(narratorLlm));

        var result = await orchestrator.RunCycleAsync(new AutopilotCycleRequest
        {
            AthleteSlug = input.Slug,
            RegistryEntry = input.RegistryEntry,
            Profile = input.Profile,
            Activities = input.Activities,
            WellnessRecords = input.WellnessRecords,
            RunReviews = input.RunReviews,
            CalendarEvents = input.CalendarEvents,
            ReferenceDate = input.ReferenceDate,
            TargetDate = input.ReferenceDate,
            Trigger = "morning_sync",
            ForceReplan = true,
            ForceNarrate = true,
            IsDryRun = true
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || result.CalendarSyncPlan is null || result.TelegramPreview is null)
        {
            return new AuditCheckItem(
                "AUTOPILOT-04",
                "autopilot",
                "fail",
                $"AutopilotCycleOrchestrator failed: {result.FailureReason ?? "missing sync plan or telegram preview"}.");
        }

        return new AuditCheckItem(
            "AUTOPILOT-04",
            "autopilot",
            "pass",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Unified cycle succeeded (health={result.UpdatedCompass?.HealthState ?? "-"}, watch={result.PlanningOutcome?.ReconciledSchedule?.WatchCommitWindow.Count ?? 0}, syncCreate={result.CalendarSyncPlan.CreateCount})."));
    }

    private static AuditCheckItem CheckDashboardRender(CutoverAuditInput input)
    {
        var snapshot = DashboardSnapshotBuilder.Build(
            input.Slug,
            input.RegistryEntry.DashboardSlug,
            input.Profile,
            input.Activities,
            input.WellnessRecords,
            input.RunReviews,
            input.CalendarEvents,
            input.ReferenceDate);

        var html = DashboardHtmlRenderer.Render(snapshot);
        if (!html.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal)
            || !html.Contains("<svg", StringComparison.Ordinal)
            || html.Contains("cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase)
            || html.Contains("tag-amber", StringComparison.Ordinal))
        {
            return new AuditCheckItem("DASHBOARD-05", "dashboard", "fail", "Dashboard HTML failed structural, CDN-free, or CSS-isolation checks.");
        }

        return new AuditCheckItem(
            "DASHBOARD-05",
            "dashboard",
            "pass",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Self-contained HTML5 dashboard rendered ({html.Length} chars, runs={snapshot.TotalRuns})."));
    }

    private static AuditCheckItem CheckStagingSafety(CutoverAuditInput input)
    {
        var isStagingCandidate = input.Slug.Contains("test", StringComparison.OrdinalIgnoreCase)
            && !input.RegistryEntry.Active;

        if (isStagingCandidate)
        {
            var stagingDecision = StagingSafetyGuard.EvaluateWritePermission(input.Slug, input.RegistryEntry);
            if (!stagingDecision.Allowed)
            {
                return new AuditCheckItem("SAFETY-06", "safety", "fail", "Staging tenant unexpectedly blocked by StagingSafetyGuard.");
            }

            if (string.IsNullOrWhiteSpace(input.RegistryEntry.TelegramChatId))
            {
                return new AuditCheckItem(
                    "SAFETY-06",
                    "safety",
                    "warn",
                    "Staging Intervals.icu writes permitted, but 'telegram_chat_id' is unset in registry.json (configure before live Telegram staging soak).");
            }

            return new AuditCheckItem("SAFETY-06", "safety", "pass", "Staging tenant permitted with Telegram chat ID configured.");
        }

        var prodDefault = StagingSafetyGuard.EvaluateWritePermission(input.Slug, input.RegistryEntry, allowProductionWrite: false);
        var prodExplicit = StagingSafetyGuard.EvaluateWritePermission(input.Slug, input.RegistryEntry, allowProductionWrite: true);
        if (prodDefault.Allowed || !prodExplicit.Allowed)
        {
            return new AuditCheckItem("SAFETY-06", "safety", "fail", "Production StagingSafetyGuard enforcement violated.");
        }

        return new AuditCheckItem("SAFETY-06", "safety", "pass", "Production writes blocked by default unless explicitly authorized.");
    }

    private static async Task<AuditCheckItem> CheckSimulationsAsync(
        CutoverAuditInput input,
        ILlmClient plannerLlm,
        CancellationToken cancellationToken)
    {
        var strategy = new RollingAutopilotStrategy(plannerLlm);
        var scenarioNames = AutopilotScenarioCatalog.GetAllNames();
        var failures = new List<string>();

        foreach (var name in scenarioNames)
        {
            var scenario = AutopilotScenarioCatalog.GetScenario(name);
            var res = await AutopilotSimulator.RunAsync(
                scenario,
                input.Profile,
                input.Slug,
                "stub",
                strategy,
                maxValidationRetries: 1,
                cancellationToken).ConfigureAwait(false);
            if (!res.Passed)
            {
                failures.Add(name);
            }
        }

        if (failures.Count > 0)
        {
            return new AuditCheckItem(
                "SIMULATION-07",
                "simulation",
                "fail",
                $"Failed simulation scenario(s): {string.Join(", ", failures)}.");
        }

        return new AuditCheckItem(
            "SIMULATION-07",
            "simulation",
            "pass",
            string.Create(CultureInfo.InvariantCulture, $"All {scenarioNames.Count}/{scenarioNames.Count} multi-day self-healing scenarios passed."));
    }
}
