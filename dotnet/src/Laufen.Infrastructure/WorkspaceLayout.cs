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

    public static WorkspaceLayout DiscoverFrom(string? startDirectory = null)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory ?? Environment.CurrentDirectory));
        while (current is not null)
        {
            var registryCandidate = Path.Combine(current.FullName, "athletes", "registry.json");
            if (File.Exists(registryCandidate))
            {
                return FromRoot(current.FullName);
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate 'athletes/registry.json' from '{startDirectory ?? Environment.CurrentDirectory}' or any parent directory.");
    }
}
