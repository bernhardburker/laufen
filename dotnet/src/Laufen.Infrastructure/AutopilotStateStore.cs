using System.Globalization;
using System.Text;
using System.Text.Json;
using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Persists isolated .NET autopilot state (<c>block_compass.json</c> and <c>decision_trace_dotnet.jsonl</c>)
/// under <c>athletes/&lt;slug&gt;/</c> or an explicit shadow directory without modifying Python state files.
/// </summary>
public static class AutopilotStateStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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
        File.WriteAllText(path, JsonSerializer.Serialize(compass, IndentedOptions) + "\n", Utf8NoBom);
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
        File.AppendAllText(path, line, Utf8NoBom);
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
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, IndentedOptions) + "\n", Utf8NoBom);

        var mdPath = Path.Combine(dir, "narrative_commentary_dotnet.md");
        File.WriteAllText(mdPath, report.CommentaryMarkdown.TrimEnd() + "\n", Utf8NoBom);
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
        File.WriteAllText(path, JsonSerializer.Serialize(reviews, IndentedOptions) + "\n", Utf8NoBom);
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
        File.WriteAllText(path, json + "\n", Utf8NoBom);
    }

    public static BlockCompassSnapshot? LoadBlockCompassFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, "block_compass.json");
        return File.Exists(path) ? StrictJsonLoader.LoadFromFile<BlockCompassSnapshot>(path) : null;
    }

    public static AutopilotDecisionTraceEntry? LoadLatestDecisionTraceFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, "decision_trace_dotnet.jsonl");
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

    public static void WriteShadowRunArtifacts(
        string directory,
        DateOnly refDate,
        string trigger,
        string provider,
        AutopilotCycleResult cycleResult)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(cycleResult);

        var fullDir = Path.GetFullPath(directory);
        var historyDir = Path.Combine(fullDir, "history");
        Directory.CreateDirectory(fullDir);
        Directory.CreateDirectory(historyDir);

        if (cycleResult.UpdatedCompass is not null)
        {
            File.WriteAllText(
                Path.Combine(fullDir, "block_compass.json"),
                JsonSerializer.Serialize(cycleResult.UpdatedCompass, IndentedOptions) + "\n",
                Utf8NoBom);
        }

        if (cycleResult.PlanningOutcome?.TraceEntry is { } traceEntry)
        {
            File.AppendAllText(
                Path.Combine(fullDir, "decision_trace_dotnet.jsonl"),
                JsonSerializer.Serialize(traceEntry, CompactOptions) + "\n",
                Utf8NoBom);
        }

        if (cycleResult.NarrativeOutcome?.Report is { } narrative)
        {
            File.WriteAllText(
                Path.Combine(fullDir, "narrative_summary_dotnet.json"),
                JsonSerializer.Serialize(narrative, IndentedOptions) + "\n",
                Utf8NoBom);
            File.WriteAllText(
                Path.Combine(fullDir, "narrative_commentary_dotnet.md"),
                narrative.CommentaryMarkdown.TrimEnd() + "\n",
                Utf8NoBom);
        }

        if (cycleResult.TelegramPreview is { } tg)
        {
            var tgSb = new StringBuilder();
            tgSb.Append("TargetDate: ").AppendLine(tg.TargetDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-");
            tgSb.Append("MessageKind: ").AppendLine(tg.MessageKind);
            tgSb.AppendLine("---");
            tgSb.AppendLine(tg.Text.TrimEnd());
            if (tg.ReplyMarkup?.InlineKeyboard is { Count: > 0 } rows)
            {
                var btns = rows.SelectMany(r => r).Select(b => $"[{b.Text} -> {b.CallbackData}]");
                tgSb.AppendLine();
                tgSb.Append("Buttons: ").AppendLine(string.Join(" ", btns));
            }

            File.WriteAllText(Path.Combine(fullDir, "telegram_preview_latest.txt"), tgSb.ToString(), Utf8NoBom);
        }

        var safeTriggerBuilder = new StringBuilder(trigger.Length);
        foreach (var ch in trigger.Trim())
        {
            safeTriggerBuilder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }
        var safeTrigger = safeTriggerBuilder.Length > 0 ? safeTriggerBuilder.ToString() : "run";

        var watchItems = cycleResult.PlanningOutcome?.ReconciledSchedule?.WatchCommitWindow ?? [];
        var nowUtc = DateTimeOffset.UtcNow;
        var historyPayload = new
        {
            timestampUtc = nowUtc,
            referenceDate = refDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            trigger,
            provider,
            hysteresis = cycleResult.Hysteresis,
            healthState = cycleResult.UpdatedCompass?.HealthState,
            updatedCompass = cycleResult.UpdatedCompass,
            traceEntry = cycleResult.PlanningOutcome?.TraceEntry,
            watchWindow = watchItems.Select(w => new
            {
                date = w.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                w.Title,
                w.SessionType,
                w.Ownership,
                durationMinutes = w.TotalDurationSeconds.HasValue ? (int?)Math.Round(w.TotalDurationSeconds.Value / 60.0) : null
            }),
            calendarSyncPlan = cycleResult.CalendarSyncPlan is null
                ? null
                : new
                {
                    cycleResult.CalendarSyncPlan.CreateCount,
                    cycleResult.CalendarSyncPlan.DeleteCount,
                    cycleResult.CalendarSyncPlan.UnchangedCount,
                    cycleResult.CalendarSyncPlan.ProtectedSkippedCount
                },
            narrative = cycleResult.NarrativeOutcome?.Report,
            telegramPreview = cycleResult.TelegramPreview
        };

        File.WriteAllText(
            Path.Combine(historyDir, string.Create(CultureInfo.InvariantCulture, $"{refDate:yyyy-MM-dd}_{safeTrigger}.json")),
            JsonSerializer.Serialize(historyPayload, IndentedOptions) + "\n",
            Utf8NoBom);

        var logSb = new StringBuilder();
        logSb.Append(CultureInfo.InvariantCulture, $"## {refDate:yyyy-MM-dd} `{trigger}` (`{provider}`) — {nowUtc:yyyy-MM-dd HH:mm:ss 'UTC'}").AppendLine();
        logSb.Append(CultureInfo.InvariantCulture, $"- **Hysteresis:** `shouldReplan={cycleResult.Hysteresis.ShouldReplan}` (`{cycleResult.Hysteresis.ReasonCode}`)").AppendLine();
        logSb.Append(CultureInfo.InvariantCulture, $"- **Health State:** `{cycleResult.UpdatedCompass?.HealthState ?? "-"}`").AppendLine();
        if (watchItems.Count > 0)
        {
            var watchSummary = string.Join(
                "; ",
                watchItems.Select(w =>
                {
                    var mins = w.TotalDurationSeconds.HasValue
                        ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(w.TotalDurationSeconds.Value / 60.0)}m")
                        : "-";
                    return string.Create(CultureInfo.InvariantCulture, $"{w.Date:yyyy-MM-dd}: {w.Title} ({mins})");
                }));
            logSb.Append("- **Watch Window:** ").AppendLine(watchSummary);
        }
        if (cycleResult.CalendarSyncPlan is { } syncPlan)
        {
            logSb.Append(CultureInfo.InvariantCulture, $"- **Calendar Sync Plan:** create={syncPlan.CreateCount}, delete={syncPlan.DeleteCount}, skip_unchanged={syncPlan.UnchangedCount}, skip_protected={syncPlan.ProtectedSkippedCount}").AppendLine();
        }
        if (cycleResult.NarrativeOutcome?.Report is { } nar)
        {
            logSb.Append("- **Narrator:** `").Append(nar.StatusBadge).Append("` — ").AppendLine(nar.StatusTitle);
        }
        if (cycleResult.TelegramPreview is { } preview)
        {
            logSb.Append("- **Telegram Preview (").Append(preview.MessageKind).Append("):** ").AppendLine(preview.Text.Replace('\n', ' '));
        }
        logSb.AppendLine();

        File.AppendAllText(Path.Combine(fullDir, "shadow_log.md"), logSb.ToString(), Utf8NoBom);
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
