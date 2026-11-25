using System.Globalization;
using System.Text;
using System.Text.Json;
using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Renders Markdown dossiers (<c>report.md</c>) and machine-readable timelines (<c>timeline.json</c>)
/// inside isolated output directories without touching production athlete state.
/// </summary>
public static class SimulationReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string FormatMarkdown(SimulationRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.Append("# Simulation Report: `").Append(result.ScenarioName).AppendLine("`");
        sb.AppendLine();
        sb.Append("- **Athlete:** `").Append(result.AthleteSlug).AppendLine("`");
        sb.Append("- **Provider:** `").Append(result.Provider).AppendLine("`");
        sb.Append("- **Start Date:** `").Append(result.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).AppendLine("`");
        sb.Append("- **Steps:** ").AppendLine(result.TotalSteps.ToString(CultureInfo.InvariantCulture));
        sb.Append("- **Verdict:** ").AppendLine(result.Passed ? "PASS (0 failures)" : $"FAIL ({result.AllFindings.Count(f => f.Severity == "fail")} failure(s))");
        sb.Append("- **LLM Attempts:** ").Append(result.TotalAttempts.ToString(CultureInfo.InvariantCulture))
          .Append(" (").Append(result.TotalElapsedSeconds.ToString("F1", CultureInfo.InvariantCulture)).AppendLine("s)");
        sb.AppendLine();
        sb.Append("> ").AppendLine(result.Description);
        sb.AppendLine();

        sb.AppendLine("## Audit Findings Summary");
        sb.AppendLine();
        if (result.AllFindings.Count == 0)
        {
            sb.AppendLine("All `AUDIT-01` through `AUDIT-08` invariants passed cleanly across every simulated step.");
        }
        else
        {
            sb.AppendLine("| Step | Date | Code | Severity | Message |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");
            foreach (var f in result.AllFindings)
            {
                sb.Append("| Day +").Append(f.DayOffset.ToString(CultureInfo.InvariantCulture))
                  .Append(" (`").Append(f.Trigger).Append("`) | ")
                  .Append(f.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                  .Append(" | `").Append(f.AuditCode).Append("` | ")
                  .Append(f.Severity)
                  .Append(" | ").Append(f.Message.Replace('|', '/')).AppendLine(" |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Chronological Step Timeline");
        sb.AppendLine();

        foreach (var step in result.Steps)
        {
            sb.Append("### Day +").Append(step.DayOffset.ToString(CultureInfo.InvariantCulture))
              .Append(" — ").Append(step.Date.ToString("yyyy-MM-dd (ddd)", CultureInfo.InvariantCulture))
              .Append(" [`").Append(step.Trigger).AppendLine("`]");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(step.InjectedRunName))
            {
                sb.Append("- **Executed Run:** ").AppendLine(step.InjectedRunName);
            }

            if (!string.IsNullOrWhiteSpace(step.AthleteMessage))
            {
                sb.Append("- **Athlete Message:** `").Append(step.AthleteMessage).AppendLine("`");
            }

            sb.Append("- **Health State:** `").Append(step.HealthStateBefore ?? "none")
              .Append("` -> `").Append(step.HealthStateAfter ?? "-").AppendLine("`");

            if (step.Outcome.Success && step.Outcome.ReconciledSchedule is { } schedule)
            {
                sb.Append("- **Schedule Counts:** Watch=").Append(step.ScheduledWatchCount.ToString(CultureInfo.InvariantCulture))
                  .Append(", Preview=").Append(step.ScheduledPreviewCount.ToString(CultureInfo.InvariantCulture))
                  .Append(", Upserts=").Append(schedule.CompiledUpserts.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(", Deletes=").AppendLine(schedule.DeletedEventIds.Count.ToString(CultureInfo.InvariantCulture));

                if (schedule.WatchCommitWindow.Count > 0 || schedule.PreviewWindow.Count > 0)
                {
                    sb.AppendLine("- **14-Day Horizon:**");
                    foreach (var item in schedule.WatchCommitWindow.Concat(schedule.PreviewWindow))
                    {
                        sb.Append("  - `").Append(item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                          .Append("` [").Append(item.Tier).Append(" / ").Append(item.Ownership).Append("] ")
                          .Append(item.Title).Append(" (").Append(item.EstimatedLoad.ToString("F1", CultureInfo.InvariantCulture)).AppendLine(" TSS)");
                    }
                }
            }
            else
            {
                sb.Append("- **Planning Error:** ").AppendLine(step.Outcome.FailureReason ?? "Unknown failure");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static (string ReportPath, string TimelinePath) WriteArtifacts(
        string outputDirectory,
        SimulationRunResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(result);

        var scenarioDir = Path.Combine(outputDirectory, result.ScenarioName);
        Directory.CreateDirectory(scenarioDir);

        var reportPath = Path.Combine(scenarioDir, "report.md");
        var timelinePath = Path.Combine(scenarioDir, "timeline.json");

        File.WriteAllText(reportPath, FormatMarkdown(result), Encoding.UTF8);
        File.WriteAllText(timelinePath, JsonSerializer.Serialize(result, JsonOptions) + "\n", Encoding.UTF8);

        return (reportPath, timelinePath);
    }
}
