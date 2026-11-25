using System.Globalization;
using System.Text;
using System.Text.Json;
using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Context-aware deterministic <see cref="ILlmClient"/> stub for Role 2 (<see cref="LlmProgressNarrator"/>).
/// Generates domain-typed <see cref="NarrativeReport"/> JSON without CSS classes or prescriptive aerobic paces.
/// </summary>
public sealed class NarrativeStubLlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var contextJson = ExtractContextJson(request.UserPrompt);
        using var doc = JsonDocument.Parse(contextJson);
        var root = doc.RootElement;

        var report = BuildReport(root);
        var json = JsonSerializer.Serialize(report, SerializerOptions);

        return Task.FromResult(new LlmResponse(
            Success: true,
            Text: json,
            Error: null,
            Model: "narrative-stub-v1",
            ElapsedSeconds: 0.01));
    }

    private static NarrativeReport BuildReport(JsonElement root)
    {
        var refDate = DateOnly.ParseExact(
            GetString(root, "reference_date") ?? "2026-11-25",
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        var triggerReason = GetString(root, "trigger_reason") ?? "manual";

        var athleteEl = GetObj(root, "athlete");
        var athleteName = GetString(athleteEl, "Name") ?? "Athlet";
        var z2Max = GetInt(athleteEl, "Z2MaxHeartRate") ?? 158;

        var compassEl = GetObj(root, "current_compass");
        var healthState = compassEl.HasValue ? (GetString(compassEl.Value, "health_state") ?? "Normal") : "Normal";
        var blockFocus = compassEl.HasValue ? GetString(compassEl.Value, "current_block_focus") : null;

        var latestRunEl = GetObj(root, "latest_completed_run");
        var z2GapEl = GetObj(root, "zone2_gap_trend");
        var macroTrendsCount = GetArrayLength(root, "macro_trends_4_weeks");
        var scheduleHighlights = ParseScheduleHighlights(root);

        var (statusTitle, statusBadge, statusSeverity) = healthState switch
        {
            "SickHold" => ("Krankheitspause & konsequente Erholung", "SickHold aktiv", "warning"),
            "ReEntry" => ("Behutsamer Wiedereinstieg in Zone 2", "ReEntry-Aufbau", "info"),
            _ when latestRunEl.HasValue => ("Kontinuierlicher Form- und Basisaufbau", "Plan im Takt", "success"),
            _ => ("Kaltstart: Aerobes Fundament etablieren", "Basisaufbau", "info")
        };

        var sb = new StringBuilder();
        sb.Append("### Einordnung & Status für ").AppendLine(athleteName);
        if (latestRunEl.HasValue)
        {
            var runName = GetString(latestRunEl.Value, "Name") ?? "Lauf";
            var runDate = GetString(latestRunEl.Value, "Date") ?? refDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var distKm = GetDouble(latestRunEl.Value, "DistanceKm") ?? 0.0;
            var durMin = GetDouble(latestRunEl.Value, "DurationMinutes") ?? 0.0;
            var z1z2 = GetDouble(latestRunEl.Value, "Z1Z2Percentage");
            var z1z2Label = z1z2.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $" ({z1z2.Value:F1}% in Z1/Z2)")
                : string.Empty;
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Der letzte Lauf \"{runName}\" am {runDate} umfasste {distKm:F2} km in {durMin:F0} min{z1z2Label}."));
        }
        else
        {
            sb.AppendLine("Bisher liegen keine absolvierten Läufe vor; der Fokus liegt vollständig auf verletzungsfreiem Einstieg über Herzfrequenzsteuerung.");
        }

        sb.AppendLine();
        sb.AppendLine("### Physiologischer Trend & Steuerung");
        if (z2GapEl.HasValue)
        {
            var early = GetString(z2GapEl.Value, "EarlyAveragePaceText") ?? "-";
            var recent = GetString(z2GapEl.Value, "RecentAveragePaceText") ?? "-";
            var delta = GetDouble(z2GapEl.Value, "DeltaSecondsPerKm") ?? 0.0;
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Die beobachtete Z2-GAP-Pace entwickelte sich von {early} auf {recent} (Delta {delta:+0.0;-0.0;0.0} s/km) – rein als Ergebnis der Herzfrequenzdisziplin unter {z2Max} bpm."));
        }
        else
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Grundlagenläufe werden konsequent nach Puls (<= {z2Max} bpm) gesteuert; an Steigungen rechtzeitig zügige Gehpausen (Run-Walk) einbauen."));
        }

        if (!string.IsNullOrWhiteSpace(blockFocus))
        {
            sb.AppendLine();
            sb.Append("### Aktueller Blockfokus: ").AppendLine(blockFocus);
        }

        var insights = new List<NarrativeInsight>();
        if (latestRunEl.HasValue)
        {
            var z1z2 = GetDouble(latestRunEl.Value, "Z1Z2Percentage");
            var aef = GetDouble(latestRunEl.Value, "AerobicEfficiencyFactor");
            insights.Add(new NarrativeInsight
            {
                Title = "Letzte Einheit & Pulsdisziplin",
                Severity = z1z2 is >= 80.0 ? "success" : "info",
                Badge = z1z2.HasValue
                    ? string.Create(CultureInfo.InvariantCulture, $"{z1z2.Value:F0}% Z1/Z2")
                    : "Abgeschlossen",
                Text = aef.HasValue
                    ? string.Create(CultureInfo.InvariantCulture, $"AEF {aef.Value:F3} bestätigt solide aerobe Verarbeitung ohne Überpacen.")
                    : "Herzfrequenzgestützte Verarbeitung der jüngsten Belastung erfasst."
            });
        }
        else
        {
            insights.Add(new NarrativeInsight
            {
                Title = "Kaltstart ohne Laufhistorie",
                Severity = "info",
                Badge = "0 Läufe",
                Text = string.Create(CultureInfo.InvariantCulture, $"Erste Einheiten strikt in Z1/Z2 unter {z2Max} bpm mit Gehpausen absolvieren.")
            });
        }

        insights.Add(new NarrativeInsight
        {
            Title = "Gesundheits- & Blockstatus",
            Severity = string.Equals(healthState, "SickHold", StringComparison.Ordinal) ? "warning" : "info",
            Badge = healthState,
            Text = string.Equals(healthState, "SickHold", StringComparison.Ordinal)
                ? "Offene Einheiten sind pausiert, bis Erholungsmarker und Befinden wieder stabil sind."
                : string.Create(CultureInfo.InvariantCulture, $"4-Wochen-Makrofenster berücksichtigt {macroTrendsCount} dokumentierte Woche(n).")
        });

        return new NarrativeReport
        {
            ReferenceDate = refDate,
            TriggerReason = triggerReason,
            StatusTitle = statusTitle,
            StatusBadge = statusBadge,
            StatusSeverity = statusSeverity,
            CommentaryMarkdown = sb.ToString().TrimEnd(),
            Insights = insights,
            ScheduleHighlights = scheduleHighlights
        };
    }

    private static List<string> ParseScheduleHighlights(JsonElement root)
    {
        var list = new List<string>();
        if (!TryGetProp(root, "reconciled_schedule_summary", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in arr.EnumerateArray().Take(5))
        {
            var date = GetString(item, "Date") ?? "";
            var title = GetString(item, "Title") ?? "Workout";
            var tier = GetString(item, "Tier") ?? "watch_commit";
            var ownership = GetString(item, "Ownership") ?? "auto";
            if (!string.IsNullOrWhiteSpace(date))
            {
                list.Add($"{date} [{tier}/{ownership}]: {title}");
            }
        }

        return list;
    }

    private static string ExtractContextJson(string userPrompt)
    {
        const string marker = "ORIGINAL CONTEXT:";
        var idx = userPrompt.IndexOf(marker, StringComparison.Ordinal);
        var slice = idx >= 0 ? userPrompt[(idx + marker.Length)..] : userPrompt;
        return AutopilotPromptBuilder.ExtractJsonPayload(slice);
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

    private static JsonElement? GetObj(JsonElement parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? GetString(JsonElement? parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? GetString(JsonElement parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement? parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? GetDouble(JsonElement parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static int GetArrayLength(JsonElement parent, string name) =>
        TryGetProp(parent, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
}
