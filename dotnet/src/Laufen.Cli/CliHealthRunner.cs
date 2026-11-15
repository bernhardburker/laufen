using System.Text.Json;
using Laufen.Domain;

namespace Laufen.Cli;

/// <summary>
/// Executes lightweight CLI health verification for the shadow .NET runtime.
/// </summary>
public static class CliHealthRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Run(IReadOnlyList<string> args, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);

        if (args.Count == 0 || (args.Count == 1 && string.Equals(args[0], "--health", StringComparison.Ordinal)))
        {
            var manifest = RuntimeManifest.CreateDefault();
            var payload = new HealthResponse(
                "ok",
                manifest.ApplicationName,
                manifest.TargetFramework,
                manifest.ExecutionMode);
            stdout.WriteLine(JsonSerializer.Serialize(payload, SerializerOptions));
            return 0;
        }

        stdout.WriteLine("Unsupported arguments. Available option: --health");
        return 1;
    }

    private sealed record HealthResponse(
        string Status,
        string Application,
        string TargetFramework,
        string ExecutionMode);
}
