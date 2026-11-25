using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Laufen.Domain;

/// <summary>
/// Domain-typed insight item emitted by <see cref="IProgressNarrator"/> without UI CSS classes.
/// </summary>
public sealed record NarrativeInsight
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("badge")]
    public required string Badge { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// Editorial progress commentary and domain-typed insights produced off the critical scheduling path.
/// </summary>
public sealed record NarrativeReport
{
    [JsonPropertyName("reference_date")]
    public required DateOnly ReferenceDate { get; init; }

    [JsonPropertyName("trigger_reason")]
    public required string TriggerReason { get; init; }

    [JsonPropertyName("status_title")]
    public required string StatusTitle { get; init; }

    [JsonPropertyName("status_badge")]
    public required string StatusBadge { get; init; }

    [JsonPropertyName("status_severity")]
    public required string StatusSeverity { get; init; }

    [JsonPropertyName("commentary_markdown")]
    public required string CommentaryMarkdown { get; init; }

    [JsonPropertyName("insights")]
    public required IReadOnlyList<NarrativeInsight> Insights { get; init; }

    [JsonPropertyName("schedule_highlights")]
    public required IReadOnlyList<string> ScheduleHighlights { get; init; }
}

public sealed record NarrativeTriggerEvaluation(
    bool ShouldNarrate,
    string TriggerReason,
    IReadOnlyList<string> Reasons);

public sealed record NarrativeCompletedRunSummary
{
    public required string ActivityId { get; init; }
    public required DateOnly Date { get; init; }
    public required string Name { get; init; }
    public required double DurationMinutes { get; init; }
    public required double DistanceKm { get; init; }
    public double? AverageHeartRate { get; init; }
    public double? Z1Z2Percentage { get; init; }
    public double? AerobicEfficiencyFactor { get; init; }
    public string? ComplianceStatus { get; init; }
    public string? ComplianceSummary { get; init; }
    public string? AthleteNote { get; init; }
}

public sealed record NarrativeScheduledSession
{
    public required DateOnly Date { get; init; }
    public required string Tier { get; init; }
    public required string Ownership { get; init; }
    public required string Title { get; init; }
    public string? SessionType { get; init; }
    public int? DurationMinutes { get; init; }
}

public sealed record Zone2GapTrendSummary
{
    public required int SampleCount { get; init; }
    public required string EarlyAveragePaceText { get; init; }
    public required string RecentAveragePaceText { get; init; }
    public required double DeltaSecondsPerKm { get; init; }
}

/// <summary>
/// Curated briefing dossier passed to <see cref="IProgressNarrator"/> (Role 2).
/// </summary>
public sealed record NarrativeBriefingDossier
{
    public required DateOnly ReferenceDate { get; init; }
    public required string TriggerReason { get; init; }
    public required AthleteDossierSummary Athlete { get; init; }
    public NarrativeCompletedRunSummary? LatestCompletedRun { get; init; }
    public required IReadOnlyList<WeeklyTrendSummary> MacroTrends4Weeks { get; init; }
    public Zone2GapTrendSummary? Zone2GapTrend { get; init; }
    public BlockCompassSnapshot? CurrentCompass { get; init; }
    public string? PreviousHealthState { get; init; }
    public required IReadOnlyList<NarrativeScheduledSession> ReconciledScheduleSummary { get; init; }
    public CoachDecisionRationale? LatestTraceRationale { get; init; }
    public required IReadOnlyList<string> DataQualityNotes { get; init; }
}

public sealed record NarrativeGenerationContext
{
    public required NarrativeBriefingDossier Briefing { get; init; }
    public int MaxValidationRetries { get; init; } = 1;
    public DateTimeOffset? TimestampUtc { get; init; }
}

public sealed record NarrativeGenerationAttempt
{
    public required int AttemptNumber { get; init; }
    public required bool LlmSucceeded { get; init; }
    public string? LlmError { get; init; }
    public required bool JsonParsed { get; init; }
    public string? ParseError { get; init; }
    public required IReadOnlyList<WorkoutValidationError> ValidationErrors { get; init; }
    public required double ElapsedSeconds { get; init; }
}

public sealed record NarrativeGenerationOutcome
{
    public required bool Success { get; init; }
    public string? FailureReason { get; init; }
    public string? ModelUsed { get; init; }
    public NarrativeReport? Report { get; init; }
    public required IReadOnlyList<NarrativeGenerationAttempt> Attempts { get; init; }
}

public interface IProgressNarrator
{
    Task<NarrativeGenerationOutcome> NarrateAsync(
        NarrativeGenerationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Event-driven trigger gatekeeper ensuring <see cref="IProgressNarrator"/> fires only on
/// completed runs, major health/block transitions, or explicit manual requests.
/// </summary>
public static class ProgressNarratorGate
{
    public static NarrativeTriggerEvaluation Evaluate(
        string trigger,
        string? previousHealthState,
        string? currentHealthState,
        string? previousBlockFocus,
        string? currentBlockFocus,
        bool forceManual = false)
    {
        var reasons = new List<string>();
        var isPostRun = string.Equals(trigger, "post_run", StringComparison.OrdinalIgnoreCase);

        if (isPostRun)
        {
            reasons.Add("completed_run");
        }

        var prevHealth = string.IsNullOrWhiteSpace(previousHealthState) ? null : previousHealthState.Trim();
        var currHealth = string.IsNullOrWhiteSpace(currentHealthState) ? null : currentHealthState.Trim();
        var isHealthShift = false;
        if (currHealth is not null)
        {
            if (prevHealth is not null)
            {
                isHealthShift = !string.Equals(prevHealth, currHealth, StringComparison.OrdinalIgnoreCase);
            }
            else if (!string.Equals(currHealth, "Normal", StringComparison.OrdinalIgnoreCase))
            {
                isHealthShift = true;
            }
        }

        if (isHealthShift)
        {
            reasons.Add($"health_state_shift:{prevHealth ?? "unset"}->{currHealth}");
        }

        var prevFocus = string.IsNullOrWhiteSpace(previousBlockFocus) ? null : previousBlockFocus.Trim();
        var currFocus = string.IsNullOrWhiteSpace(currentBlockFocus) ? null : currentBlockFocus.Trim();
        var isBlockFocusShift = prevFocus is not null
            && currFocus is not null
            && !string.Equals(prevFocus, currFocus, StringComparison.OrdinalIgnoreCase);

        if (isBlockFocusShift)
        {
            reasons.Add("block_focus_shift");
        }

        if (forceManual)
        {
            reasons.Add("manual_force");
        }

        if (reasons.Count == 0)
        {
            return new NarrativeTriggerEvaluation(false, "suppressed", []);
        }

        var triggerReason = isPostRun
            ? "post_run"
            : isHealthShift
                ? "health_state_shift"
                : isBlockFocusShift
                    ? "block_focus_shift"
                    : "manual";

        return new NarrativeTriggerEvaluation(true, triggerReason, reasons);
    }
}

/// <summary>
/// Pure builder assembling a read-only <see cref="NarrativeBriefingDossier"/> under strict <c>&lt;= refDate</c> time-travel isolation.
/// </summary>
public static partial class NarrativeBriefingDossierBuilder
{
    [GeneratedRegex(@"^(?<min>\d{1,2}):(?<sec>\d{2})(?:\s*/km)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PaceTextPattern();

    public static NarrativeBriefingDossier Build(
        AthleteProfile profile,
        IReadOnlyList<IntervalsActivityEntry> activities,
        IReadOnlyList<IntervalsWellnessEntry>? wellness,
        IReadOnlyDictionary<string, RunReviewRecord>? runReviews,
        IReadOnlyList<CalendarEventEntry>? calendarEvents,
        DateOnly refDate,
        string triggerReason,
        CoachIntelligenceDossier? existingDossier = null,
        ReconciledAutopilotSchedule? reconciledSchedule = null,
        BlockCompassSnapshot? currentCompass = null,
        string? previousHealthState = null,
        CoachDecisionRationale? latestTraceRationale = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerReason);

        var dossier = existingDossier ?? CoachIntelligenceDossierBuilder.Build(
            profile,
            activities,
            wellness,
            runReviews,
            calendarEvents,
            refDate,
            refDate,
            currentCompass: currentCompass);

        var activitiesUpToRef = activities
            .Where(a => DateOnly.FromDateTime(a.StartDateLocal) <= refDate)
            .ToList();

        var validRunsDesc = PhysiologicalCalculator.FilterValidRuns(activitiesUpToRef)
            .OrderByDescending(a => a.StartDateLocal)
            .ToList();

        var macroTrends4Weeks = PhysiologicalCalculator.AggregateWeeklyTrends(activitiesUpToRef, maxWeeks: 4);
        var latestRunSummary = BuildLatestRunSummary(validRunsDesc.FirstOrDefault(), runReviews, refDate);
        var z2GapTrend = BuildZone2GapTrend(dossier.StimulusProgression.BaseProgressionChain);
        var scheduleSummary = BuildScheduleSummary(dossier, reconciledSchedule, refDate);

        return new NarrativeBriefingDossier
        {
            ReferenceDate = refDate,
            TriggerReason = triggerReason,
            Athlete = dossier.Athlete,
            LatestCompletedRun = latestRunSummary,
            MacroTrends4Weeks = macroTrends4Weeks,
            Zone2GapTrend = z2GapTrend,
            CurrentCompass = currentCompass ?? dossier.CurrentCompass,
            PreviousHealthState = previousHealthState,
            ReconciledScheduleSummary = scheduleSummary,
            LatestTraceRationale = latestTraceRationale,
            DataQualityNotes = dossier.DataQualityNotes
        };
    }

    private static NarrativeCompletedRunSummary? BuildLatestRunSummary(
        IntervalsActivityEntry? latestRun,
        IReadOnlyDictionary<string, RunReviewRecord>? runReviews,
        DateOnly refDate)
    {
        if (latestRun is null)
        {
            return null;
        }

        RunReviewRecord? review = null;
        if (runReviews is not null
            && runReviews.TryGetValue(latestRun.Id, out var foundReview)
            && DateOnly.FromDateTime(foundReview.Date) <= refDate)
        {
            review = foundReview;
        }

        var metrics = PhysiologicalCalculator.CalculateRunMetrics(latestRun);
        var durationMinutes = Math.Round((latestRun.MovingTime ?? 0) / 60.0, 1, MidpointRounding.ToEven);
        var complianceStatus = review?.Review.Stages.Stage2Compliance.StatusLabel
            ?? review?.Review.Stages.Stage2Compliance.Status;
        var complianceSummary = review?.Review.Stages.Stage2Compliance.TargetComparison
            ?? review?.Review.Summary;
        var note = !string.IsNullOrWhiteSpace(latestRun.Description)
            ? latestRun.Description.Trim()
            : (string.IsNullOrWhiteSpace(review?.AthleteNote) ? null : review.AthleteNote.Trim());

        return new NarrativeCompletedRunSummary
        {
            ActivityId = latestRun.Id,
            Date = DateOnly.FromDateTime(latestRun.StartDateLocal),
            Name = string.IsNullOrWhiteSpace(latestRun.Name) ? "Run" : latestRun.Name.Trim(),
            DurationMinutes = durationMinutes,
            DistanceKm = metrics.DistanceKm,
            AverageHeartRate = latestRun.AverageHeartrate.HasValue
                ? Math.Round((double)latestRun.AverageHeartrate.Value, 1, MidpointRounding.ToEven)
                : null,
            Z1Z2Percentage = metrics.Z1Z2Percentage ?? review?.Z1Z2Percentage,
            AerobicEfficiencyFactor = metrics.AerobicEfficiencyFactor ?? review?.AerobicEf,
            ComplianceStatus = complianceStatus,
            ComplianceSummary = complianceSummary,
            AthleteNote = note
        };
    }

    private static Zone2GapTrendSummary? BuildZone2GapTrend(IReadOnlyList<BaseSessionSummary> baseChain)
    {
        var pacePoints = new List<double>();
        foreach (var item in baseChain)
        {
            if (item.IsCompromised || item.Z2ValidForTrend != true)
            {
                continue;
            }

            if (TryParsePaceSeconds(item.Z2GapPaceText, out var seconds))
            {
                pacePoints.Add(seconds);
            }
        }

        if (pacePoints.Count == 0)
        {
            return null;
        }

        if (pacePoints.Count == 1)
        {
            var singlePace = FormatPace(pacePoints[0]);
            return new Zone2GapTrendSummary
            {
                SampleCount = 1,
                EarlyAveragePaceText = singlePace,
                RecentAveragePaceText = singlePace,
                DeltaSecondsPerKm = 0.0
            };
        }

        var k = Math.Min(3, Math.Max(1, pacePoints.Count / 3));
        var earlyAvg = pacePoints.Take(k).Average();
        var recentAvg = pacePoints.Skip(pacePoints.Count - k).Average();
        var delta = Math.Round(recentAvg - earlyAvg, 1, MidpointRounding.ToEven);

        return new Zone2GapTrendSummary
        {
            SampleCount = pacePoints.Count,
            EarlyAveragePaceText = FormatPace(earlyAvg),
            RecentAveragePaceText = FormatPace(recentAvg),
            DeltaSecondsPerKm = delta
        };
    }

    private static List<NarrativeScheduledSession> BuildScheduleSummary(
        CoachIntelligenceDossier dossier,
        ReconciledAutopilotSchedule? reconciledSchedule,
        DateOnly refDate)
    {
        if (reconciledSchedule is not null)
        {
            return reconciledSchedule.WatchCommitWindow
                .Concat(reconciledSchedule.PreviewWindow)
                .OrderBy(w => w.Date)
                .Select(w => new NarrativeScheduledSession
                {
                    Date = w.Date,
                    Tier = w.Tier,
                    Ownership = w.Ownership,
                    Title = w.Title,
                    SessionType = w.SessionType,
                    DurationMinutes = w.TotalDurationSeconds.HasValue
                        ? (int)Math.Round(w.TotalDurationSeconds.Value / 60.0, MidpointRounding.AwayFromZero)
                        : null
                })
                .ToList();
        }

        var watchCutoff = refDate.AddDays(6);
        return dossier.Calendar.UpcomingEventsNext14Days
            .Where(e => string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Date)
            .Select(e => new NarrativeScheduledSession
            {
                Date = e.Date,
                Tier = e.Date <= watchCutoff ? "watch_commit" : "preview",
                Ownership = e.Ownership,
                Title = e.Name,
                SessionType = null,
                DurationMinutes = e.DurationSeconds.HasValue
                    ? (int)Math.Round(e.DurationSeconds.Value / 60.0, MidpointRounding.AwayFromZero)
                    : null
            })
            .ToList();
    }

    private static bool TryParsePaceSeconds(string? paceText, out double secondsPerKm)
    {
        if (string.IsNullOrWhiteSpace(paceText))
        {
            secondsPerKm = 0.0;
            return false;
        }

        var match = PaceTextPattern().Match(paceText.Trim());
        if (!match.Success)
        {
            secondsPerKm = 0.0;
            return false;
        }

        var mins = int.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture);
        var secs = int.Parse(match.Groups["sec"].Value, CultureInfo.InvariantCulture);
        secondsPerKm = (mins * 60) + secs;
        return secondsPerKm > 0;
    }

    private static string FormatPace(double secondsPerKm)
    {
        var rounded = (int)Math.Round(secondsPerKm, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{rounded / 60:D2}:{rounded % 60:D2}/km");
    }
}

/// <summary>
/// Validates <see cref="NarrativeReport"/> outputs against domain-typed severity rules,
/// CSS leakage prevention, and Coaching DNA non-prescriptive aerobic pace boundaries.
/// </summary>
public static partial class ProgressNarratorValidator
{
    private const int MinInsights = 1;
    private const int MaxInsights = 6;

    private static readonly HashSet<string> AllowedSeverities = new(
        ["info", "success", "warning"],
        StringComparer.Ordinal);

    [GeneratedRegex(@"\btag-(?:amber|green|cyan|red|blue|gray)\b|class\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssClassLeakPattern();

    [GeneratedRegex(@"(?:Ziel-?Pace|Soll-?Pace|peile|halte\s+(?:eine\s+)?Pace|laufe\s+mit|target\s+pace|pace\s+target)[^.\n]{0,30}\d{1,2}:\d{2}\s*(?:/km|min/km)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrescriptiveAerobicPacePattern();

    public static WorkoutValidationResult Validate(NarrativeReport report, NarrativeBriefingDossier briefing)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(briefing);

        var errors = new List<WorkoutValidationError>();

        if (report.ReferenceDate != briefing.ReferenceDate)
        {
            errors.Add(new WorkoutValidationError(
                "reference_date_mismatch",
                "reference_date",
                $"Expected reference_date '{briefing.ReferenceDate:yyyy-MM-dd}', received '{report.ReferenceDate:yyyy-MM-dd}'."));
        }

        if (string.IsNullOrWhiteSpace(report.TriggerReason))
        {
            errors.Add(new WorkoutValidationError("missing_trigger_reason", "trigger_reason", "trigger_reason must be non-empty."));
        }

        if (string.IsNullOrWhiteSpace(report.StatusTitle))
        {
            errors.Add(new WorkoutValidationError("missing_status_title", "status_title", "status_title must be non-empty."));
        }

        if (string.IsNullOrWhiteSpace(report.StatusBadge))
        {
            errors.Add(new WorkoutValidationError("missing_status_badge", "status_badge", "status_badge must be non-empty."));
        }

        if (!AllowedSeverities.Contains(report.StatusSeverity))
        {
            errors.Add(new WorkoutValidationError(
                "invalid_status_severity",
                "status_severity",
                $"status_severity '{report.StatusSeverity}' is invalid. Expected info, success, or warning."));
        }

        if (string.IsNullOrWhiteSpace(report.CommentaryMarkdown))
        {
            errors.Add(new WorkoutValidationError("missing_commentary", "commentary_markdown", "commentary_markdown must be non-empty."));
        }

        if (report.Insights is null || report.Insights.Count < MinInsights || report.Insights.Count > MaxInsights)
        {
            errors.Add(new WorkoutValidationError(
                "invalid_insights_count",
                "insights",
                $"insights must contain between {MinInsights} and {MaxInsights} items."));
        }
        else
        {
            for (var i = 0; i < report.Insights.Count; i++)
            {
                var insight = report.Insights[i];
                var prefix = string.Create(CultureInfo.InvariantCulture, $"insights[{i}]");

                if (string.IsNullOrWhiteSpace(insight.Title))
                {
                    errors.Add(new WorkoutValidationError("missing_insight_field", $"{prefix}.title", "Insight title must be non-empty."));
                }

                if (string.IsNullOrWhiteSpace(insight.Badge))
                {
                    errors.Add(new WorkoutValidationError("missing_insight_field", $"{prefix}.badge", "Insight badge must be non-empty."));
                }

                if (string.IsNullOrWhiteSpace(insight.Text))
                {
                    errors.Add(new WorkoutValidationError("missing_insight_field", $"{prefix}.text", "Insight text must be non-empty."));
                }

                if (!AllowedSeverities.Contains(insight.Severity))
                {
                    errors.Add(new WorkoutValidationError(
                        "invalid_insight_severity",
                        $"{prefix}.severity",
                        $"Insight severity '{insight.Severity}' is invalid. Expected info, success, or warning."));
                }

                CheckCssLeak(insight.Title, $"{prefix}.title", errors);
                CheckCssLeak(insight.Badge, $"{prefix}.badge", errors);
                CheckCssLeak(insight.Text, $"{prefix}.text", errors);
                CheckPrescriptivePaceLeak(insight.Text, $"{prefix}.text", errors);
            }
        }

        if (report.ScheduleHighlights is null || report.ScheduleHighlights.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add(new WorkoutValidationError(
                "invalid_schedule_highlights",
                "schedule_highlights",
                "schedule_highlights must not be null or contain empty items."));
        }
        else
        {
            for (var i = 0; i < report.ScheduleHighlights.Count; i++)
            {
                var field = string.Create(CultureInfo.InvariantCulture, $"schedule_highlights[{i}]");
                CheckCssLeak(report.ScheduleHighlights[i], field, errors);
                CheckPrescriptivePaceLeak(report.ScheduleHighlights[i], field, errors);
            }
        }

        CheckCssLeak(report.StatusTitle, "status_title", errors);
        CheckCssLeak(report.StatusBadge, "status_badge", errors);
        CheckCssLeak(report.CommentaryMarkdown, "commentary_markdown", errors);
        CheckPrescriptivePaceLeak(report.CommentaryMarkdown, "commentary_markdown", errors);

        return new WorkoutValidationResult(errors.Count == 0, errors);
    }

    private static void CheckCssLeak(string? text, string field, List<WorkoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var match = CssClassLeakPattern().Match(text);
        if (match.Success)
        {
            errors.Add(new WorkoutValidationError(
                "css_class_leak",
                field,
                $"Narrative text must not emit UI CSS classes ('{match.Value}'). Use semantic severity instead."));
        }
    }

    private static void CheckPrescriptivePaceLeak(string? text, string field, List<WorkoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var match = PrescriptiveAerobicPacePattern().Match(text);
        if (match.Success)
        {
            errors.Add(new WorkoutValidationError(
                "prescriptive_aerobic_pace_leak",
                field,
                $"Narrative commentary must not prescribe target paces ('{match.Value}'). Report historical GAP pace as factual outcome only."));
        }
    }
}

/// <summary>
/// Pure prompt projector for Role 2 (<see cref="IProgressNarrator"/>).
/// </summary>
public static class ProgressNarratorPromptBuilder
{
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string BuildSystemPrompt() => """
        You are the Decoupled Editorial & Macro Progress Narrator (Role 2: ProgressNarrator).
        Your responsibility is strictly commentary and physiological trend synthesis in German—you NEVER schedule, override, or modify workouts.

        STRICT RULES:
        1. Separation of Authority:
           - You are read-only over the reconciled 14-day schedule and BlockCompass. Summarize the active plan rather than inventing a competing workout.
        2. Coaching DNA & Non-Judgmental Tone:
           - Write clear, encouraging, data-grounded German coaching prose (du-form).
           - NEVER prescribe or target paces for easy, recovery, or long runs (never write "Zielpace 07:00/km" or "laufe mit 06:45/km"). You MAY report observed historical Zone 2 GAP pace purely as factual outcome telemetry (e.g., "Z2-GAP-Pace liegt bei 07:58/km").
           - Emphasize HR discipline and proactive brisk walking breaks (Gehpausen / Run-Walk) when HR drifts toward Z2 max.
           - Respect data_quality_notes (e.g., cold-start zero-activity athletes, stale wellness, or historical HR zone boundary shifts).
        3. Domain-Typed Output Only (ZERO CSS classes):
           - Use semantic severities "info" | "success" | "warning" ONLY. Never output CSS class names like "tag-amber", "tag-green", "tag-cyan", or HTML attributes.
           - Include 1 to 6 concise insights and crisp schedule_highlights matching reconciled_schedule_summary.
           - Return ONLY one valid raw JSON object matching the exact schema below.

        EXACT ALLOWED JSON SCHEMA (strict unmapped member check - NO extra properties!):
        {
          "reference_date": "YYYY-MM-DD",
          "trigger_reason": "post_run | health_state_shift | block_focus_shift | manual",
          "status_title": "Concise German headline",
          "status_badge": "Short 2-4 word status badge",
          "status_severity": "info | success | warning",
          "commentary_markdown": "Structured German Markdown commentary with ### headings",
          "insights": [
            {
              "title": "Insight headline",
              "severity": "info | success | warning",
              "badge": "Short telemetry badge",
              "text": "Concise German insight explanation"
            }
          ],
          "schedule_highlights": [
            "YYYY-MM-DD: Short summary of planned session from reconciled_schedule_summary"
          ]
        }
        """;

    public static string BuildUserPrompt(NarrativeBriefingDossier briefing)
    {
        ArgumentNullException.ThrowIfNull(briefing);

        var payload = new
        {
            reference_date = briefing.ReferenceDate,
            trigger_reason = briefing.TriggerReason,
            athlete = briefing.Athlete,
            latest_completed_run = briefing.LatestCompletedRun,
            macro_trends_4_weeks = briefing.MacroTrends4Weeks,
            zone2_gap_trend = briefing.Zone2GapTrend,
            current_compass = briefing.CurrentCompass,
            previous_health_state = briefing.PreviousHealthState,
            reconciled_schedule_summary = briefing.ReconciledScheduleSummary,
            latest_trace_rationale = briefing.LatestTraceRationale,
            data_quality_notes = briefing.DataQualityNotes
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
        sb.AppendLine("Your previous NarrativeReport JSON failed deterministic validation or strict schema parsing.");
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
}

/// <summary>
/// LLM-backed Role 2 <see cref="IProgressNarrator"/> with strict JSON validation and bounded self-correction retries.
/// </summary>
public sealed class LlmProgressNarrator : IProgressNarrator
{
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true
    };

    private readonly ILlmClient _llmClient;

    public LlmProgressNarrator(ILlmClient llmClient)
    {
        ArgumentNullException.ThrowIfNull(llmClient);
        _llmClient = llmClient;
    }

    public async Task<NarrativeGenerationOutcome> NarrateAsync(
        NarrativeGenerationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var maxAttempts = 1 + Math.Max(0, context.MaxValidationRetries);
        var systemPrompt = ProgressNarratorPromptBuilder.BuildSystemPrompt();
        var baseUserPrompt = ProgressNarratorPromptBuilder.BuildUserPrompt(context.Briefing);
        var currentUserPrompt = baseUserPrompt;

        var attempts = new List<NarrativeGenerationAttempt>(maxAttempts);
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
                attempts.Add(new NarrativeGenerationAttempt
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
                    return new NarrativeGenerationOutcome
                    {
                        Success = false,
                        FailureReason = llmResult.Error ?? "LLM invocation failed.",
                        ModelUsed = lastModel,
                        Report = null,
                        Attempts = attempts
                    };
                }

                currentUserPrompt = ProgressNarratorPromptBuilder.BuildCorrectionPrompt(
                    baseUserPrompt,
                    llmResult.Text,
                    [llmResult.Error ?? "LLM generation failed."]);
                continue;
            }

            var extractedJson = AutopilotPromptBuilder.ExtractJsonPayload(llmResult.Text);
            NarrativeReport? report;
            string? parseError = null;

            try
            {
                report = JsonSerializer.Deserialize<NarrativeReport>(extractedJson, StrictJsonOptions);
                if (report is null)
                {
                    parseError = "Deserialized NarrativeReport was null.";
                }
                else
                {
                    ValidateNonNullListItems(report);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                report = null;
                parseError = ex.Message;
            }

            if (report is null)
            {
                var errList = new List<string> { $"[json_parse_error] $: {parseError}" };
                attempts.Add(new NarrativeGenerationAttempt
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
                    return new NarrativeGenerationOutcome
                    {
                        Success = false,
                        FailureReason = $"JSON parse error: {parseError}",
                        ModelUsed = lastModel,
                        Report = null,
                        Attempts = attempts
                    };
                }

                currentUserPrompt = ProgressNarratorPromptBuilder.BuildCorrectionPrompt(
                    baseUserPrompt,
                    llmResult.Text,
                    errList);
                continue;
            }

            var validation = ProgressNarratorValidator.Validate(report, context.Briefing);
            attempts.Add(new NarrativeGenerationAttempt
            {
                AttemptNumber = attemptNumber,
                LlmSucceeded = true,
                LlmError = null,
                JsonParsed = true,
                ParseError = null,
                ValidationErrors = validation.Errors,
                ElapsedSeconds = llmResult.ElapsedSeconds
            });

            if (validation.IsValid)
            {
                return new NarrativeGenerationOutcome
                {
                    Success = true,
                    FailureReason = null,
                    ModelUsed = lastModel,
                    Report = report,
                    Attempts = attempts
                };
            }

            if (attemptNumber == maxAttempts)
            {
                var joined = string.Join("; ", validation.Errors.Select(e => $"[{e.Code}] {e.Field}: {e.Message}"));
                return new NarrativeGenerationOutcome
                {
                    Success = false,
                    FailureReason = $"Narrative validator rejected output after {attempts.Count} attempt(s): {joined}",
                    ModelUsed = lastModel,
                    Report = null,
                    Attempts = attempts
                };
            }

            var errorDescriptions = validation.Errors
                .Select(e => $"[{e.Code}] {e.Field}: {e.Message}")
                .ToList();
            currentUserPrompt = ProgressNarratorPromptBuilder.BuildCorrectionPrompt(
                baseUserPrompt,
                llmResult.Text,
                errorDescriptions);
        }

        return new NarrativeGenerationOutcome
        {
            Success = false,
            FailureReason = "Narrative generation failed with zero attempts.",
            ModelUsed = lastModel,
            Report = null,
            Attempts = attempts
        };
    }

    private static void ValidateNonNullListItems(NarrativeReport report)
    {
        if (report.Insights is not null)
        {
            foreach (var insight in report.Insights)
            {
                if (insight is null)
                {
                    throw new JsonException("Null entry inside 'insights' is not allowed.");
                }
            }
        }

        if (report.ScheduleHighlights is not null)
        {
            foreach (var item in report.ScheduleHighlights)
            {
                if (item is null)
                {
                    throw new JsonException("Null entry inside 'schedule_highlights' is not allowed.");
                }
            }
        }
    }
}
