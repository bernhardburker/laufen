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
}
