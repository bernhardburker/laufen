namespace Laufen.Domain;

/// <summary>
/// Describes the active .NET runtime configuration and shadow-execution boundary.
/// </summary>
public sealed record RuntimeManifest(
    string ApplicationName,
    string TargetFramework,
    string ExecutionMode)
{
    public static RuntimeManifest CreateDefault() =>
        new("Laufen.DotNet", "net11.0", "shadow-test-only");
}
