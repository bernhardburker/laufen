using System.Globalization;
using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Payload sent to Intervals.icu <c>POST /api/v1/athlete/{id}/events</c>.
/// </summary>
public sealed record IntervalsCalendarEventUpsertRequest
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = "WORKOUT";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "Run";

    [JsonPropertyName("start_date_local")]
    public required string StartDateLocal { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("external_id")]
    public required string ExternalId { get; init; }
}

public sealed record CalendarSyncAction
{
    public required string Kind { get; init; }
    public required DateOnly Date { get; init; }
    public long? ExistingEventId { get; init; }
    public required string Ownership { get; init; }
    public IntervalsCalendarEventUpsertRequest? Request { get; init; }
    public required string Reason { get; init; }
}

public sealed record CalendarSyncPlan
{
    public required string AthleteSlug { get; init; }
    public required string IntervalsAthleteId { get; init; }
    public required DateOnly ReferenceDate { get; init; }
    public required string Trigger { get; init; }
    public required bool IsDryRun { get; init; }
    public required IReadOnlyList<CalendarSyncAction> Actions { get; init; }
    public required int DeleteCount { get; init; }
    public required int CreateCount { get; init; }
    public required int UnchangedCount { get; init; }
    public required int ProtectedSkippedCount { get; init; }
}

public sealed record CalendarSyncExecutionResult
{
    public required CalendarSyncPlan Plan { get; init; }
    public required bool Applied { get; init; }
    public required IReadOnlyList<long> DeletedEventIds { get; init; }
    public required IReadOnlyList<long> CreatedEventIds { get; init; }
    public required IReadOnlyList<long> RetainedAutoEventIds { get; init; }
    public required IReadOnlyList<long> ResultingAutoEventIds { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
}

public sealed record StagingWriteDecision(bool Allowed, string Reason);

/// <summary>
/// Hard safety gate protecting production athletes from unintended live external API writes.
/// </summary>
public static class StagingSafetyGuard
{
    private const string ProductionIntervalsId = "i697752";

    public static StagingWriteDecision EvaluateWritePermission(
        string athleteSlug,
        AthleteRegistryEntry entry,
        bool allowProductionWrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(athleteSlug);
        ArgumentNullException.ThrowIfNull(entry);

        var isStagingTestTenant = athleteSlug.Contains("test", StringComparison.OrdinalIgnoreCase)
            && !entry.Active
            && !string.Equals(entry.IntervalsId, ProductionIntervalsId, StringComparison.OrdinalIgnoreCase);

        if (isStagingTestTenant)
        {
            return new StagingWriteDecision(
                true,
                $"Staging tenant '{athleteSlug}' ({entry.IntervalsId}) permitted for live write.");
        }

        if (allowProductionWrite)
        {
            return new StagingWriteDecision(
                true,
                $"Production write explicitly authorized for '{athleteSlug}' ({entry.IntervalsId}).");
        }

        return new StagingWriteDecision(
            false,
            $"SAFETY BLOCK: Refusing live external write to production athlete '{athleteSlug}' ({entry.IntervalsId}) without explicit --allow-production-write.");
    }
}

/// <summary>
/// Builds idempotent, anti-churn Intervals.icu calendar sync plans from a reconciled 14-day schedule.
/// </summary>
public static class IntervalsCalendarSyncPlanner
{
    public const string AutoExternalIdPrefix = "laufen-auto-";
    public const string LockedExternalIdPrefix = "laufen-locked-";

    public static string BuildExternalId(DateOnly date, string ownership)
    {
        var prefix = string.Equals(ownership, "auto", StringComparison.Ordinal)
            ? AutoExternalIdPrefix
            : LockedExternalIdPrefix;
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{date:yyyy-MM-dd}");
    }

    public static CalendarSyncPlan BuildPlan(
        string athleteSlug,
        string intervalsAthleteId,
        ReconciledAutopilotSchedule schedule,
        IReadOnlyList<CalendarEventEntry> currentCalendar,
        IReadOnlySet<long> autoEventIds,
        IReadOnlySet<DateOnly> userLockedDates,
        bool isDryRun = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(athleteSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(intervalsAthleteId);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(currentCalendar);
        ArgumentNullException.ThrowIfNull(autoEventIds);
        ArgumentNullException.ThrowIfNull(userLockedDates);

        var horizonStart = schedule.ReferenceDate;
        var horizonEnd = schedule.ReferenceDate.AddDays(13);
        var isTelegramOverride = string.Equals(schedule.Trigger, "telegram_override", StringComparison.Ordinal);

        var horizonWorkouts = currentCalendar
            .Where(e => string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Event: e, Date: DateOnly.FromDateTime(e.StartDateLocal)))
            .Where(x => x.Date >= horizonStart && x.Date <= horizonEnd)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Event.Id)
            .ToList();

        var ownershipById = new Dictionary<long, string>();
        var eventsById = new Dictionary<long, (CalendarEventEntry Event, DateOnly Date)>();
        var eventsByDate = new Dictionary<DateOnly, List<CalendarEventEntry>>();

        foreach (var (evt, date) in horizonWorkouts)
        {
            eventsById[evt.Id] = (evt, date);
            if (!eventsByDate.TryGetValue(date, out var list))
            {
                list = [];
                eventsByDate[date] = list;
            }

            list.Add(evt);

            if (!string.IsNullOrEmpty(evt.PairedActivityId))
            {
                ownershipById[evt.Id] = "completed";
            }
            else if (userLockedDates.Contains(date))
            {
                ownershipById[evt.Id] = "user_locked";
            }
            else if (autoEventIds.Contains(evt.Id)
                || (!string.IsNullOrEmpty(evt.ExternalId) && evt.ExternalId.StartsWith(AutoExternalIdPrefix, StringComparison.Ordinal)))
            {
                ownershipById[evt.Id] = "auto";
            }
            else
            {
                ownershipById[evt.Id] = "user_locked";
            }
        }

        var actions = new List<CalendarSyncAction>();
        var handledDeleteIds = new HashSet<long>();
        var retainedUnchangedIds = new HashSet<long>();
        var protectedSkippedIds = new HashSet<long>();

        // 1. Evaluate upserts with anti-churn diffing first
        foreach (var upsert in schedule.CompiledUpserts.OrderBy(u => u.Date))
        {
            var desiredOwnership = upsert.Ownership;
            var desiredName = upsert.Title.Trim();
            var desiredDescription = upsert.CompiledWorkout?.DslText ?? (upsert.Title.Trim() + "\n");
            var normalizedDesiredDesc = NormalizeDescription(desiredDescription);

            var dateEvents = eventsByDate.TryGetValue(upsert.Date, out var existingList)
                ? existingList
                : [];

            var uncompletedOnDate = dateEvents
                .Where(e => !string.Equals(ownershipById[e.Id], "completed", StringComparison.Ordinal))
                .ToList();

            var mutableOnDate = uncompletedOnDate
                .Where(e => string.Equals(ownershipById[e.Id], "auto", StringComparison.Ordinal) || isTelegramOverride)
                .ToList();

            // Only treat as unchanged if there is a single clean mutable event on that date matching title, description, and ownership
            if (uncompletedOnDate.Count == 1
                && mutableOnDate.Count == 1
                && string.Equals(ownershipById[mutableOnDate[0].Id], desiredOwnership, StringComparison.Ordinal)
                && string.Equals(mutableOnDate[0].Name?.Trim(), desiredName, StringComparison.Ordinal)
                && string.Equals(NormalizeDescription(mutableOnDate[0].Description), normalizedDesiredDesc, StringComparison.Ordinal))
            {
                var kept = mutableOnDate[0];
                retainedUnchangedIds.Add(kept.Id);
                actions.Add(new CalendarSyncAction
                {
                    Kind = "skip_unchanged",
                    Date = upsert.Date,
                    ExistingEventId = kept.Id,
                    Ownership = desiredOwnership,
                    Request = null,
                    Reason = $"Existing {desiredOwnership} workout '{desiredName}' matches compiled AST; skipping churn."
                });
                continue;
            }

            foreach (var evt in uncompletedOnDate)
            {
                var own = ownershipById[evt.Id];
                if (string.Equals(own, "user_locked", StringComparison.Ordinal) && !isTelegramOverride)
                {
                    if (protectedSkippedIds.Add(evt.Id))
                    {
                        actions.Add(new CalendarSyncAction
                        {
                            Kind = "skip_protected",
                            Date = upsert.Date,
                            ExistingEventId = evt.Id,
                            Ownership = own,
                            Request = null,
                            Reason = $"Protected {own} event {evt.Id} skipped during '{schedule.Trigger}'."
                        });
                    }

                    continue;
                }

                if (handledDeleteIds.Add(evt.Id))
                {
                    actions.Add(new CalendarSyncAction
                    {
                        Kind = "delete",
                        Date = upsert.Date,
                        ExistingEventId = evt.Id,
                        Ownership = own,
                        Request = null,
                        Reason = $"Replacing {own} workout on {upsert.Date:yyyy-MM-dd} with reconciled session '{desiredName}'."
                    });
                }
            }

            actions.Add(new CalendarSyncAction
            {
                Kind = "create",
                Date = upsert.Date,
                ExistingEventId = null,
                Ownership = desiredOwnership,
                Request = new IntervalsCalendarEventUpsertRequest
                {
                    Category = "WORKOUT",
                    Type = "Run",
                    StartDateLocal = string.Create(CultureInfo.InvariantCulture, $"{upsert.Date:yyyy-MM-dd}T07:00:00"),
                    Name = desiredName,
                    Description = normalizedDesiredDesc + "\n",
                    ExternalId = BuildExternalId(upsert.Date, desiredOwnership)
                },
                Reason = $"Scheduling reconciled {desiredOwnership} workout '{desiredName}' on {upsert.Date:yyyy-MM-dd}."
            });
        }

        // 2. Process explicit deletions from reconciled schedule
        foreach (var delId in schedule.DeletedEventIds)
        {
            if (retainedUnchangedIds.Contains(delId) || handledDeleteIds.Contains(delId))
            {
                continue;
            }

            if (!eventsById.TryGetValue(delId, out var found))
            {
                continue;
            }

            var own = ownershipById[delId];
            if (string.Equals(own, "completed", StringComparison.Ordinal)
                || (string.Equals(own, "user_locked", StringComparison.Ordinal) && !isTelegramOverride))
            {
                if (protectedSkippedIds.Add(delId))
                {
                    actions.Add(new CalendarSyncAction
                    {
                        Kind = "skip_protected",
                        Date = found.Date,
                        ExistingEventId = delId,
                        Ownership = own,
                        Request = null,
                        Reason = $"Protected {own} event {delId} cannot be deleted under trigger '{schedule.Trigger}'."
                    });
                }

                continue;
            }

            handledDeleteIds.Add(delId);
            actions.Add(new CalendarSyncAction
            {
                Kind = "delete",
                Date = found.Date,
                ExistingEventId = delId,
                Ownership = own,
                Request = null,
                Reason = $"Deleting stale/cleared {own} event {delId} on {found.Date:yyyy-MM-dd}."
            });
        }

        return new CalendarSyncPlan
        {
            AthleteSlug = athleteSlug,
            IntervalsAthleteId = intervalsAthleteId,
            ReferenceDate = schedule.ReferenceDate,
            Trigger = schedule.Trigger,
            IsDryRun = isDryRun,
            Actions = actions,
            DeleteCount = actions.Count(a => string.Equals(a.Kind, "delete", StringComparison.Ordinal)),
            CreateCount = actions.Count(a => string.Equals(a.Kind, "create", StringComparison.Ordinal)),
            UnchangedCount = actions.Count(a => string.Equals(a.Kind, "skip_unchanged", StringComparison.Ordinal)),
            ProtectedSkippedCount = actions.Count(a => string.Equals(a.Kind, "skip_protected", StringComparison.Ordinal))
        };
    }

    private static string NormalizeDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
