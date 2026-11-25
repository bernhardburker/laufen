using System.Globalization;
using System.Text.Json;
using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Context-aware deterministic <see cref="ILlmClient"/> stub for offline multi-day simulation tests and CLI dry-runs.
/// Parses the serialized prompt dossier and synthesizes coaching-DNA-compliant <see cref="AutopilotPlanDecision"/> JSON.
/// </summary>
public sealed class SimulationStubLlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string[] IllnessKeywords =
        ["krank", "erkält", "fieber", "infekt", "sick", "ill", "flu"];

    private static readonly string[] RecoveryKeywords =
        ["wieder fit", "fieber ist weg", "kein fieber", "keine beschwerden", "gesund", "erholt"];

    private static readonly string[] RestKeywords =
        ["ruhetag", "streichen", "pausier", "rest day", "skip today"];

    private static readonly string[] DowngradeKeywords =
        ["lockerer", "leichter", "nur 30", "schwer", "müde"];

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var contextJson = ExtractContextJson(request.UserPrompt);
        using var doc = JsonDocument.Parse(contextJson);
        var root = doc.RootElement;

        var decision = BuildDecision(root);
        var json = JsonSerializer.Serialize(decision, SerializerOptions);

        return Task.FromResult(new LlmResponse(
            Success: true,
            Text: json,
            Error: null,
            Model: "simulation-stub-v1",
            ElapsedSeconds: 0.01));
    }

    private static AutopilotPlanDecision BuildDecision(JsonElement root)
    {
        var trigger = GetString(root, "trigger") ?? "morning_sync";
        var refDate = DateOnly.ParseExact(GetString(root, "reference_date")!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var athleteMessage = GetString(root, "athlete_message");

        var athleteEl = GetObj(root, "athlete");
        var allowConsecutive = GetBool(athleteEl, "AllowConsecutiveRunDays") ?? true;

        var readinessEl = GetObj(root, "readiness");
        var redFlagsCount = GetArrayLength(readinessEl, "MedicalRedFlags");

        var stimEl = GetObj(root, "stimulus_progression");
        var ledgerEl = GetObj(stimEl, "Ledger");
        var daysSinceLastRun = GetInt(ledgerEl, "DaysSinceLastRun");
        var daysSinceLastQuality = GetInt(ledgerEl, "DaysSinceLastQualityStimulus");

        var breakEl = GetObj(stimEl, "BreakReentry");
        var hasExtendedBreak = GetBool(breakEl, "HasRecentExtendedBreak") ?? false;
        var cleanBaseSinceBreak = GetInt(breakEl, "CleanBaseRunsSinceRecentBreak") ?? 0;

        var baseChain = ParseBaseChain(stimEl);
        var calendarEl = GetObj(root, "calendar_14d");
        var upcomingEvents = ParseUpcomingEvents(calendarEl);
        var missedAutoCount = GetArrayLength(calendarEl, "MissedAutoWorkoutsLast7Days");
        var lockedDates = ParseDateSet(calendarEl, "UserLockedDates");
        var hasCompletedToday = GetBool(calendarEl, "HasCompletedRunOnTargetDate") ?? false;

        var compassEl = GetObj(root, "current_compass");
        var prevHealthState = compassEl.HasValue ? GetString(compassEl.Value, "health_state") : null;
        var compassUpdatedAt = compassEl.HasValue ? TryGetDate(compassEl.Value, "updated_at") : null;

        var cleanRunsSinceCompass = compassUpdatedAt.HasValue
            ? baseChain.Count(b => b.Date >= compassUpdatedAt.Value && !b.IsCompromised && (b.Z1Z2Pct is null or >= 80.0))
            : 0;

        var hasRecoveryMessage = ContainsAny(athleteMessage, RecoveryKeywords);
        var hasIllnessMessage = !hasRecoveryMessage && ContainsAny(athleteMessage, IllnessKeywords);
        string healthState;
        if (hasIllnessMessage || redFlagsCount > 0)
        {
            healthState = "SickHold";
        }
        else if (string.Equals(prevHealthState, "SickHold", StringComparison.Ordinal))
        {
            healthState = "ReEntry";
        }
        else if (hasExtendedBreak && cleanBaseSinceBreak < 2)
        {
            healthState = "ReEntry";
        }
        else if (string.Equals(prevHealthState, "ReEntry", StringComparison.Ordinal) && cleanRunsSinceCompass < 2)
        {
            healthState = "ReEntry";
        }
        else
        {
            healthState = "Normal";
        }

        var operations = new List<ScheduledDayOperation>();
        var remainingAutoIds = new List<long>();

        if (string.Equals(healthState, "SickHold", StringComparison.Ordinal))
        {
            foreach (var evt in upcomingEvents)
            {
                var isFrozenToday = string.Equals(trigger, "intraday_check", StringComparison.Ordinal) && evt.Date == refDate;
                if (!evt.IsCompleted && string.Equals(evt.Ownership, "auto", StringComparison.Ordinal) && !isFrozenToday)
                {
                    operations.Add(new ScheduledDayOperation
                    {
                        Date = evt.Date,
                        Operation = "delete",
                        EventId = evt.Id,
                        Reason = "SickHold clears open auto workouts to remove pressure during illness."
                    });
                }
                else if (string.Equals(evt.Ownership, "auto", StringComparison.Ordinal))
                {
                    remainingAutoIds.Add(evt.Id);
                }
            }

            return new AutopilotPlanDecision
            {
                Trigger = trigger,
                ReferenceDate = refDate,
                HorizonDays = 14,
                HealthState = "SickHold",
                Operations = operations,
                Rationale = new CoachDecisionRationale
                {
                    Observations = BuildSickHoldObservations(athleteMessage, redFlagsCount, operations.Count),
                    TradeoffWeighed = "Complete physical recovery prioritized over maintaining short-term weekly mileage",
                    ProgressionIntent = "Hold all planned running sessions until fever and RHR elevation resolve, then enter ReEntry",
                    RejectedAlternative = "Keeping even Z1 recovery workouts rejected because active illness signals forbid planned upserts"
                },
                UpdatedCompass = new BlockCompassSnapshot
                {
                    UpdatedAt = refDate,
                    CurrentBlockFocus = "Acute illness recovery (SickHold)",
                    BaseMilestone = "Pause running until RHR normalizes and athlete reports recovery",
                    QualityMilestone = "Quality paused during SickHold and ReEntry gate",
                    ObservedAthletePatterns = ["Prioritize clean recovery before structured load"],
                    LastDecisionSummary = $"Entered SickHold on {refDate:yyyy-MM-dd} and cleared open auto workouts.",
                    HealthState = "SickHold",
                    AutoEventIds = remainingAutoIds
                }
            };
        }

        var isRestOverride = string.Equals(trigger, "telegram_override", StringComparison.Ordinal)
            && ContainsAny(athleteMessage, RestKeywords);
        var isDowngradeOverride = string.Equals(trigger, "telegram_override", StringComparison.Ordinal)
            && ContainsAny(athleteMessage, DowngradeKeywords);

        // Identify existing events that remain fixed (completed, frozen today under intraday_check, or user_locked unless telegram_override targets today)
        var fixedDates = new HashSet<DateOnly>();
        var fixedEvents = new List<ParsedCalendarEvt>();
        var mutableEvents = new List<ParsedCalendarEvt>();

        foreach (var evt in upcomingEvents)
        {
            var canOverrideToday = string.Equals(trigger, "telegram_override", StringComparison.Ordinal)
                && evt.Date == refDate
                && (isRestOverride || isDowngradeOverride);

            var isImmutable = evt.IsCompleted
                || (string.Equals(trigger, "intraday_check", StringComparison.Ordinal) && evt.Date == refDate)
                || (string.Equals(evt.Ownership, "user_locked", StringComparison.Ordinal) && !canOverrideToday);

            if (isImmutable)
            {
                fixedEvents.Add(evt);
                if (string.Equals(evt.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase))
                {
                    fixedDates.Add(evt.Date);
                }
            }
            else if (string.Equals(evt.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase))
            {
                mutableEvents.Add(evt);
            }
        }

        // Handle user_locked_dates without an event on them yet
        foreach (var ld in lockedDates)
        {
            if (!(string.Equals(trigger, "telegram_override", StringComparison.Ordinal) && ld == refDate && (isRestOverride || isDowngradeOverride)))
            {
                fixedDates.Add(ld);
            }
        }

        if (isRestOverride)
        {
            foreach (var evt in mutableEvents.Where(e => e.Date == refDate))
            {
                operations.Add(new ScheduledDayOperation
                {
                    Date = evt.Date,
                    Operation = "delete",
                    EventId = evt.Id,
                    Reason = "Athlete requested rest day via Telegram override."
                });
            }

            fixedDates.Add(refDate);
        }

        var blockedDates = new HashSet<DateOnly>(fixedDates);
        if (hasCompletedToday || daysSinceLastRun == 0)
        {
            blockedDates.Add(refDate);
            if (!allowConsecutive)
            {
                blockedDates.Add(refDate.AddDays(1));
            }
        }

        if (!allowConsecutive && daysSinceLastRun == 1)
        {
            blockedDates.Add(refDate);
        }

        if (!allowConsecutive)
        {
            foreach (var fd in fixedDates)
            {
                blockedDates.Add(fd.AddDays(-1));
                blockedDates.Add(fd.AddDays(1));
            }
        }

        if (string.Equals(trigger, "intraday_check", StringComparison.Ordinal))
        {
            blockedDates.Add(refDate);
            if (!allowConsecutive && upcomingEvents.Any(e => e.Date == refDate && string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase)))
            {
                blockedDates.Add(refDate.AddDays(1));
            }
        }

        var mutableByDate = mutableEvents
            .Where(e => !(isRestOverride && e.Date == refDate))
            .GroupBy(e => e.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        var selectedUpsertDates = new List<DateOnly>();

        if (isDowngradeOverride && !isRestOverride && !blockedDates.Contains(refDate))
        {
            selectedUpsertDates.Add(refDate);
        }

        SelectWindowDates(
            refDate,
            0,
            6,
            targetSessions: 3,
            allowConsecutive,
            fixedDates,
            blockedDates,
            mutableByDate,
            selectedUpsertDates);

        SelectWindowDates(
            refDate,
            7,
            13,
            targetSessions: 3,
            allowConsecutive,
            fixedDates,
            blockedDates,
            mutableByDate,
            selectedUpsertDates);

        var upsertDateSet = selectedUpsertDates.ToHashSet();

        // Explicit delete for mutable events on vacated/conflicting dates
        foreach (var evt in mutableEvents)
        {
            if (isRestOverride && evt.Date == refDate)
            {
                continue;
            }

            if (!upsertDateSet.Contains(evt.Date))
            {
                operations.Add(new ScheduledDayOperation
                {
                    Date = evt.Date,
                    Operation = "delete",
                    EventId = evt.Id,
                    Reason = "Re-spaced 14-day schedule removes stale/conflicting auto event."
                });
            }
        }

        // Preserve untouched fixed events cleanly (optional informational keep is not needed if untouched; let's emit keep for fixed future events to aid readability if none conflicted)
        foreach (var fixedEvt in fixedEvents.Where(e => e.Date >= refDate && !e.IsCompleted))
        {
            operations.Add(new ScheduledDayOperation
            {
                Date = fixedEvt.Date,
                Operation = "keep",
                EventId = fixedEvt.Id,
                Reason = string.Equals(trigger, "intraday_check", StringComparison.Ordinal) && fixedEvt.Date == refDate
                    ? "Frozen by intraday_check Same-Day Freeze."
                    : "Preserved sacred user_locked session."
            });
            if (string.Equals(fixedEvt.Ownership, "auto", StringComparison.Ordinal))
            {
                remainingAutoIds.Add(fixedEvt.Id);
            }
        }

        // Plan workouts chronologically across fixed uncompleted aerobic items + selectedUpsertDates
        var cleanCount = hasExtendedBreak
            ? cleanBaseSinceBreak
            : (prevHealthState is "SickHold" or "ReEntry" ? cleanRunsSinceCompass : 99);

        var orderedFixedFuture = fixedEvents
            .Where(e => !e.IsCompleted && string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Date)
            .Distinct()
            .ToHashSet();

        var timeline = orderedFixedFuture
            .Select(d => (Date: d, IsUpsert: false))
            .Concat(selectedUpsertDates.Select(d => (Date: d, IsUpsert: true)))
            .OrderBy(x => x.Date)
            .ToList();

        var qualityInWatch = false;
        var qualityInPreview = false;
        var longRunInWatch = false;
        var longRunInPreview = false;
        var watchCutoff = refDate.AddDays(6);

        var plannedWorkouts = new List<(DateOnly Date, WorkoutDefinition Workout)>();

        for (var idx = 0; idx < timeline.Count; idx++)
        {
            var (date, isUpsert) = timeline[idx];
            var isWatch = date <= watchCutoff;

            if (!isUpsert)
            {
                var fixedItem = fixedEvents.First(e => e.Date == date);
                var fixedIsQuality = ContainsAny(fixedItem.Name, ["interval", "tempo", "schwelle", "threshold", "speed"]);
                if (!fixedIsQuality)
                {
                    cleanCount++;
                }

                continue;
            }

            WorkoutDefinition workout;
            if (isDowngradeOverride && date == refDate)
            {
                workout = BuildRecovery30();
                cleanCount++;
            }
            else if (string.Equals(healthState, "ReEntry", StringComparison.Ordinal) && cleanCount < 2)
            {
                workout = BuildEasyBase45(cleanCount == 0 ? "Re-Entry Lockerer Einstieg (40m Z2)" : "Re-Entry Basis-Lauf (45m Z2)", cleanCount == 0 ? 1500 : 1800);
                cleanCount++;
            }
            else
            {
                var preferQualityNow = ((isWatch && !qualityInWatch) || (!isWatch && !qualityInPreview))
                    && (daysSinceLastQuality is null or >= 3 || !isWatch);

                if (preferQualityNow)
                {
                    workout = BuildThreshold4x5(date >= watchCutoff.AddDays(1) ? "Aufbau Schwellenintervalle (4x 5m Z4)" : "Kontrollierte Schwellenintervalle (4x 5m Z4)");

                    if (isWatch)
                    {
                        qualityInWatch = true;
                    }
                    else
                    {
                        qualityInPreview = true;
                    }
                }
                else
                {
                    var isLastOfWindow = !timeline.Skip(idx + 1).Any(t => t.IsUpsert && (isWatch ? t.Date <= watchCutoff : t.Date > watchCutoff));
                    var needLongRun = isWatch ? !longRunInWatch : !longRunInPreview;
                    if (isLastOfWindow && needLongRun)
                    {
                        workout = BuildLongRun(isWatch ? 60 : 65);
                        if (isWatch)
                        {
                            longRunInWatch = true;
                        }
                        else
                        {
                            longRunInPreview = true;
                        }
                    }
                    else
                    {
                        workout = BuildEasyBase45("Aerobe Basis & Z2 Dauerlauf (45m)", 1800);
                    }

                    cleanCount++;
                }
            }

            plannedWorkouts.Add((date, workout));
            operations.Add(new ScheduledDayOperation
            {
                Date = date,
                Operation = "upsert",
                Workout = workout,
                Reason = isDowngradeOverride && date == refDate
                    ? "Downgraded today's session via Telegram override."
                    : "Scheduled balanced stimulus within 14-day autopilot horizon."
            });
        }

        var observations = new List<string>
        {
            $"HealthState={healthState}, CleanBaseRunsSinceBreak={cleanBaseSinceBreak}, MissedAuto7d={missedAutoCount}."
        };
        if (lockedDates.Count > 0)
        {
            observations.Add($"Preserving user_locked dates: {string.Join(", ", lockedDates.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}.");
        }

        return new AutopilotPlanDecision
        {
            Trigger = trigger,
            ReferenceDate = refDate,
            HorizonDays = 14,
            HealthState = healthState,
            Operations = operations,
            Rationale = new CoachDecisionRationale
            {
                Observations = observations,
                TradeoffWeighed = "Balanced 14-day quality vs aerobic durability while respecting ownership and non-consecutive spacing",
                ProgressionIntent = string.Equals(healthState, "ReEntry", StringComparison.Ordinal)
                    ? "Complete 2 clean aerobic base runs before resuming threshold intensity"
                    : "Maintain alternating easy, threshold, and long-run stimuli across Days 1-14",
                RejectedAlternative = isRestOverride
                    ? "Keeping locked workout rejected because athlete explicitly requested rest"
                    : "Consecutive hard days rejected under alternating run-day cadence"
            },
            UpdatedCompass = new BlockCompassSnapshot
            {
                UpdatedAt = refDate,
                CurrentBlockFocus = string.Equals(healthState, "ReEntry", StringComparison.Ordinal)
                    ? "ReEntry aerobic consolidation"
                    : "14-day balanced base and threshold autopilot",
                BaseMilestone = "60-65m Z2 Long Run durability",
                QualityMilestone = "4x 5m Z4 Threshold",
                ObservedAthletePatterns = ["Alternating run days respected", "Z1/Z2 HR separation maintained"],
                LastDecisionSummary = $"Reconciled {plannedWorkouts.Count} upsert(s) under trigger '{trigger}' ({healthState}).",
                HealthState = healthState,
                AutoEventIds = remainingAutoIds
            }
        };
    }

    private static void SelectWindowDates(
        DateOnly refDate,
        int startOffset,
        int endOffset,
        int targetSessions,
        bool allowConsecutive,
        HashSet<DateOnly> fixedDates,
        HashSet<DateOnly> blockedDates,
        Dictionary<DateOnly, List<ParsedCalendarEvt>> mutableByDate,
        List<DateOnly> selected)
    {
        var windowStart = refDate.AddDays(startOffset);
        var windowEnd = refDate.AddDays(endOffset);
        var windowCount = fixedDates.Count(d => d >= windowStart && d <= windowEnd);

        bool CanUseDate(DateOnly candidate)
        {
            if (candidate < windowStart || candidate > windowEnd)
            {
                return false;
            }

            if (blockedDates.Contains(candidate) || selected.Contains(candidate))
            {
                return false;
            }

            if (!allowConsecutive && selected.Any(s => Math.Abs(s.DayNumber - candidate.DayNumber) <= 1))
            {
                return false;
            }

            return true;
        }

        // First prefer existing mutable calendar dates within this window if spacing allows
        foreach (var existingDate in mutableByDate.Keys.Where(d => d >= windowStart && d <= windowEnd).OrderBy(d => d))
        {
            if (windowCount >= targetSessions)
            {
                break;
            }

            if (CanUseDate(existingDate))
            {
                selected.Add(existingDate);
                windowCount++;
            }
        }

        // Fill remaining slots monotonically with non-consecutive spacing
        for (var offset = startOffset; offset <= endOffset && windowCount < targetSessions; offset++)
        {
            var candidate = refDate.AddDays(offset);
            if (CanUseDate(candidate))
            {
                selected.Add(candidate);
                windowCount++;
            }
        }
    }

    private static WorkoutDefinition BuildEasyBase45(string title, int mainSeconds) => new()
    {
        Title = title,
        SessionType = "easy_run",
        CoachingNotes = "Maintain relaxed cadence in Zone 2 HR and insert brief walk breaks if HR rises.",
        Steps =
        [
            new() { Kind = "warmup", DurationSeconds = 600, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } },
            new() { Kind = "steady", DurationSeconds = mainSeconds, Target = new() { Modality = "hr_zone", HrZoneStart = 2 } },
            new() { Kind = "cooldown", DurationSeconds = 300, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } }
        ]
    };

    private static WorkoutDefinition BuildRecovery30() => new()
    {
        Title = "Aktive Erholung (30m Z1)",
        SessionType = "recovery_run",
        CoachingNotes = "Very light recovery effort purely in Zone 1 HR.",
        Steps =
        [
            new() { Kind = "steady", DurationSeconds = 1800, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } }
        ]
    };

    private static WorkoutDefinition BuildLongRun(int totalMinutes) => new()
    {
        Title = string.Create(CultureInfo.InvariantCulture, $"Aerober Long Run ({totalMinutes}m Z2)"),
        SessionType = "long_run",
        CoachingNotes = "Steady aerobic anchor strictly controlled by Z2 HR.",
        Steps =
        [
            new() { Kind = "warmup", DurationSeconds = 600, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } },
            new() { Kind = "steady", DurationSeconds = (totalMinutes - 15) * 60, Target = new() { Modality = "hr_zone", HrZoneStart = 2 } },
            new() { Kind = "cooldown", DurationSeconds = 300, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } }
        ]
    };

    private static WorkoutDefinition BuildThreshold4x5(string title) => new()
    {
        Title = title,
        SessionType = "threshold_intervals",
        CoachingNotes = "Controlled threshold blocks in Z4 HR with full Z1 recoveries.",
        Steps =
        [
            new() { Kind = "warmup", DurationSeconds = 600, Target = new() { Modality = "hr_zone", HrZoneStart = 1, HrZoneEnd = 2 } },
            new()
            {
                Kind = "repeat",
                Reps = 4,
                Work = new() { DurationSeconds = 300, Target = new() { Modality = "hr_zone", HrZoneStart = 4 } },
                Recovery = new() { DurationSeconds = 120, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } }
            },
            new() { Kind = "cooldown", DurationSeconds = 600, Target = new() { Modality = "hr_zone", HrZoneStart = 1 } }
        ]
    };

    private static List<string> BuildSickHoldObservations(string? message, int redFlags, int clearedCount)
    {
        var obs = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Active medical red flags ({redFlags}) or illness symptoms triggered SickHold."),
            string.Create(CultureInfo.InvariantCulture, $"Cleared {clearedCount} open auto workout(s) from the 14-day calendar.")
        };
        if (!string.IsNullOrWhiteSpace(message))
        {
            obs.Add($"Athlete message noted illness: '{message}'.");
        }

        return obs;
    }

    private static string ExtractContextJson(string userPrompt)
    {
        const string marker = "ORIGINAL CONTEXT:";
        var idx = userPrompt.IndexOf(marker, StringComparison.Ordinal);
        var slice = idx >= 0 ? userPrompt[(idx + marker.Length)..] : userPrompt;
        return AutopilotPromptBuilder.ExtractJsonPayload(slice);
    }

    private static List<(DateOnly Date, bool IsCompromised, double? Z1Z2Pct)> ParseBaseChain(JsonElement? stimEl)
    {
        var list = new List<(DateOnly, bool, double?)>();
        if (!TryGetProp(stimEl, "BaseProgressionChain", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var date = TryGetDate(item, "Date");
            if (!date.HasValue)
            {
                continue;
            }

            var comp = GetBool(item, "IsCompromised") ?? false;
            var pct = GetDouble(item, "Z1Z2Percentage");
            list.Add((date.Value, comp, pct));
        }

        return list;
    }

    private static List<ParsedCalendarEvt> ParseUpcomingEvents(JsonElement? calEl)
    {
        var list = new List<ParsedCalendarEvt>();
        if (!TryGetProp(calEl, "UpcomingEventsNext14Days", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var id = GetLong(item, "Id") ?? 0;
            var date = TryGetDate(item, "Date");
            if (id <= 0 || !date.HasValue)
            {
                continue;
            }

            list.Add(new ParsedCalendarEvt(
                id,
                date.Value,
                GetString(item, "Category") ?? "WORKOUT",
                GetString(item, "Name") ?? "Workout",
                GetBool(item, "IsCompleted") ?? false,
                GetString(item, "Ownership") ?? "user_locked"));
        }

        return list;
    }

    private static HashSet<DateOnly> ParseDateSet(JsonElement? parent, string propertyName)
    {
        var set = new HashSet<DateOnly>();
        if (!TryGetProp(parent, propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return set;
        }

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(item.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                set.Add(d);
            }
        }

        return set;
    }

    private static bool TryGetProp(JsonElement? element, string name, out JsonElement value)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        return TryGetProp(element.Value, name, out value);
    }

    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty(name, out value))
            {
                return value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined;
            }

            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined;
                }
            }
        }

        value = default;
        return false;
    }

    private static JsonElement? GetObj(JsonElement? parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static JsonElement? GetObj(JsonElement parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? GetString(JsonElement parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement? parent, string name) =>
        TryGetProp(parent, name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;

    private static int? GetInt(JsonElement? parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? GetLong(JsonElement parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static double? GetDouble(JsonElement parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static DateOnly? TryGetDate(JsonElement parent, string name)
    {
        var text = GetString(parent, name);
        return text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static int GetArrayLength(JsonElement? parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    private static bool ContainsAny(string? text, string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ParsedCalendarEvt(
        long Id,
        DateOnly Date,
        string Category,
        string Name,
        bool IsCompleted,
        string Ownership);
}
