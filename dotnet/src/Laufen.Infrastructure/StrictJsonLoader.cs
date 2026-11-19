using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Loads athlete JSON contracts with strict member and null-element checking to expose schema drift immediately.
/// </summary>
public static class StrictJsonLoader
{
    public static JsonSerializerOptions StrictOptions { get; } = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true
    };

    public static T Deserialize<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var result = JsonSerializer.Deserialize<T>(json, StrictOptions)
            ?? throw new JsonException($"Deserialized JSON for '{typeof(T).Name}' resulted in null.");

        ValidateNoNullCollectionEntries(result, typeof(T).Name);
        return result;
    }

    public static T LoadFromFile<T>(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var json = File.ReadAllText(filePath);
        return Deserialize<T>(json);
    }

    public static AthleteRegistry LoadRegistry(WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var registryPath = Path.Combine(layout.AthletesDirectory, "registry.json");
        return LoadFromFile<AthleteRegistry>(registryPath);
    }

    public static AthleteProfileDocument LoadAthleteProfile(WorkspaceLayout layout, string slug)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var profilePath = Path.Combine(layout.AthletesDirectory, slug, "athlete.json");
        return LoadFromFile<AthleteProfileDocument>(profilePath);
    }

    public static IReadOnlyList<CoachHistoryEntry> LoadCoachHistory(WorkspaceLayout layout, string slug)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var historyPath = Path.Combine(layout.AthletesDirectory, slug, "coach_history.json");
        return LoadFromFile<List<CoachHistoryEntry>>(historyPath);
    }

    public static IReadOnlyList<IntervalsWellnessEntry> LoadIntervalsWellness(WorkspaceLayout layout, string slug)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var wellnessPath = Path.Combine(layout.AthletesDirectory, slug, "intervals_wellness.json");
        return LoadFromFile<List<IntervalsWellnessEntry>>(wellnessPath);
    }

    private static void ValidateNoNullCollectionEntries(object? instance, string path)
    {
        if (instance is null or string)
        {
            return;
        }

        var type = instance.GetType();
        if (type.IsPrimitive || type.IsEnum || type.IsValueType)
        {
            return;
        }

        if (instance is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var entryPath = $"{path}[\"{entry.Key}\"]";
                if (entry.Value is null)
                {
                    throw new JsonException($"Null dictionary entry is not allowed at '{entryPath}'.");
                }

                ValidateNoNullCollectionEntries(entry.Value, entryPath);
            }

            return;
        }

        if (instance is IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                var itemPath = $"{path}[{index}]";
                if (item is null)
                {
                    throw new JsonException($"Null collection element is not allowed at '{itemPath}'.");
                }

                ValidateNoNullCollectionEntries(item, itemPath);
                index++;
            }

            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            ValidateNoNullCollectionEntries(property.GetValue(instance), $"{path}.{property.Name}");
        }
    }
}
