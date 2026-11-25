using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Laufen.Domain;

/// <summary>
/// Snapshot of the latest briefing proposal stored in <c>telegram_state*.json</c>.
/// </summary>
public sealed record TelegramProposalSnapshot
{
    [JsonPropertyName("target_date")]
    public string? TargetDate { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("event_id")]
    public long? EventId { get; init; }

    [JsonPropertyName("is_rest_day_today")]
    public bool? IsRestDayToday { get; init; }
}

/// <summary>
/// Strict contract compatible with both <c>telegram_state.json</c> and <c>telegram_state_dotnet.json</c>.
/// </summary>
public sealed record TelegramStateRecord
{
    [JsonPropertyName("last_briefing_date")]
    public string? LastBriefingDate { get; init; }

    [JsonPropertyName("last_dispatched_briefing_date")]
    public string? LastDispatchedBriefingDate { get; init; }

    [JsonPropertyName("last_evening_preview_date")]
    public string? LastEveningPreviewDate { get; init; }

    [JsonPropertyName("last_dispatched_evening_date")]
    public string? LastDispatchedEveningDate { get; init; }

    [JsonPropertyName("current_proposal")]
    public TelegramProposalSnapshot? CurrentProposal { get; init; }

    [JsonPropertyName("notified_activity_ids")]
    public IReadOnlyList<string>? NotifiedActivityIds { get; init; }

    [JsonPropertyName("user_locked_dates")]
    public IReadOnlyList<DateOnly>? UserLockedDates { get; init; }

    [JsonPropertyName("last_activities_poll_ts")]
    public double? LastActivitiesPollTs { get; init; }

    [JsonPropertyName("pending_run_note")]
    public JsonElement? PendingRunNote { get; init; }

    [JsonPropertyName("awaiting_run_note_for_id")]
    public string? AwaitingRunNoteForId { get; init; }

    [JsonPropertyName("initialized")]
    public bool? Initialized { get; init; }
}

public sealed record TelegramInlineButton
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("callback_data")]
    public required string CallbackData { get; init; }
}

public sealed record TelegramReplyMarkup
{
    [JsonPropertyName("inline_keyboard")]
    public required IReadOnlyList<IReadOnlyList<TelegramInlineButton>> InlineKeyboard { get; init; }
}

public sealed record TelegramOutgoingMessage
{
    public required string ChatId { get; init; }
    public required string Text { get; init; }
    public string ParseMode { get; init; } = "HTML";
    public TelegramReplyMarkup? ReplyMarkup { get; init; }
    public DateOnly? TargetDate { get; init; }
    public required string MessageKind { get; init; }
}

public sealed record TelegramUpdateMessage
{
    public required long MessageId { get; init; }
    public required string ChatId { get; init; }
    public string? Text { get; init; }
    public long UnixTimestamp { get; init; }
}

public sealed record TelegramCallbackQuery
{
    public required string Id { get; init; }
    public required string ChatId { get; init; }
    public required string Data { get; init; }
    public string? MessageText { get; init; }
}

public sealed record TelegramIncomingUpdate
{
    public required long UpdateId { get; init; }
    public TelegramUpdateMessage? Message { get; init; }
    public TelegramCallbackQuery? CallbackQuery { get; init; }
}

public sealed record TelegramInteractionRoute
{
    public required string Kind { get; init; }
    public DateOnly? TargetDate { get; init; }
    public required bool RequiresLlmReplan { get; init; }
    public required string Trigger { get; init; }
    public string? AthleteMessage { get; init; }
    public required bool LocksDate { get; init; }
    public required string AcknowledgementText { get; init; }
    public TelegramReplyMarkup? ReplyMarkup { get; init; }
}

/// <summary>
/// Resolves athlete Telegram notification preferences consistently with <c>athletes/registry.json</c>.
/// </summary>
public static class TelegramPreferencePolicy
{
    public static bool IsMorningBriefingEnabled(AthleteRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (entry.BriefingEnabled ?? false) && (entry.MorningBriefingEnabled ?? true);
    }

    public static bool IsEveningBriefingEnabled(AthleteRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (entry.BriefingEnabled ?? false) && (entry.EveningBriefingEnabled ?? false);
    }

    public static bool IsPostRunSummaryEnabled(AthleteRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (entry.BriefingEnabled ?? false) && (entry.PostRunSummaryEnabled ?? true);
    }

    public static string GetMorningBriefingTime(AthleteRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.IsNullOrWhiteSpace(entry.BriefingTime) ? "06:30" : entry.BriefingTime.Trim();
    }

    public static string GetEveningBriefingTime(AthleteRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.IsNullOrWhiteSpace(entry.EveningBriefingTime) ? "20:00" : entry.EveningBriefingTime.Trim();
    }
}

/// <summary>
/// Formats concise, HR-clean HTML morning briefings and post-run feedback messages for Telegram.
/// </summary>
public static partial class TelegramAutopilotPresenter
{
    private static readonly HashSet<string> AerobicSessionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "recovery",
        "easy",
        "base_aerobic",
        "run_walk",
        "long_run"
    };

    private static readonly Regex PaceTokenPattern = new(
        @"\b\d{1,2}:\d{2}(?:\s*-\s*\d{1,2}:\d{2})?\s*(?:/km|min/km)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static TelegramOutgoingMessage FormatMorningBriefing(
        string chatId,
        CoachIntelligenceDossier dossier,
        ReconciledAutopilotSchedule? schedule = null,
        BlockCompassSnapshot? compass = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentNullException.ThrowIfNull(dossier);

        var targetDate = dossier.TargetDate;
        var healthState = schedule?.HealthState ?? compass?.HealthState ?? dossier.CurrentCompass?.HealthState ?? "Normal";
        var isSickHold = string.Equals(healthState, "SickHold", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        var dayLabel = targetDate.ToString("ddd, dd.MM.", CultureInfo.InvariantCulture);
        sb.Append("<b>Guten Morgen, ")
            .Append(EscapeHtml(dossier.Athlete.Name))
            .Append("</b> — ")
            .AppendLine(EscapeHtml(dayLabel));

        if (isSickHold)
        {
            sb.AppendLine("⚠️ <b>SickHold aktiv:</b> Fokus voll auf Erholung, keine Laufeinheit geplant.");
        }
        else if (string.Equals(healthState, "ReEntry", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("🌱 <b>Re-Entry-Modus:</b> Vorsichtiger Einstieg, rein nach Herzfrequenz und Gefuehl.");
        }
        else if (dossier.Readiness.MedicalRedFlags.Count > 0)
        {
            var latestFlag = dossier.Readiness.MedicalRedFlags[^1];
            sb.Append("⚠️ <b>Signal:</b> ").AppendLine(EscapeHtml(latestFlag.Detail));
        }

        AppendReadinessSummary(sb, dossier.Readiness);

        var scheduledToday = schedule?.WatchCommitWindow.FirstOrDefault(w => w.Date == targetDate);
        var calendarToday = dossier.Calendar.EventsOnTargetDate.FirstOrDefault(e =>
            string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase));

        bool hasWorkoutToday;
        if (isSickHold)
        {
            hasWorkoutToday = false;
            sb.AppendLine().AppendLine("<b>Heute:</b> Ruhetag (SickHold)");
        }
        else if (scheduledToday is not null)
        {
            hasWorkoutToday = true;
            var mins = scheduledToday.TotalDurationSeconds.HasValue
                ? $" ({scheduledToday.TotalDurationSeconds.Value / 60}m)"
                : string.Empty;
            var safeTitle = SanitizeTitle(scheduledToday.Title, scheduledToday.SessionType);
            sb.AppendLine()
                .Append("<b>Heute:</b> ")
                .Append(EscapeHtml(safeTitle))
                .Append(EscapeHtml(mins))
                .Append(" [").Append(EscapeHtml(scheduledToday.Ownership)).AppendLine("]");

            if (scheduledToday.CompiledWorkout?.DslText is { Length: > 0 } dsl)
            {
                var safeDsl = SanitizeTextForModality(dsl, scheduledToday.SessionType);
                sb.Append("<pre>").Append(EscapeHtml(safeDsl.TrimEnd())).AppendLine("</pre>");
            }
        }
        else if (calendarToday is not null)
        {
            hasWorkoutToday = true;
            var mins = calendarToday.DurationSeconds.HasValue
                ? $" ({calendarToday.DurationSeconds.Value / 60}m)"
                : string.Empty;
            var inferredSession = InferSessionType(calendarToday.Name);
            var safeTitle = SanitizeTitle(calendarToday.Name, inferredSession);
            sb.AppendLine()
                .Append("<b>Heute:</b> ")
                .Append(EscapeHtml(safeTitle))
                .Append(EscapeHtml(mins))
                .Append(" [").Append(EscapeHtml(calendarToday.Ownership)).AppendLine("]");
        }
        else
        {
            hasWorkoutToday = false;
            sb.AppendLine().AppendLine("<b>Heute:</b> Ruhetag (Erholung)");
        }

        AppendWatchPreview(sb, targetDate, schedule, dossier.Calendar.UpcomingEventsNext7Days);

        return new TelegramOutgoingMessage
        {
            ChatId = chatId,
            Text = sb.ToString().TrimEnd(),
            ReplyMarkup = BuildMorningKeyboard(targetDate, hasWorkoutToday, isSickHold),
            TargetDate = targetDate,
            MessageKind = "morning_briefing"
        };
    }

    public static TelegramOutgoingMessage FormatPostRunMessage(
        string chatId,
        RunReviewRecord review,
        NarrativeReport? narrative = null,
        ReconciledAutopilotSchedule? schedule = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentNullException.ThrowIfNull(review);

        var sb = new StringBuilder();
        sb.Append("<b>Lauf-Review: ")
            .Append(EscapeHtml(review.Name))
            .AppendLine("</b>");

        sb.Append(review.DistanceKm.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" km in ")
            .Append(EscapeHtml(review.MovingTimeText))
            .Append(" | Ø HF ")
            .Append(review.AverageHeartRate.ToString("F0", CultureInfo.InvariantCulture))
            .Append(" bpm | Z1-Z2 ")
            .Append(review.Z1Z2Percentage.ToString("F0", CultureInfo.InvariantCulture))
            .Append("% | Kadenz ")
            .Append(review.CadenceSpm.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine(" spm");

        sb.Append("<b>Bewertung:</b> ").AppendLine(EscapeHtml(review.Review.RatingLabel));
        if (!string.IsNullOrWhiteSpace(review.Review.Summary))
        {
            sb.AppendLine(EscapeHtml(review.Review.Summary));
        }

        if (!string.IsNullOrWhiteSpace(review.Review.CoachTip))
        {
            sb.Append("<b>Tipp:</b> ").AppendLine(EscapeHtml(review.Review.CoachTip));
        }

        if (narrative is not null && !string.IsNullOrWhiteSpace(narrative.StatusTitle))
        {
            sb.Append("<b>Block-Status:</b> ").AppendLine(EscapeHtml(narrative.StatusTitle));
        }

        var nextRun = schedule?.WatchCommitWindow.FirstOrDefault(w => w.Date > DateOnly.FromDateTime(review.Date));
        if (nextRun is not null)
        {
            var safeTitle = SanitizeTitle(nextRun.Title, nextRun.SessionType);
            sb.Append("<b>Ausblick ")
                .Append(nextRun.Date.ToString("ddd dd.MM.", CultureInfo.InvariantCulture))
                .Append(":</b> ")
                .AppendLine(EscapeHtml(safeTitle));
        }

        return new TelegramOutgoingMessage
        {
            ChatId = chatId,
            Text = sb.ToString().TrimEnd(),
            ReplyMarkup = null,
            TargetDate = DateOnly.FromDateTime(review.Date),
            MessageKind = "post_run_summary"
        };
    }

    public static TelegramReplyMarkup BuildMorningKeyboard(DateOnly targetDate, bool hasWorkoutToday, bool isSickHold)
    {
        var dateTag = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (isSickHold)
        {
            return new TelegramReplyMarkup
            {
                InlineKeyboard =
                [
                    [
                        new TelegramInlineButton { Text = "🌱 Bereit fuer Re-Entry", CallbackData = $"reentry_ready:{dateTag}" },
                        new TelegramInlineButton { Text = "🛌 Weiter Ruhetag", CallbackData = $"keep_rest:{dateTag}" }
                    ]
                ]
            };
        }

        if (hasWorkoutToday)
        {
            return new TelegramReplyMarkup
            {
                InlineKeyboard =
                [
                    [
                        new TelegramInlineButton { Text = "✅ Passt!", CallbackData = $"accept:{dateTag}" },
                        new TelegramInlineButton { Text = "😌 Leichter", CallbackData = $"easier:{dateTag}" }
                    ],
                    [
                        new TelegramInlineButton { Text = "⏱ Kuerzer 35m", CallbackData = $"shorter35:{dateTag}" },
                        new TelegramInlineButton { Text = "🛌 Ruhetag", CallbackData = $"rest:{dateTag}" }
                    ]
                ]
            };
        }

        return new TelegramReplyMarkup
        {
            InlineKeyboard =
            [
                [
                    new TelegramInlineButton { Text = "🛌 Ruhetag passt", CallbackData = $"keep_rest:{dateTag}" },
                    new TelegramInlineButton { Text = "🏃 Heute doch laufen", CallbackData = $"run_today:{dateTag}" }
                ]
            ]
        };
    }

    private static void AppendReadinessSummary(StringBuilder sb, ReadinessSignalDossier readiness)
    {
        var parts = new List<string>();
        if (readiness.RestingHrDelta.HasValue)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"RHR {readiness.RestingHrDelta.Value:+0.0;-0.0;0.0}"));
        }

        if (readiness.HrvBaselineRatio.HasValue)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"HRV {readiness.HrvBaselineRatio.Value * 100:F0}%"));
        }

        if (readiness.SleepHoursLatest.HasValue)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Schlaf {readiness.SleepHoursLatest.Value:F1}h"));
        }

        if (readiness.Tsb.HasValue)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"TSB {readiness.Tsb.Value:+0.0;-0.0;0.0}"));
        }

        if (parts.Count > 0)
        {
            sb.Append("<b>Readiness:</b> ").AppendLine(EscapeHtml(string.Join(" | ", parts)));
        }
    }

    private static void AppendWatchPreview(
        StringBuilder sb,
        DateOnly targetDate,
        ReconciledAutopilotSchedule? schedule,
        IReadOnlyList<ScheduledCalendarItem> fallbackUpcoming)
    {
        var upcomingLines = new List<string>();
        if (schedule is not null)
        {
            foreach (var item in schedule.WatchCommitWindow.Where(w => w.Date > targetDate).Take(3))
            {
                var safeTitle = SanitizeTitle(item.Title, item.SessionType);
                upcomingLines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{item.Date:ddd dd.MM.}: {safeTitle}"));
            }
        }
        else
        {
            foreach (var item in fallbackUpcoming.Where(e =>
                e.Date > targetDate && string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                var safeTitle = SanitizeTitle(item.Name, InferSessionType(item.Name));
                upcomingLines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{item.Date:ddd dd.MM.}: {safeTitle}"));
            }
        }

        if (upcomingLines.Count > 0)
        {
            sb.AppendLine().Append("<b>Naechste Tage:</b> ").AppendLine(EscapeHtml(string.Join(" | ", upcomingLines)));
        }
    }

    private static string SanitizeTitle(string title, string? sessionType)
    {
        return SanitizeTextForModality(title, sessionType);
    }

    private static string SanitizeTextForModality(string text, string? sessionType)
    {
        if (!string.IsNullOrWhiteSpace(sessionType) && !AerobicSessionTypes.Contains(sessionType))
        {
            return text;
        }

        var cleaned = PaceTokenPattern.Replace(text, "Z2 HF").Trim();
        return cleaned;
    }

    private static string? InferSessionType(string title)
    {
        if (title.Contains("tempo", StringComparison.OrdinalIgnoreCase)
            || title.Contains("intervall", StringComparison.OrdinalIgnoreCase)
            || title.Contains("speed", StringComparison.OrdinalIgnoreCase)
            || title.Contains("cadence", StringComparison.OrdinalIgnoreCase)
            || title.Contains("strid", StringComparison.OrdinalIgnoreCase))
        {
            return "tempo_steady";
        }

        return "easy";
    }

    private static string EscapeHtml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}

/// <summary>
/// Routes Telegram callbacks, commands, and free-text overrides while honoring auth, locks, and idempotency.
/// </summary>
public static class TelegramInteractionRouter
{
    public static bool IsUserAuthorized(AthleteRegistryEntry entry, string? incomingChatId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.TelegramChatId) || string.IsNullOrWhiteSpace(incomingChatId))
        {
            return false;
        }

        return string.Equals(entry.TelegramChatId.Trim(), incomingChatId.Trim(), StringComparison.Ordinal);
    }

    public static bool ShouldSendMorningBriefing(TelegramStateRecord? state, DateOnly refDate, bool force = false)
    {
        if (force)
        {
            return true;
        }

        if (state is null)
        {
            return true;
        }

        var todayStr = refDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return !string.Equals(state.LastBriefingDate, todayStr, StringComparison.Ordinal)
            && !string.Equals(state.LastDispatchedBriefingDate, todayStr, StringComparison.Ordinal);
    }

    public static bool ShouldNotifyCompletedRun(TelegramStateRecord? state, string activityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        if (state?.NotifiedActivityIds is null)
        {
            return true;
        }

        return !state.NotifiedActivityIds.Contains(activityId, StringComparer.Ordinal);
    }

    public static TelegramInteractionRoute RouteCallback(string? callbackData, DateOnly defaultDate)
    {
        if (string.IsNullOrWhiteSpace(callbackData))
        {
            return new TelegramInteractionRoute
            {
                Kind = "ignored",
                TargetDate = defaultDate,
                RequiresLlmReplan = false,
                Trigger = "intraday_check",
                LocksDate = false,
                AcknowledgementText = "Leere Aktion ignoriert."
            };
        }

        var parts = callbackData.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
        var action = parts[0].ToLowerInvariant();
        var targetDate = defaultDate;
        if (parts.Length == 2
            && DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            targetDate = parsedDate;
        }

        return action switch
        {
            "accept" => new TelegramInteractionRoute
            {
                Kind = "accept_day_lock",
                TargetDate = targetDate,
                RequiresLlmReplan = false,
                Trigger = "intraday_check",
                LocksDate = true,
                AcknowledgementText = $"✅ {targetDate:dd.MM.} als fest bestaetigt (user_locked)."
            },
            "keep_rest" => new TelegramInteractionRoute
            {
                Kind = "keep_rest_lock",
                TargetDate = targetDate,
                RequiresLlmReplan = false,
                Trigger = "intraday_check",
                LocksDate = true,
                AcknowledgementText = $"🛌 Ruhetag am {targetDate:dd.MM.} bestaetigt (user_locked)."
            },
            "easier" => new TelegramInteractionRoute
            {
                Kind = "telegram_override",
                TargetDate = targetDate,
                RequiresLlmReplan = true,
                Trigger = "telegram_override",
                AthleteMessage = $"Bitte die Einheit am {targetDate:yyyy-MM-dd} deutlich leichter und rein aerob anpassen.",
                LocksDate = false,
                AcknowledgementText = "😌 Plane eine leichtere Einheit ..."
            },
            "shorter35" => new TelegramInteractionRoute
            {
                Kind = "telegram_override",
                TargetDate = targetDate,
                RequiresLlmReplan = true,
                Trigger = "telegram_override",
                AthleteMessage = $"Ich habe am {targetDate:yyyy-MM-dd} maximal 35 Minuten Zeit: bitte auf eine kurze Einheit kuerzen.",
                LocksDate = false,
                AcknowledgementText = "⏱ Plane maximal 35 Minuten ..."
            },
            "rest" => new TelegramInteractionRoute
            {
                Kind = "telegram_override",
                TargetDate = targetDate,
                RequiresLlmReplan = true,
                Trigger = "telegram_override",
                AthleteMessage = $"Bitte am {targetDate:yyyy-MM-dd} auf Ruhetag umstellen (user_locked) und die naechsten Tage neu balancieren.",
                LocksDate = true,
                AcknowledgementText = "🛌 Stelle auf Ruhetag um und balanciere die Woche ..."
            },
            "run_today" => new TelegramInteractionRoute
            {
                Kind = "telegram_override",
                TargetDate = targetDate,
                RequiresLlmReplan = true,
                Trigger = "telegram_override",
                AthleteMessage = $"Ich fuehle mich am {targetDate:yyyy-MM-dd} gut und moechte doch eine lockere Einheit laufen.",
                LocksDate = false,
                AcknowledgementText = "🏃 Plane eine passende Einheit fuer heute ..."
            },
            "reentry_ready" => new TelegramInteractionRoute
            {
                Kind = "reentry_ready",
                TargetDate = targetDate,
                RequiresLlmReplan = true,
                Trigger = "telegram_override",
                AthleteMessage = $"Ich fuehle mich am {targetDate:yyyy-MM-dd} wieder gesund und bereit fuer einen vorsichtigen Re-Entry.",
                LocksDate = false,
                AcknowledgementText = "🌱 Starte vorsichtigen Re-Entry-Plan ..."
            },
            _ => new TelegramInteractionRoute
            {
                Kind = "ignored",
                TargetDate = targetDate,
                RequiresLlmReplan = false,
                Trigger = "intraday_check",
                LocksDate = false,
                AcknowledgementText = $"Unbekannte Aktion '{action}'."
            }
        };
    }

    public static TelegramInteractionRoute RouteFreeText(string? messageText, DateOnly defaultDate)
    {
        if (string.IsNullOrWhiteSpace(messageText))
        {
            return new TelegramInteractionRoute
            {
                Kind = "ignored",
                TargetDate = defaultDate,
                RequiresLlmReplan = false,
                Trigger = "intraday_check",
                LocksDate = false,
                AcknowledgementText = string.Empty
            };
        }

        var trimmed = messageText.Trim();
        if (string.Equals(trimmed, "/start", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "/help", StringComparison.OrdinalIgnoreCase))
        {
            return new TelegramInteractionRoute
            {
                Kind = "help",
                TargetDate = defaultDate,
                RequiresLlmReplan = false,
                Trigger = "intraday_check",
                LocksDate = false,
                AcknowledgementText = "Schreibe mir jederzeit deinen Wunsch (z.B. 'Heute nur 30m Zeit' oder 'Beine muede') oder nutze die Buttons."
            };
        }

        return new TelegramInteractionRoute
        {
            Kind = "telegram_override",
            TargetDate = defaultDate,
            RequiresLlmReplan = true,
            Trigger = "telegram_override",
            AthleteMessage = trimmed,
            LocksDate = false,
            AcknowledgementText = "Coach wertet deine Nachricht aus und passt den Plan an ..."
        };
    }

    public static TelegramStateRecord ApplyStateLock(TelegramStateRecord? existing, DateOnly lockDate)
    {
        var current = existing ?? new TelegramStateRecord();
        var updatedLocks = (current.UserLockedDates ?? [])
            .Append(lockDate)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        return current with
        {
            UserLockedDates = updatedLocks
        };
    }

    public static TelegramStateRecord MarkMorningBriefingSent(
        TelegramStateRecord? existing,
        DateOnly refDate,
        TelegramProposalSnapshot? proposal = null)
    {
        var current = existing ?? new TelegramStateRecord();
        var dateStr = refDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return current with
        {
            LastBriefingDate = dateStr,
            LastDispatchedBriefingDate = dateStr,
            CurrentProposal = proposal ?? current.CurrentProposal
        };
    }

    public static TelegramStateRecord MarkActivityNotified(TelegramStateRecord? existing, string activityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        var current = existing ?? new TelegramStateRecord();
        var updatedIds = (current.NotifiedActivityIds ?? [])
            .Append(activityId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return current with
        {
            NotifiedActivityIds = updatedIds
        };
    }
}
