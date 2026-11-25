namespace Laufen.Infrastructure;

/// <summary>
/// Loads environment secrets cleanly from <c>.env</c> files without logging sensitive values.
/// </summary>
public static class DotEnvLoader
{
    public static IReadOnlyDictionary<string, string> LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            var key = line[..eqIndex].Trim();
            var val = line[(eqIndex + 1)..].Trim();

            if (val.Length >= 2
                && ((val.StartsWith('"') && val.EndsWith('"')) || (val.StartsWith('\'') && val.EndsWith('\''))))
            {
                val = val[1..^1];
            }

            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
            {
                values[key] = val;
            }
        }

        return values;
    }

    public static string? ResolveSecret(WorkspaceLayout layout, string key)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var envValue = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue.Trim();
        }

        var dotEnvPath = Path.Combine(layout.RootDirectory, ".env");
        var parsed = LoadFile(dotEnvPath);
        return parsed.TryGetValue(key, out var fileValue) ? fileValue : null;
    }
}
