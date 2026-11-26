using System.Globalization;
using System.Text;

namespace Laufen.Domain;

/// <summary>
/// Formats <see cref="AutopilotDecisionTraceEntry"/> logs and active dossier criteria into
/// terminal output and GitHub Actions <c>$GITHUB_STEP_SUMMARY</c> Markdown.
/// </summary>
public static class DecisionTracePresenter
{
    public static string FormatTerminal(
        string slug,
        IReadOnlyList<AutopilotDecisionTraceEntry> traces,
        CoachIntelligenceDossier? currentDossier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(traces);

        var sb = new StringBuilder();
        sb.AppendLine("===========================================================================");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  .NET AUTOPILOT DECISION TRACE ({slug})");
        sb.AppendLine("===========================================================================");

        if (currentDossier is not null)
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"Stichtag: {currentDossier.ReferenceDate:yyyy-MM-dd} | Health: {currentDossier.CurrentCompass?.HealthState ?? "Normal"} | RedFlags: {currentDossier.Readiness.MedicalRedFlags.Count}");
            if (currentDossier.Milestones is { } ms)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"Nordstern (12w): {ms.NorthStar.TrendDirection} | Z2 GAP: {ms.NorthStar.Z2GapEarlyPaceText ?? "-"} -> {ms.NorthStar.Z2GapRecentPaceText ?? "-"} | AEF: {ms.NorthStar.BaseAefEarly?.ToString("F3", CultureInfo.InvariantCulture) ?? "-"} -> {ms.NorthStar.BaseAefRecent?.ToString("F3", CultureInfo.InvariantCulture) ?? "-"}");
                foreach (var item in ms.ActiveMilestones)
                {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"  - [{item.Status}] {item.Title}: {item.CurrentValue:F1}/{item.TargetValue:F1} {item.Unit} ({item.Evidence})");
                }
            }
            sb.AppendLine("---------------------------------------------------------------------------");
        }

        if (traces.Count == 0)
        {
            sb.AppendLine("Keine Eintraege in decision_trace_dotnet.jsonl vorhanden.");
            sb.AppendLine("===========================================================================");
            return sb.ToString();
        }

        foreach (var entry in traces)
        {
            var status = entry.Success ? "OK" : "FAILED";
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss zzz}] {entry.Trigger} | ref={entry.ReferenceDate:yyyy-MM-dd} | {status} | health={entry.HealthState ?? "-"} | attempts={entry.AttemptCount}");
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"  Model={entry.Model ?? "-"} | Upserts={entry.UpsertCount} | Deletes={entry.DeleteCount} | Watch={string.Join(",", entry.WatchWindowDates.Select(d => d.ToString("MM-dd", CultureInfo.InvariantCulture)))}");

            if (entry.ProjectedEndingCtl.HasValue || entry.ProjectedMinTsb.HasValue)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  Banister 14d: endCTL={entry.ProjectedEndingCtl?.ToString("F1", CultureInfo.InvariantCulture) ?? "-"}, endATL={entry.ProjectedEndingAtl?.ToString("F1", CultureInfo.InvariantCulture) ?? "-"}, minTSB={entry.ProjectedMinTsb?.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) ?? "-"}");
            }

            if (entry.Rationale is { } r)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Observations: {string.Join(" | ", r.Observations)}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Tradeoff:     {r.TradeoffWeighed}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Intent:       {r.ProgressionIntent}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Rejected:     {r.RejectedAlternative}");
            }

            if (entry.ValidationErrors.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ValidationErrors: {string.Join("; ", entry.ValidationErrors)}");
            }

            if (!string.IsNullOrWhiteSpace(entry.FailureReason))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  FailureReason: {entry.FailureReason}");
            }
        }

        sb.AppendLine("===========================================================================");
        return sb.ToString();
    }

    public static string FormatMarkdown(
        string slug,
        IReadOnlyList<AutopilotDecisionTraceEntry> traces,
        CoachIntelligenceDossier? currentDossier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(traces);

        var sb = new StringBuilder();
        sb.Append("## Decision Trace: `").Append(slug).AppendLine("`");
        sb.AppendLine();

        if (currentDossier?.Milestones is { } ms)
        {
            sb.Append("### 14-Tage Etappenziele & 12-Wochen Nordstern (`")
                .Append(ms.ReferenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .AppendLine("`)");
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"- **Nordstern-Trend:** `{ms.NorthStar.TrendDirection}` (Z2 GAP: `{ms.NorthStar.Z2GapEarlyPaceText ?? "-"}` -> `{ms.NorthStar.Z2GapRecentPaceText ?? "-"}`, Basis-AEF: `{ms.NorthStar.BaseAefEarly?.ToString("F3", CultureInfo.InvariantCulture) ?? "-"}` -> `{ms.NorthStar.BaseAefRecent?.ToString("F3", CultureInfo.InvariantCulture) ?? "-"}`)");
            sb.AppendLine();
            sb.AppendLine("| Kategorie | Ziel | Fortschritt | Status | Nachweis |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");
            foreach (var item in ms.ActiveMilestones)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| `{item.Category}` | {item.Title} | {item.CurrentValue:F1} / {item.TargetValue:F1} {item.Unit} | `{item.Status}` | {item.Evidence} |");
            }
            sb.AppendLine();
        }

        if (traces.Count == 0)
        {
            sb.AppendLine("_Keine Eintraege in `decision_trace_dotnet.jsonl` vorhanden._");
            return sb.ToString();
        }

        sb.AppendLine("| Zeitstempel | Trigger | Ref-Datum | Status | Health | Upserts / Deletes | Min TSB |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
        foreach (var entry in traces)
        {
            var status = entry.Success ? "OK" : "FAILED";
            var minTsb = entry.ProjectedMinTsb?.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) ?? "-";
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {entry.Timestamp:yyyy-MM-dd HH:mm} | `{entry.Trigger}` | {entry.ReferenceDate:yyyy-MM-dd} | {status} | `{entry.HealthState ?? "-"}` | {entry.UpsertCount} / {entry.DeleteCount} | {minTsb} |");
        }

        var latestWithRationale = traces.FirstOrDefault(t => t.Rationale is not null);
        if (latestWithRationale?.Rationale is { } rationale)
        {
            sb.AppendLine();
            sb.AppendLine("### Aktuelle Begruendung");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Beobachtungen:** {string.Join(" | ", rationale.Observations)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Abwaegung:** {rationale.TradeoffWeighed}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Intention:** {rationale.ProgressionIntent}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Verworfen:** {rationale.RejectedAlternative}");
        }

        return sb.ToString();
    }
}
