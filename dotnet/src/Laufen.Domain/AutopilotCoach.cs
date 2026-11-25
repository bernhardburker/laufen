using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laufen.Domain;

public sealed record LlmRequest(
    string SystemPrompt,
    string UserPrompt,
    string? JsonSchema = null);

public sealed record LlmResponse(
    bool Success,
    string Text,
    string? Error = null,
    string? Model = null,
    double ElapsedSeconds = 0.0);

public interface ILlmClient
{
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

public sealed record CoachPlanningContext
{
    public required CoachIntelligenceDossier Dossier { get; init; }
    public required string Trigger { get; init; }
    public string? AthleteMessage { get; init; }
    public int MaxValidationRetries { get; init; } = 1;
    public DateTimeOffset? TimestampUtc { get; init; }
}

public sealed record CoachPlanningAttempt
{
    public required int AttemptNumber { get; init; }
    public required bool LlmSucceeded { get; init; }
    public string? LlmError { get; init; }
    public required bool JsonParsed { get; init; }
    public string? ParseError { get; init; }
    public required IReadOnlyList<WorkoutValidationError> ValidationErrors { get; init; }
    public required double ElapsedSeconds { get; init; }
}

public sealed record AutopilotDecisionTraceEntry
{
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("athlete_id")]
    public required string AthleteId { get; init; }

    [JsonPropertyName("reference_date")]
    public required DateOnly ReferenceDate { get; init; }

    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("attempt_count")]
    public required int AttemptCount { get; init; }

    [JsonPropertyName("health_state")]
    public string? HealthState { get; init; }

    [JsonPropertyName("upsert_count")]
    public required int UpsertCount { get; init; }

    [JsonPropertyName("delete_count")]
    public required int DeleteCount { get; init; }

    [JsonPropertyName("watch_window_dates")]
    public required IReadOnlyList<DateOnly> WatchWindowDates { get; init; }

    [JsonPropertyName("preview_window_dates")]
    public required IReadOnlyList<DateOnly> PreviewWindowDates { get; init; }

    [JsonPropertyName("projected_ending_ctl")]
    public double? ProjectedEndingCtl { get; init; }

    [JsonPropertyName("projected_ending_atl")]
    public double? ProjectedEndingAtl { get; init; }

    [JsonPropertyName("projected_min_tsb")]
    public double? ProjectedMinTsb { get; init; }

    [JsonPropertyName("validation_errors")]
    public required IReadOnlyList<string> ValidationErrors { get; init; }

    [JsonPropertyName("rationale")]
    public CoachDecisionRationale? Rationale { get; init; }

    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; init; }
}

public sealed record CoachPlanningOutcome
{
    public required bool Success { get; init; }
    public string? FailureReason { get; init; }
    public string? ModelUsed { get; init; }
    public AutopilotPlanDecision? Decision { get; init; }
    public ReconciledAutopilotSchedule? ReconciledSchedule { get; init; }
    public required IReadOnlyList<CoachPlanningAttempt> Attempts { get; init; }
    public required AutopilotDecisionTraceEntry TraceEntry { get; init; }
}

public interface ICoachPlanningStrategy
{
    Task<CoachPlanningOutcome> PlanAsync(CoachPlanningContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pure prompt projector and JSON response extractor for the 14-day self-healing autopilot coach.
/// </summary>
public static class AutopilotPromptBuilder
{
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string BuildSystemPrompt() => """
        You are the authoritative 14-day Self-Healing Running Coach (RollingAutopilotCoach).
        Your job is to reconcile the athlete's next 14 days [reference_date .. reference_date + 13] based on physiological progression, readiness deltas, calendar ownership, and weather.

        STRICT COACHING DNA & SAFETY RULES (enforced by deterministic validator):
        1. HR vs. Pace Separation:
           - Session types "easy_run", "recovery_run", "long_run", AND ALL "warmup"/"cooldown" steps MUST ONLY use target modality "hr_zone" (<= Z2 HR; "recovery_run" steps must be Z1 HR only).
           - NEVER write or suggest any pace (like "06:45/km" or "7:00 min/km") in title, coaching_notes, or cue for aerobic/recovery runs.
           - Short intervals (work.duration_seconds < 120) MUST NOT use "hr_zone" (use "pace_range" or "rpe" instead due to cardiac lag).
           - NEVER combine HR, Pace, and RPE properties inside one target object.
           - Total duration per workout must be between 900s (15m) and 9000s (150m). Every step duration must be >= 10s.
        2. 14-Day Self-Healing & Ownership Rules:
           - Horizon is strictly 14 days: horizon_days must be 14; all operation dates in [reference_date .. reference_date + 13].
           - Plan ~3 sessions in Days 1-7 (Watch Commit Window) and ~3 sessions in Days 8-14 (Preview Window), with AT MOST ONE "upsert" per date. Clean up redundant/stale "auto" events (e.g. duplicate Slot 2A/2B pairs) using "delete" or replacing via "upsert".
           - NEVER modify or delete "completed" events.
           - NEVER modify or delete "user_locked" events or user_locked_dates unless trigger == "telegram_override".
           - NEVER modify or delete events on reference_date when trigger == "intraday_check".
           - If athlete.allow_consecutive_run_days is false, NEVER schedule back-to-back run days.
        3. Health & Re-Entry Rules:
           - If readiness.medical_red_flags is non-empty: health_state MUST be "SickHold" or "ReEntry" (never "Normal"), and DO NOT schedule quality workouts or "long_run" in Days 1-7.
           - If health_state == "SickHold": NO "upsert" operations allowed (only "delete" open "auto" workouts or "keep").
           - If stimulus_progression.break_reentry.has_recent_extended_break is true AND clean_base_runs_since_recent_break < 2: set health_state to "ReEntry" and ensure at least 2 cumulative clean post-break aerobic sessions ("easy_run"/"long_run"/"recovery_run") are completed/scheduled BEFORE the first quality workout ("intervals", "fartlek", "tempo_run", "threshold_intervals", "micro_threshold", "progressive_threshold").
        4. Weather & Progression:
           - On days where weather pace_interval_friendly is false, prefer HR/RPE targets or shift pace intervals to calmer days.
           - Respect data_quality_notes (e.g., stale wellness or historical HR zone boundary shifts) and use Run-Walk cues on Z2 runs where helpful.
           - Return ONLY one valid raw JSON object matching the exact schema below. Do NOT include extra keys or markdown prose outside the JSON.

        EXACT ALLOWED JSON SCHEMA (strict unmapped member check - NO extra properties!):
        {
          "trigger": "morning_sync | post_run | telegram_override | intraday_check",
          "reference_date": "YYYY-MM-DD",
          "horizon_days": 14,
          "health_state": "Normal | SickHold | ReEntry",
          "operations": [
            {
              "date": "YYYY-MM-DD",
              "operation": "keep | upsert | delete",
              "event_id": 123456,
              "reason": "short explanation",
              "workout": {
                "title": "Workout title",
                "session_type": "easy_run | recovery_run | long_run | intervals | fartlek | tempo_run | threshold_intervals | micro_threshold | progressive_threshold",
                "coaching_notes": "Optional coaching cue without pace numbers for aerobic runs",
                "steps": [
                  {
                    "kind": "warmup | steady | cooldown",
                    "duration_seconds": 600,
                    "target": { "modality": "hr_zone", "hr_zone_start": 1, "hr_zone_end": 2 },
                    "cue": "Optional short cue"
                  },
                  {
                    "kind": "repeat",
                    "reps": 4,
                    "work": {
                      "duration_seconds": 300,
                      "target": { "modality": "hr_zone", "hr_zone_start": 4 }
                    },
                    "recovery": {
                      "duration_seconds": 120,
                      "target": { "modality": "hr_zone", "hr_zone_start": 1 }
                    }
                  }
                ]
              }
            }
          ],
          "rationale": {
            "observations": ["Concrete telemetry facts observed"],
            "tradeoff_weighed": "Key tradeoff evaluated",
            "progression_intent": "How this 14d plan progresses base and quality stimuli",
            "rejected_alternative": "What was intentionally rejected and why"
          },
          "updated_compass": {
            "updated_at": "YYYY-MM-DD",
            "current_block_focus": "Short block objective",
            "base_milestone": "Current aerobic base progression target",
            "quality_milestone": "Current quality progression target",
            "observed_athlete_patterns": ["Concise durable pattern"],
            "last_decision_summary": "One-sentence summary of this plan update",
            "health_state": "Normal | SickHold | ReEntry",
            "auto_event_ids": [123456]
          }
        }
        Note: For "keep" or "delete" operations, omit "workout" (or set null). For "upsert", omit "event_id" (or set null).
        For "pace_range" target, use {"modality":"pace_range","pace_fast_sec_per_km":310,"pace_slow_sec_per_km":325}.
        For "rpe" target, use {"modality":"rpe","rpe":7}.
        """;

    public static string BuildUserPrompt(CoachPlanningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var d = context.Dossier;

        var payload = new
        {
            trigger = context.Trigger,
            reference_date = d.ReferenceDate,
            horizon_end_date = d.ReferenceDate.AddDays(13),
            athlete_message = context.AthleteMessage,
            athlete = d.Athlete,
            readiness = d.Readiness,
            stimulus_progression = d.StimulusProgression,
            calendar_14d = d.Calendar,
            weather = d.Weather,
            current_compass = d.CurrentCompass,
            data_quality_notes = d.DataQualityNotes
        };

        return JsonSerializer.Serialize(payload, PromptJsonOptions);
    }

    public static string BuildCorrectionPrompt(
        string originalUserPrompt,
        string previousOutput,
        IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(originalUserPrompt);
        ArgumentNullException.ThrowIfNull(errors);

        var truncatedPrev = string.IsNullOrWhiteSpace(previousOutput)
            ? "<empty>"
            : (previousOutput.Length > 4000 ? previousOutput[..4000] + "\n...[truncated]" : previousOutput);

        var sb = new StringBuilder();
        sb.AppendLine("Your previous AutopilotPlanDecision JSON failed deterministic validation or strict schema parsing.");
        sb.AppendLine("Fix ALL listed errors and return ONLY the corrected complete JSON object matching the exact schema.");
        sb.AppendLine();
        sb.AppendLine("ERRORS TO FIX:");
        foreach (var err in errors)
        {
            sb.Append("- ").AppendLine(err);
        }

        sb.AppendLine();
        sb.AppendLine("PREVIOUS INVALID OUTPUT:");
        sb.AppendLine(truncatedPrev);
        sb.AppendLine();
        sb.AppendLine("ORIGINAL CONTEXT:");
        sb.AppendLine(originalUserPrompt);
        return sb.ToString();
    }

    public static string ExtractJsonPayload(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var trimmed = rawText.Trim();
        var startIndex = trimmed.IndexOf('{');
        if (startIndex < 0)
        {
            return trimmed;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = startIndex; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return trimmed.Substring(startIndex, i - startIndex + 1);
                }
            }
        }

        return trimmed[startIndex..];
    }
}

/// <summary>
/// Rolling 14-day self-healing planning strategy invoking an <see cref="ILlmClient"/>
/// with deterministic <see cref="AutopilotScheduleGatekeeper"/> verification and bounded self-correction retries.
/// </summary>
public sealed class RollingAutopilotStrategy : ICoachPlanningStrategy
{
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true
    };

    private readonly ILlmClient _llmClient;

    public RollingAutopilotStrategy(ILlmClient llmClient)
    {
        ArgumentNullException.ThrowIfNull(llmClient);
        _llmClient = llmClient;
    }

    public async Task<CoachPlanningOutcome> PlanAsync(
        CoachPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var maxAttempts = 1 + Math.Max(0, context.MaxValidationRetries);
        var systemPrompt = AutopilotPromptBuilder.BuildSystemPrompt();
        var baseUserPrompt = AutopilotPromptBuilder.BuildUserPrompt(context);
        var currentUserPrompt = baseUserPrompt;

        var attempts = new List<CoachPlanningAttempt>(maxAttempts);
        var timestamp = context.TimestampUtc ?? DateTimeOffset.UtcNow;
        string? lastModel = null;

        for (var attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
        {
            var llmResult = await _llmClient.GenerateAsync(
                new LlmRequest(systemPrompt, currentUserPrompt),
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(llmResult.Model))
            {
                lastModel = llmResult.Model;
            }

            if (!llmResult.Success)
            {
                attempts.Add(new CoachPlanningAttempt
                {
                    AttemptNumber = attemptNumber,
                    LlmSucceeded = false,
                    LlmError = llmResult.Error ?? "LLM invocation failed.",
                    JsonParsed = false,
                    ParseError = null,
                    ValidationErrors = [],
                    ElapsedSeconds = llmResult.ElapsedSeconds
                });

                if (attemptNumber == maxAttempts)
                {
                    return BuildFailureOutcome(
                        context,
                        timestamp,
                        lastModel,
                        llmResult.Error ?? "LLM invocation failed.",
                        attempts);
                }

                currentUserPrompt = AutopilotPromptBuilder.BuildCorrectionPrompt(
                    baseUserPrompt,
                    llmResult.Text,
                    [llmResult.Error ?? "LLM generation failed."]);
                continue;
            }

            var extractedJson = AutopilotPromptBuilder.ExtractJsonPayload(llmResult.Text);
            AutopilotPlanDecision? decision;
            string? parseError = null;

            try
            {
                decision = JsonSerializer.Deserialize<AutopilotPlanDecision>(extractedJson, StrictJsonOptions);
                if (decision is null)
                {
                    parseError = "Deserialized AutopilotPlanDecision was null.";
                }
                else
                {
                    ValidateNonNullListItems(decision);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                decision = null;
                parseError = ex.Message;
            }

            if (decision is null)
            {
                var errList = new List<string> { $"[json_parse_error] $: {parseError}" };
                attempts.Add(new CoachPlanningAttempt
                {
                    AttemptNumber = attemptNumber,
                    LlmSucceeded = true,
                    LlmError = null,
                    JsonParsed = false,
                    ParseError = parseError,
                    ValidationErrors = [],
                    ElapsedSeconds = llmResult.ElapsedSeconds
                });

                if (attemptNumber == maxAttempts)
                {
                    return BuildFailureOutcome(
                        context,
                        timestamp,
                        lastModel,
                        $"JSON parse error: {parseError}",
                        attempts);
                }

                currentUserPrompt = AutopilotPromptBuilder.BuildCorrectionPrompt(baseUserPrompt, llmResult.Text, errList);
                continue;
            }

            var validationErrors = new List<WorkoutValidationError>();
            if (decision.ReferenceDate != context.Dossier.ReferenceDate)
            {
                validationErrors.Add(new WorkoutValidationError(
                    "reference_date_mismatch",
                    "reference_date",
                    $"Expected reference_date '{context.Dossier.ReferenceDate:yyyy-MM-dd}', received '{decision.ReferenceDate:yyyy-MM-dd}'."));
            }

            if (!string.Equals(decision.Trigger, context.Trigger, StringComparison.Ordinal))
            {
                validationErrors.Add(new WorkoutValidationError(
                    "trigger_mismatch",
                    "trigger",
                    $"Expected trigger '{context.Trigger}', received '{decision.Trigger}'."));
            }

            var gatekeeperResult = AutopilotScheduleGatekeeper.Validate(decision, context.Dossier);
            if (!gatekeeperResult.IsValid)
            {
                validationErrors.AddRange(gatekeeperResult.Errors);
            }

            attempts.Add(new CoachPlanningAttempt
            {
                AttemptNumber = attemptNumber,
                LlmSucceeded = true,
                LlmError = null,
                JsonParsed = true,
                ParseError = null,
                ValidationErrors = validationErrors,
                ElapsedSeconds = llmResult.ElapsedSeconds
            });

            if (validationErrors.Count == 0)
            {
                var reconciled = AutopilotScheduleGatekeeper.Reconcile(decision, context.Dossier);
                var trace = new AutopilotDecisionTraceEntry
                {
                    Timestamp = timestamp,
                    AthleteId = context.Dossier.Athlete.Id,
                    ReferenceDate = context.Dossier.ReferenceDate,
                    Trigger = context.Trigger,
                    Success = true,
                    Model = lastModel,
                    AttemptCount = attempts.Count,
                    HealthState = decision.HealthState,
                    UpsertCount = reconciled.CompiledUpserts.Count,
                    DeleteCount = reconciled.DeletedEventIds.Count,
                    WatchWindowDates = reconciled.WatchCommitWindow.Select(w => w.Date).Distinct().ToList(),
                    PreviewWindowDates = reconciled.PreviewWindow.Select(w => w.Date).Distinct().ToList(),
                    ProjectedEndingCtl = reconciled.BanisterProjection?.EndingCtl,
                    ProjectedEndingAtl = reconciled.BanisterProjection?.EndingAtl,
                    ProjectedMinTsb = reconciled.BanisterProjection?.MinProjectedTsb,
                    ValidationErrors = [],
                    Rationale = decision.Rationale,
                    FailureReason = null
                };

                return new CoachPlanningOutcome
                {
                    Success = true,
                    FailureReason = null,
                    ModelUsed = lastModel,
                    Decision = decision,
                    ReconciledSchedule = reconciled,
                    Attempts = attempts,
                    TraceEntry = trace
                };
            }

            if (attemptNumber == maxAttempts)
            {
                var joined = string.Join("; ", validationErrors.Select(e => $"[{e.Code}] {e.Field}: {e.Message}"));
                return BuildFailureOutcome(
                    context,
                    timestamp,
                    lastModel,
                    $"Gatekeeper rejected decision after {attempts.Count} attempt(s): {joined}",
                    attempts);
            }

            var errorDescriptions = validationErrors
                .Select(e => $"[{e.Code}] {e.Field}: {e.Message}")
                .ToList();
            currentUserPrompt = AutopilotPromptBuilder.BuildCorrectionPrompt(baseUserPrompt, llmResult.Text, errorDescriptions);
        }

        return BuildFailureOutcome(context, timestamp, lastModel, "Planning failed with zero attempts.", attempts);
    }

    private static void ValidateNonNullListItems(AutopilotPlanDecision decision)
    {
        if (decision.Operations is not null)
        {
            foreach (var op in decision.Operations)
            {
                if (op is null)
                {
                    throw new JsonException("Null entry inside 'operations' is not allowed.");
                }

                if (op.Workout?.Steps is not null)
                {
                    foreach (var step in op.Workout.Steps)
                    {
                        if (step is null)
                        {
                            throw new JsonException("Null entry inside 'workout.steps' is not allowed.");
                        }
                    }
                }
            }
        }

        if (decision.Rationale?.Observations is not null)
        {
            foreach (var obs in decision.Rationale.Observations)
            {
                if (obs is null)
                {
                    throw new JsonException("Null entry inside 'rationale.observations' is not allowed.");
                }
            }
        }

        if (decision.UpdatedCompass?.ObservedAthletePatterns is not null)
        {
            foreach (var pattern in decision.UpdatedCompass.ObservedAthletePatterns)
            {
                if (pattern is null)
                {
                    throw new JsonException("Null entry inside 'updated_compass.observed_athlete_patterns' is not allowed.");
                }
            }
        }
    }

    private static CoachPlanningOutcome BuildFailureOutcome(
        CoachPlanningContext context,
        DateTimeOffset timestamp,
        string? model,
        string failureReason,
        List<CoachPlanningAttempt> attempts)
    {
        var lastErrors = attempts.Count > 0
            ? attempts[^1].ValidationErrors.Select(e => $"[{e.Code}] {e.Field}: {e.Message}").ToList()
            : new List<string> { failureReason };

        var trace = new AutopilotDecisionTraceEntry
        {
            Timestamp = timestamp,
            AthleteId = context.Dossier.Athlete.Id,
            ReferenceDate = context.Dossier.ReferenceDate,
            Trigger = context.Trigger,
            Success = false,
            Model = model,
            AttemptCount = attempts.Count,
            HealthState = null,
            UpsertCount = 0,
            DeleteCount = 0,
            WatchWindowDates = [],
            PreviewWindowDates = [],
            ProjectedEndingCtl = null,
            ProjectedEndingAtl = null,
            ProjectedMinTsb = null,
            ValidationErrors = lastErrors,
            Rationale = null,
            FailureReason = failureReason
        };

        return new CoachPlanningOutcome
        {
            Success = false,
            FailureReason = failureReason,
            ModelUsed = model,
            Decision = null,
            ReconciledSchedule = null,
            Attempts = attempts,
            TraceEntry = trace
        };
    }
}
