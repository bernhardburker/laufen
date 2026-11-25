using System.Globalization;
using System.Text;

namespace Laufen.Domain;

public sealed record CompiledWorkout(
    string Title,
    string SessionType,
    int TotalDurationSeconds,
    string DslText);

/// <summary>
/// Deterministically compiles validated <see cref="WorkoutDefinition"/> ASTs into Intervals.icu step DSL.
/// </summary>
public static class IntervalsWorkoutCompiler
{
    public static CompiledWorkout Compile(WorkoutDefinition workout)
    {
        ArgumentNullException.ThrowIfNull(workout);

        var validation = WorkoutModalityValidator.ValidateWorkout(workout);
        if (!validation.IsValid)
        {
            var codes = string.Join(", ", validation.Errors.Select(e => $"{e.Code} ({e.Field})"));
            throw new InvalidOperationException($"Cannot compile invalid workout '{workout.Title}': {codes}");
        }

        var sections = new List<string>();
        var steps = workout.Steps;

        var isStandaloneRecovery = string.Equals(workout.SessionType, "recovery_run", StringComparison.Ordinal)
            && steps.Count == 1
            && string.Equals(steps[0].Kind, "steady", StringComparison.Ordinal);

        if (isStandaloneRecovery)
        {
            sections.Add($"Recovery Run\n{RenderScalarStepLine(steps[0])}");
        }
        else
        {
            var i = 0;
            while (i < steps.Count)
            {
                var step = steps[i];
                switch (step.Kind)
                {
                    case "warmup":
                        sections.Add($"Warmup\n{RenderScalarStepLine(step)}");
                        i++;
                        break;

                    case "steady":
                        var sb = new StringBuilder();
                        sb.Append("Main Set");
                        while (i < steps.Count && string.Equals(steps[i].Kind, "steady", StringComparison.Ordinal))
                        {
                            sb.Append('\n').Append(RenderScalarStepLine(steps[i]));
                            i++;
                        }
                        sections.Add(sb.ToString());
                        break;

                    case "repeat":
                        var work = step.Work!;
                        var recovery = step.Recovery!;
                        var workCue = FormatCue(work.Cue);
                        var recCue = FormatCue(recovery.Cue);
                        sections.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{step.Reps!.Value}x\n- {FormatDuration(work.DurationSeconds)} {FormatTarget(work.Target)}{workCue}\n- {FormatDuration(recovery.DurationSeconds)} Recovery {FormatTarget(recovery.Target)}{recCue}"));
                        i++;
                        break;

                    case "cooldown":
                        sections.Add($"Cooldown\n{RenderScalarStepLine(step)}");
                        i++;
                        break;

                    default:
                        throw new InvalidOperationException($"Unexpected step kind '{step.Kind}'.");
                }
            }
        }

        var dslText = string.Join("\n\n", sections) + "\n";
        var totalDurationSeconds = WorkoutModalityValidator.ComputeTotalDurationSeconds(workout);

        return new CompiledWorkout(
            workout.Title.Trim(),
            workout.SessionType,
            totalDurationSeconds,
            dslText);
    }

    public static string FormatDuration(int seconds)
    {
        if (seconds >= 60 && seconds % 60 == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m");
        }

        if (seconds >= 120)
        {
            var mins = seconds / 60;
            var rem = seconds % 60;
            return string.Create(CultureInfo.InvariantCulture, $"{mins}m{rem}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds}s");
    }

    public static string FormatTarget(WorkoutTargetSpec target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.Modality switch
        {
            "hr_zone" when target.HrZoneEnd.HasValue => string.Create(
                CultureInfo.InvariantCulture,
                $"Z{target.HrZoneStart!.Value}-Z{target.HrZoneEnd.Value} HR"),
            "hr_zone" => string.Create(
                CultureInfo.InvariantCulture,
                $"Z{target.HrZoneStart!.Value} HR"),
            "pace_range" => string.Create(
                CultureInfo.InvariantCulture,
                $"{FormatPace(target.PaceFastSecPerKm!.Value)}-{FormatPace(target.PaceSlowSecPerKm!.Value)}/km Pace"),
            "rpe" => string.Create(
                CultureInfo.InvariantCulture,
                $"RPE {target.Rpe!.Value}"),
            _ => throw new InvalidOperationException($"Unsupported target modality '{target.Modality}'.")
        };
    }

    private static string RenderScalarStepLine(WorkoutStepNode step)
    {
        var dur = FormatDuration(step.DurationSeconds!.Value);
        var target = FormatTarget(step.Target!);
        var cue = FormatCue(step.Cue);
        return $"- {dur} {target}{cue}";
    }

    private static string FormatCue(string? cue) =>
        string.IsNullOrWhiteSpace(cue) ? string.Empty : $" \"{cue.Trim()}\"";

    private static string FormatPace(int secondsPerKm) =>
        string.Create(CultureInfo.InvariantCulture, $"{secondsPerKm / 60:D2}:{secondsPerKm % 60:D2}");
}
