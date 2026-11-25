using System.Text;
using System.Text.Json;
using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Persists isolated .NET autopilot state (<c>block_compass.json</c> and <c>decision_trace_dotnet.jsonl</c>)
/// under <c>athletes/&lt;slug&gt;/</c> without modifying Python state files.
/// </summary>
public static class AutopilotStateStore
{
    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false
    };

    public static BlockCompassSnapshot? LoadBlockCompass(WorkspaceLayout layout, string slug)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var path = Path.Combine(layout.AthletesDirectory, slug, "block_compass.json");
        return File.Exists(path) ? StrictJsonLoader.LoadFromFile<BlockCompassSnapshot>(path) : null;
    }

    public static void SaveBlockCompass(WorkspaceLayout layout, string slug, BlockCompassSnapshot compass)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(compass);

        var dir = Path.Combine(layout.AthletesDirectory, slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "block_compass.json");
        File.WriteAllText(path, JsonSerializer.Serialize(compass, IndentedOptions) + "\n", Encoding.UTF8);
    }

    public static void AppendDecisionTrace(WorkspaceLayout layout, string slug, AutopilotDecisionTraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(entry);

        var dir = Path.Combine(layout.AthletesDirectory, slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "decision_trace_dotnet.jsonl");
        var line = JsonSerializer.Serialize(entry, CompactOptions) + "\n";
        File.AppendAllText(path, line, Encoding.UTF8);
    }

    public static AutopilotDecisionTraceEntry? LoadLatestDecisionTrace(WorkspaceLayout layout, string slug)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var path = Path.Combine(layout.AthletesDirectory, slug, "decision_trace_dotnet.jsonl");
        if (!File.Exists(path))
        {
            return null;
        }

        string? lastLine = null;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lastLine = line;
            }
        }

        return lastLine is null ? null : StrictJsonLoader.Deserialize<AutopilotDecisionTraceEntry>(lastLine);
    }

    public static void SaveNarrativeArtifacts(WorkspaceLayout layout, string slug, NarrativeReport report)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(report);

        var dir = Path.Combine(layout.AthletesDirectory, slug);
        Directory.CreateDirectory(dir);

        var jsonPath = Path.Combine(dir, "narrative_summary_dotnet.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, IndentedOptions) + "\n", Encoding.UTF8);

        var mdPath = Path.Combine(dir, "narrative_commentary_dotnet.md");
        File.WriteAllText(mdPath, report.CommentaryMarkdown.TrimEnd() + "\n", Encoding.UTF8);
    }

    public static NarrativeReport? LoadNarrativeSummary(WorkspaceLayout layout, string slug)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var path = Path.Combine(layout.AthletesDirectory, slug, "narrative_summary_dotnet.json");
        return File.Exists(path) ? StrictJsonLoader.LoadFromFile<NarrativeReport>(path) : null;
    }

    public static void SaveDotNetRunReviews(
        WorkspaceLayout layout,
        string slug,
        IReadOnlyDictionary<string, RunReviewRecord> reviews)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(reviews);

        var dir = Path.Combine(layout.AthletesDirectory, slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "run_reviews_dotnet.json");
        File.WriteAllText(path, JsonSerializer.Serialize(reviews, IndentedOptions) + "\n", Encoding.UTF8);
    }

    public static IReadOnlyDictionary<string, RunReviewRecord> LoadDotNetRunReviews(WorkspaceLayout layout, string slug)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var path = Path.Combine(layout.AthletesDirectory, slug, "run_reviews_dotnet.json");
        return File.Exists(path)
            ? StrictJsonLoader.LoadFromFile<Dictionary<string, RunReviewRecord>>(path)
            : new Dictionary<string, RunReviewRecord>(StringComparer.Ordinal);
    }
}
