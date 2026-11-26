using System.Globalization;

namespace Laufen.Domain;

public enum DaemonScheduledActionKind
{
    MorningBriefing,
    EveningPreview,
    ActivityPoll
}

public sealed record DaemonDueAction(
    string AthleteSlug,
    DaemonScheduledActionKind ActionKind,
    DateOnly ReferenceDate,
    string Reason);

public sealed record DaemonRuntimeConfig
{
    public int PollIntervalSeconds { get; init; } = 5;
    public int ActivityPollIntervalMinutes { get; init; } = 10;
    public int PostRunLookbackHours { get; init; } = 6;
    public int TelegramLongPollTimeoutSeconds { get; init; } = 20;
    public bool DryRun { get; init; } = true;
    public bool ApplyCalendar { get; init; }
    public bool SendTelegram { get; init; }
    public bool AllowProductionWrite { get; init; }
    public bool RebuildDashboardOnChange { get; init; } = true;
    public int MaxValidationRetries { get; init; } = 1;
}

public sealed record DaemonTickSummary
{
    public required DateTimeOffset Timestamp { get; init; }
    public required int UpdatesProcessed { get; init; }
    public required int CallbacksAnswered { get; init; }
    public required int OverridesExecuted { get; init; }
    public required int MorningBriefingsTriggered { get; init; }
    public required int EveningPreviewsTriggered { get; init; }
    public required int PostRunReviewsTriggered { get; init; }
    public required int DashboardsRebuilt { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required bool DryRun { get; init; }
}

/// <summary>
/// Pure deterministic scheduler and run-detection evaluator for the .NET Autopilot daemon.
/// All time decisions are strictly driven by an explicit <see cref="DateTimeOffset"/> clock.
/// </summary>
public static class DaemonScheduleEvaluator
{
    public static IReadOnlyList<DaemonDueAction> EvaluateDueActions(
        AthleteRegistry registry,
        IReadOnlyDictionary<string, TelegramStateRecord?> states,
        DateTimeOffset nowLocal,
        int activityPollIntervalMinutes = 10)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(states);

        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var currentTime = TimeOnly.FromDateTime(nowLocal.DateTime);
        var nowUnix = nowLocal.ToUnixTimeSeconds();
        var intervalSeconds = Math.Max(1, activityPollIntervalMinutes) * 60.0;
        var due = new List<DaemonDueAction>();

        foreach (var (slug, entry) in registry.Athletes.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (!entry.Active)
            {
                continue;
            }

            states.TryGetValue(slug, out var state);

            if (!string.IsNullOrWhiteSpace(entry.TelegramChatId))
            {
                if (TelegramPreferencePolicy.IsMorningBriefingEnabled(entry)
                    && TelegramInteractionRouter.ShouldSendMorningBriefing(state, today))
                {
                    var morningStr = TelegramPreferencePolicy.GetMorningBriefingTime(entry);
                    if (TryParseTime(morningStr, out var morningTime) && currentTime >= morningTime)
                    {
                        due.Add(new DaemonDueAction(
                            slug,
                            DaemonScheduledActionKind.MorningBriefing,
                            today,
                            $"Morning briefing due at {morningStr}"));
                    }
                }

                if (TelegramPreferencePolicy.IsEveningBriefingEnabled(entry)
                    && TelegramInteractionRouter.ShouldSendEveningPreview(state, today))
                {
                    var eveningStr = TelegramPreferencePolicy.GetEveningBriefingTime(entry);
                    if (TryParseTime(eveningStr, out var eveningTime) && currentTime >= eveningTime)
                    {
                        due.Add(new DaemonDueAction(
                            slug,
                            DaemonScheduledActionKind.EveningPreview,
                            today,
                            $"Evening preview due at {eveningStr}"));
                    }
                }
            }

            var elapsedSincePoll = state?.LastActivitiesPollTs is { } lastTs
                ? nowUnix - lastTs
                : double.PositiveInfinity;

            if (elapsedSincePoll >= intervalSeconds)
            {
                due.Add(new DaemonDueAction(
                    slug,
                    DaemonScheduledActionKind.ActivityPoll,
                    today,
                    $"Activity poll due ({activityPollIntervalMinutes}m interval)"));
            }
        }

        return due;
    }

    public static IReadOnlyList<IntervalsActivityEntry> DetectNewCompletedRuns(
        IReadOnlyList<IntervalsActivityEntry> activities,
        TelegramStateRecord? state,
        DateTimeOffset nowLocal,
        int lookbackHours = 6)
    {
        ArgumentNullException.ThrowIfNull(activities);

        var localNow = nowLocal.DateTime;
        var cutoff = localNow.AddHours(-Math.Max(1, lookbackHours));

        return PhysiologicalCalculator.FilterValidRuns(activities)
            .Where(a => a.StartDateLocal <= localNow
                && a.StartDateLocal >= cutoff
                && TelegramInteractionRouter.ShouldNotifyCompletedRun(state, a.Id))
            .OrderBy(a => a.StartDateLocal)
            .ToList();
    }

    public static string? ResolveAthleteByChatId(AthleteRegistry registry, string? chatId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (string.IsNullOrWhiteSpace(chatId))
        {
            return null;
        }

        var trimmed = chatId.Trim();
        string? inactiveMatch = null;
        foreach (var (slug, entry) in registry.Athletes.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(entry.TelegramChatId))
            {
                continue;
            }

            if (string.Equals(entry.TelegramChatId.Trim(), trimmed, StringComparison.Ordinal))
            {
                if (entry.Active)
                {
                    return slug;
                }

                inactiveMatch ??= slug;
            }
        }

        return inactiveMatch;
    }

    private static bool TryParseTime(string timeText, out TimeOnly time)
    {
        return TimeOnly.TryParseExact(timeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
            || TimeOnly.TryParseExact(timeText, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}
