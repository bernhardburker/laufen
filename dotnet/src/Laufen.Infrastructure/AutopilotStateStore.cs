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

    public static IReadOnlyList<AutopilotDecisionTraceEntry> LoadDecisionTraces(
        WorkspaceLayout layout,
        string slug,
        int? limit = null,
        string? triggerFilter = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var path = Path.Combine(layout.AthletesDirectory, slug, "decision_trace_dotnet.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        var entries = new List<AutopilotDecisionTraceEntry>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parsed = StrictJsonLoader.Deserialize<AutopilotDecisionTraceEntry>(line);
            if (!string.IsNullOrWhiteSpace(triggerFilter)
                && !string.Equals(parsed.Trigger, triggerFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(parsed);
        }

        IEnumerable<AutopilotDecisionTraceEntry> ordered = entries.OrderByDescending(e => e.Timestamp);
        if (limit is > 0)
        {
            ordered = ordered.Take(limit.Value);
        }

        return ordered.ToList();
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

    public static TelegramStateRecord? LoadDotNetTelegramState(WorkspaceLayout layout, string slug)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var path = Path.Combine(layout.AthletesDirectory, slug, "telegram_state_dotnet.json");
        return File.Exists(path)
            ? StrictJsonLoader.LoadFromFile<TelegramStateRecord>(path)
            : null;
    }

    public static void SaveDotNetTelegramState(WorkspaceLayout layout, string slug, TelegramStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(state);

        var athleteDir = Path.Combine(layout.AthletesDirectory, slug);
        Directory.CreateDirectory(athleteDir);
        var path = Path.Combine(athleteDir, "telegram_state_dotnet.json");
        var json = JsonSerializer.Serialize(state, IndentedOptions);
        File.WriteAllText(path, json + "\n", Encoding.UTF8);
    }

    public static IReadOnlySet<DateOnly> MergeUserLockedDates(
        TelegramStateRecord? pythonState,
        TelegramStateRecord? dotnetState,
        IReadOnlySet<DateOnly>? explicitLockedDates = null)
    {
        var merged = new HashSet<DateOnly>();
        if (pythonState?.UserLockedDates is not null)
        {
            foreach (var date in pythonState.UserLockedDates)
            {
                merged.Add(date);
            }
        }

        if (dotnetState?.UserLockedDates is not null)
        {
            foreach (var date in dotnetState.UserLockedDates)
            {
                merged.Add(date);
            }
        }

        if (explicitLockedDates is not null)
        {
            foreach (var date in explicitLockedDates)
            {
                merged.Add(date);
            }
        }

        return merged;
    }
}
