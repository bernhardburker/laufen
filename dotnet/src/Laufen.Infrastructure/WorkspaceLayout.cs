namespace Laufen.Infrastructure;

/// <summary>
/// Resolves workspace and athlete directory locations without mutating Python state.
/// </summary>
public sealed record WorkspaceLayout(string RootDirectory, string AthletesDirectory)
{
    public bool RootExists => Directory.Exists(RootDirectory);

    public bool AthletesDirectoryExists => Directory.Exists(AthletesDirectory);

    public static WorkspaceLayout FromRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullRoot = Path.GetFullPath(rootDirectory);
        return new WorkspaceLayout(fullRoot, Path.Combine(fullRoot, "athletes"));
    }
}
