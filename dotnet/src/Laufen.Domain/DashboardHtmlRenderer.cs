using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Laufen.Domain;

/// <summary>
/// Pure self-contained static HTML5 dashboard renderer with zero external JS/CDN dependencies,
/// interactive inline SVG charts, date/week/intensity filters, full HTML encoding, and strict
/// Coaching DNA aerobic pace hygiene.
/// </summary>
public static partial class DashboardHtmlRenderer
{
    private static readonly string[] QualityScheduleKeywords =
        ["interval", "tempo", "schwelle", "threshold", "speed", "fartlek", "fahrtspiel", "race", "wettkampf"];

    private static readonly Dictionary<int, string> ZoneColorMap = new()
    {
        [1] = "#94a3b8",
        [2] = "#10b981",
        [3] = "#f59e0b",
        [4] = "#f97316",
        [5] = "#f43f5e",
        [6] = "#c084fc",
        [7] = "#fb7185"
    };

    private static readonly JsonSerializerOptions EmbeddedJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false
    };

    [GeneratedRegex(@"\b\d{1,2}:\d{2}(?:\s*-\s*\d{1,2}:\d{2})?\s*(?:/km|min/km)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PaceTokenRegex();

    public static string Render(DashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder(65536);
        var title = string.Create(CultureInfo.InvariantCulture, $"Lauf- & Leistungs-Dashboard ({snapshot.AthleteName})");
        var healthState = snapshot.CurrentCompass?.HealthState ?? "Normal";

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"de\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.Append("  <title>").Append(Enc(title)).AppendLine("</title>");
        AppendStyles(sb);
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");

        AppendHeader(sb, snapshot, title, healthState);
        AppendDataSourceBanner(sb, snapshot);
        AppendFilterBar(sb, snapshot);
        AppendKpiStrip(sb, snapshot);
        AppendMilestonesSection(sb, snapshot);
        AppendAutopilotScheduleSection(sb, snapshot);
        AppendNarratorSection(sb, snapshot);
        AppendChartsGridSection(sb, snapshot);
        AppendForecastAndZonesSection(sb, snapshot);
        AppendReviewsTableSection(sb, snapshot);

        sb.AppendLine("</div>");
        sb.AppendLine("<div id=\"chart-tooltip\" class=\"tooltip\" hidden></div>");
        AppendEmbeddedDataScript(sb, snapshot);
        AppendScript(sb);
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void AppendStyles(StringBuilder sb)
    {
        sb.AppendLine("""
              <style>
                :root {
                  --bg: #0b0f19;
                  --card: #151c2c;
                  --card-soft: rgba(15, 23, 42, 0.65);
                  --border: #1e293b;
                  --text: #f8fafc;
                  --muted: #94a3b8;
                  --cyan: #38bdf8;
                  --emerald: #10b981;
                  --amber: #f59e0b;
                  --orange: #f97316;
                  --rose: #f43f5e;
                }
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body { background: var(--bg); color: var(--text); font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; line-height: 1.5; padding: 24px; overflow-x: clip; }
                .container { max-width: 1260px; margin: 0 auto; display: flex; flex-direction: column; gap: 22px; }
                header { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 14px; padding-bottom: 16px; border-bottom: 1px solid var(--border); }
                h1 { font-size: 1.7rem; font-weight: 700; color: var(--text); }
                h2 { font-size: 1.12rem; font-weight: 600; margin-bottom: 10px; color: var(--text); }
                h3 { font-size: 0.96rem; font-weight: 600; margin-bottom: 8px; color: var(--cyan); }
                .subtitle { color: var(--muted); font-size: 0.88rem; }
                .badge { display: inline-flex; align-items: center; gap: 6px; padding: 4px 11px; border-radius: 999px; font-size: 0.78rem; font-weight: 600; border: 1px solid var(--border); background: rgba(56,189,248,0.12); color: var(--cyan); }
                .sev-success { background: rgba(16,185,129,0.15); color: var(--emerald); border-color: rgba(16,185,129,0.35); }
                .sev-warning { background: rgba(245,158,11,0.15); color: var(--amber); border-color: rgba(245,158,11,0.35); }
                .sev-danger { background: rgba(244,63,94,0.16); color: var(--rose); border-color: rgba(244,63,94,0.38); }
                .sev-info { background: rgba(56,189,248,0.12); color: var(--cyan); border-color: rgba(56,189,248,0.32); }
                .card { background: var(--card); border: 1px solid var(--border); border-radius: 12px; padding: 18px; min-width: 0; max-width: 100%; }
                .full-width { grid-column: 1 / -1; }
                .filter-bar { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between; background: var(--card); border: 1px solid var(--border); border-radius: 12px; padding: 12px 16px; }
                .filter-group { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
                .filter-buttons { display: flex; flex-wrap: wrap; gap: 6px; }
                .filter-btn, .toggle-btn, .btn-small { background: rgba(15,23,42,0.85); color: var(--muted); border: 1px solid var(--border); border-radius: 8px; padding: 6px 11px; font-size: 0.82rem; font-weight: 600; cursor: pointer; transition: all 0.15s ease; }
                .filter-btn:hover, .toggle-btn:hover, .btn-small:hover { color: var(--text); border-color: var(--cyan); }
                .filter-btn.active, .toggle-btn.active { background: rgba(56,189,248,0.16); color: var(--cyan); border-color: var(--cyan); }
                .custom-range { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
                .date-input, .filter-select, input[type="search"] { background: #0f172a; color: var(--text); border: 1px solid var(--border); border-radius: 8px; padding: 6px 10px; font-size: 0.85rem; }
                input[type="search"] { min-width: 0; width: 100%; max-width: 280px; }
                .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 175px), 1fr)); gap: 14px; }
                .kpi-label { font-size: 0.78rem; color: var(--muted); text-transform: uppercase; letter-spacing: 0.04em; }
                .kpi-value { font-size: 1.48rem; font-weight: 700; margin-top: 4px; }
                .kpi-sub { font-size: 0.8rem; color: var(--muted); margin-top: 2px; }
                .two-col { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 360px), 1fr)); gap: 18px; }
                .charts-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 460px), 1fr)); gap: 18px; }
                .chart-header { display: flex; justify-content: space-between; align-items: flex-start; gap: 10px; flex-wrap: wrap; margin-bottom: 10px; }
                .chart-toggles { display: flex; gap: 6px; flex-wrap: wrap; }
                .chart-host { width: 100%; min-width: 0; max-width: 100%; min-height: 170px; }
                .legend-row { display: flex; gap: 12px; flex-wrap: wrap; font-size: 0.78rem; color: var(--muted); margin-top: 8px; }
                .legend-item { display: inline-flex; align-items: center; gap: 5px; }
                .legend-dot { width: 9px; height: 9px; border-radius: 50%; display: inline-block; }
                .schedule-list { display: flex; flex-direction: column; gap: 10px; }
                .schedule-item { padding: 10px 12px; border-radius: 8px; background: var(--card-soft); border: 1px solid var(--border); display: flex; justify-content: space-between; gap: 12px; align-items: center; }
                .insight-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: 12px; margin-top: 12px; }
                .insight-item { padding: 12px; border-radius: 8px; background: rgba(15,23,42,0.55); border: 1px solid var(--border); min-width: 0; max-width: 100%; }
                .zones-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 200px), 1fr)); gap: 10px; }
                .table-wrap { width: 100%; overflow-x: auto; -webkit-overflow-scrolling: touch; }
                table { width: 100%; border-collapse: collapse; font-size: 0.88rem; }
                th, td { text-align: left; padding: 9px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
                th { color: var(--muted); font-weight: 600; font-size: 0.8rem; text-transform: uppercase; }
                .controls { display: flex; gap: 10px; flex-wrap: wrap; margin-bottom: 12px; align-items: center; justify-content: space-between; }
                details summary { cursor: pointer; color: var(--cyan); font-size: 0.82rem; }
                .banner { padding: 12px 14px; border-radius: 10px; border: 1px solid rgba(245,158,11,0.35); background: rgba(245,158,11,0.1); font-size: 0.9rem; }
                .zone-list { display: flex; flex-direction: column; gap: 8px; }
                .zone-row { display: flex; justify-content: space-between; padding: 7px 10px; border-radius: 8px; background: rgba(15,23,42,0.5); border: 1px solid var(--border); }
                svg { width: 100%; height: auto; display: block; }
                .svg-bar { cursor: pointer; transition: opacity 0.15s ease; }
                .svg-bar:hover { opacity: 0.88; }
                .svg-bar.dimmed { opacity: 0.35; }
                .svg-bar.selected { stroke: #f8fafc; stroke-width: 1.5; }
                .svg-dot { cursor: pointer; }
                .svg-dot:hover { r: 5.5; }
                .tooltip { position: fixed; z-index: 9999; pointer-events: none; max-width: 290px; background: rgba(15, 23, 42, 0.96); color: var(--text); border: 1px solid var(--cyan); border-radius: 8px; padding: 8px 10px; font-size: 0.79rem; line-height: 1.4; box-shadow: 0 8px 24px rgba(0, 0, 0, 0.45); white-space: pre-line; }
                @media (max-width: 640px) { body { padding: 14px; } .two-col, .charts-grid { grid-template-columns: 1fr; } }
              </style>
            """);
    }

    private static void AppendHeader(StringBuilder sb, DashboardSnapshot snapshot, string title, string healthState)
    {
        var healthClass = healthState switch
        {
            "SickHold" => "sev-danger",
            "ReEntry" => "sev-warning",
            _ => "sev-success"
        };

        sb.AppendLine("  <header>");
        sb.AppendLine("    <div>");
        sb.Append("      <h1>").Append(Enc(title)).AppendLine("</h1>");
        sb.Append("      <p class=\"subtitle\">Referenzdatum: ").Append(Enc(snapshot.ReferenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .Append(" | Tenant: ").Append(Enc(snapshot.Slug))
            .Append(" | Erstellt: ").Append(Enc(snapshot.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)))
            .AppendLine("</p>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div>");
        sb.Append("      <span class=\"badge ").Append(healthClass).Append("\">Status: ").Append(Enc(healthState)).AppendLine("</span>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </header>");
    }

    private static void AppendDataSourceBanner(StringBuilder sb, DashboardSnapshot snapshot)
    {
        if (snapshot.TotalRuns == 0)
        {
            sb.Append("  <div class=\"banner\">Keine exportierten Laufaktivitaeten fuer <strong>")
                .Append(Enc(snapshot.AthleteName))
                .Append("</strong> vorhanden");
            if (snapshot.StravaPlaceholderCount > 0)
            {
                sb.Append(" (").Append(snapshot.StravaPlaceholderCount).Append(" Strava-Platzhalter ohne Rohdaten)");
            }
            sb.AppendLine(".</div>");
            return;
        }

        if (snapshot.StravaPlaceholderCount > 0)
        {
            sb.Append("  <div class=\"banner\">Datenquellen-Hinweis: <strong>")
                .Append(snapshot.TotalRuns)
                .Append(" Garmin-Laeufe</strong> analysiert; ")
                .Append(snapshot.StravaPlaceholderCount)
                .AppendLine(" Strava-Eintraege ohne Rohdaten uebersprungen.</div>");
        }
    }

    private static void AppendFilterBar(StringBuilder sb, DashboardSnapshot snapshot)
    {
        var refStr = snapshot.ReferenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sb.AppendLine("  <section class=\"filter-bar\" aria-label=\"Zeitraum-Filter\">");
        sb.AppendLine("    <div class=\"filter-group\">");
        sb.AppendLine("      <span class=\"subtitle\" style=\"font-weight:600;\">Zeitraum:</span>");
        sb.AppendLine("      <div class=\"filter-buttons\" id=\"date-filter-buttons\">");
        sb.AppendLine("        <button type=\"button\" class=\"filter-btn active\" data-range=\"all\">Gesamter Zeitraum</button>");
        sb.AppendLine("        <button type=\"button\" class=\"filter-btn\" data-range=\"12w\">Letzte 12 Wochen</button>");
        sb.AppendLine("        <button type=\"button\" class=\"filter-btn\" data-range=\"4w\">Letzte 4 Wochen</button>");
        sb.AppendLine("        <button type=\"button\" class=\"filter-btn\" data-range=\"month\">Dieser Monat</button>");
        sb.AppendLine("        <button type=\"button\" class=\"filter-btn\" data-range=\"custom\">Benutzerdefiniert</button>");
        sb.AppendLine("      </div>");
        sb.AppendLine("      <div class=\"custom-range\" id=\"custom-date-range\" hidden>");
        sb.Append("        <input type=\"date\" id=\"filter-start\" class=\"date-input\" value=\"").Append(Enc(refStr)).AppendLine("\">");
        sb.AppendLine("        <span class=\"subtitle\">bis</span>");
        sb.Append("        <input type=\"date\" id=\"filter-end\" class=\"date-input\" value=\"").Append(Enc(refStr)).AppendLine("\">");
        sb.AppendLine("        <button type=\"button\" class=\"btn-small\" id=\"btn-apply-custom\">Anwenden</button>");
        sb.AppendLine("        <button type=\"button\" class=\"btn-small\" id=\"btn-reset-filter\">Reset</button>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"filter-group\">");
        sb.AppendLine("      <span class=\"badge sev-info\" id=\"active-filter-text\">Alle Aktivitaeten</span>");
        sb.AppendLine("      <button type=\"button\" class=\"btn-small\" id=\"btn-reset-week\" hidden>Wochenfilter aufheben</button>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");
    }

    private static void AppendKpiStrip(StringBuilder sb, DashboardSnapshot snapshot)
    {
        var form = snapshot.ReportSnapshot.FitnessForm;
        var aefText = snapshot.BaseAerobicEf.HasValue
            ? snapshot.BaseAerobicEf.Value.ToString("F3", CultureInfo.InvariantCulture)
            : "-";

        sb.AppendLine("  <section class=\"kpi-grid\">");
        AppendKpiCard(sb, "kpi-dist", "kpi-runs", "Gesamtdistanz", string.Create(CultureInfo.InvariantCulture, $"{snapshot.TotalDistanceKm:F1} km"), string.Create(CultureInfo.InvariantCulture, $"{snapshot.TotalRuns} Laeufe"));
        AppendKpiCard(sb, "kpi-pace", "kpi-time", "Durchschnittstempo", snapshot.AveragePaceText, string.Create(CultureInfo.InvariantCulture, $"Laufzeit {FormatDuration(snapshot.TotalDurationSeconds)}"));
        AppendKpiCard(sb, "kpi-hr", "kpi-z1z2", "Herzfrequenz Ø", snapshot.AverageHeartRate > 0 ? string.Create(CultureInfo.InvariantCulture, $"{snapshot.AverageHeartRate:F0} bpm") : "-", string.Create(CultureInfo.InvariantCulture, $"Z1/Z2 Anteil {snapshot.OverallZ1Z2Percentage:F1}%"));
        AppendKpiCard(sb, "kpi-aef", "kpi-aef-sub", "Aerobe Effizienz (AEF)", aefText, "Basis-Laeufe (>=80% Z1/Z2)");
        AppendKpiCard(sb, "kpi-ctl-atl", "kpi-tsb", "Fitness / Ermuedung", string.Create(CultureInfo.InvariantCulture, $"CTL {form.LatestCtl:F1} / ATL {form.LatestAtl:F1}"), string.Create(CultureInfo.InvariantCulture, $"TSB {form.CurrentTsb:+0.0;-0.0;0.0} ({form.TsbLevel})"));
        sb.AppendLine("  </section>");
    }

    private static void AppendKpiCard(StringBuilder sb, string valueId, string subId, string label, string value, string sub)
    {
        sb.AppendLine("    <div class=\"card\">");
        sb.Append("      <div class=\"kpi-label\">").Append(Enc(label)).AppendLine("</div>");
        sb.Append("      <div class=\"kpi-value\" id=\"").Append(Enc(valueId)).Append("\">").Append(Enc(value)).AppendLine("</div>");
        sb.Append("      <div class=\"kpi-sub\" id=\"").Append(Enc(subId)).Append("\">").Append(Enc(sub)).AppendLine("</div>");
        sb.AppendLine("    </div>");
    }

    private static void AppendMilestonesSection(StringBuilder sb, DashboardSnapshot snapshot)
    {
        if (snapshot.Milestones is not { } ms)
        {
            return;
        }

        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine("    <h2>14-Tage-Etappenziele &amp; 6-12-Wochen-Nordstern</h2>");
        sb.Append("    <p class=\"subtitle\" style=\"margin-bottom:10px;\">Nordstern-Trend (")
            .Append(ms.NorthStar.WindowWeeks)
            .Append("w): <strong>").Append(Enc(ms.NorthStar.TrendDirection)).Append("</strong>")
            .Append(" | Z2 GAP: ").Append(Enc(ms.NorthStar.Z2GapEarlyPaceText ?? "-"))
            .Append(" &rarr; ").Append(Enc(ms.NorthStar.Z2GapRecentPaceText ?? "-"))
            .Append(" | Basis-AEF: ").Append(ms.NorthStar.BaseAefEarly?.ToString("F3", CultureInfo.InvariantCulture) ?? "-")
            .Append(" &rarr; ").Append(ms.NorthStar.BaseAefRecent?.ToString("F3", CultureInfo.InvariantCulture) ?? "-")
            .AppendLine("</p>");

        sb.AppendLine("    <div class=\"insight-grid\">");
        foreach (var item in ms.ActiveMilestones)
        {
            var badgeClass = item.Status switch
            {
                "achieved" => "sev-success",
                "on_track" => "sev-info",
                "paused_health" => "sev-warning",
                _ => "sev-danger"
            };
            var pct = Math.Clamp((int)Math.Round(item.ProgressRatio * 100.0, MidpointRounding.AwayFromZero), 0, 100);
            sb.AppendLine("      <div class=\"insight-item\">");
            sb.Append("        <span class=\"badge ").Append(badgeClass).Append("\">").Append(Enc(item.Status)).AppendLine("</span>");
            sb.Append("        <h3 style=\"margin-top:6px;\">").Append(Enc(item.Title)).AppendLine("</h3>");
            sb.Append("        <div style=\"font-weight:700;margin-bottom:6px;\">")
                .Append(item.CurrentValue.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" / ")
                .Append(item.TargetValue.ToString("F1", CultureInfo.InvariantCulture))
                .Append(' ').Append(Enc(item.Unit)).Append(" (").Append(pct).Append("%)</div>");
            sb.Append("        <div style=\"height:6px;background:#0f172a;border-radius:999px;overflow:hidden;margin-bottom:6px;\"><div style=\"height:100%;width:")
                .Append(pct).Append("%;background:var(--cyan);\"></div></div>");
            sb.Append("        <p class=\"subtitle\">").Append(Enc(item.Evidence)).AppendLine("</p>");
            sb.AppendLine("      </div>");
        }
        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");
    }

    private static void AppendAutopilotScheduleSection(StringBuilder sb, DashboardSnapshot snapshot)
    {
        var watchCutoff = snapshot.ReferenceDate.AddDays(6);
        var upcoming = snapshot.Dossier.Calendar.UpcomingEventsNext14Days
            .Where(e => string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Date)
            .ToList();
        var watchItems = upcoming.Where(e => e.Date <= watchCutoff).ToList();
        var previewItems = upcoming.Where(e => e.Date > watchCutoff).ToList();

        sb.AppendLine("  <section class=\"two-col\">");
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <h2>14-Tage Autopilot &amp; Block-Kompass</h2>");
        if (snapshot.CurrentCompass is { } compass)
        {
            sb.Append("      <p class=\"subtitle\" style=\"margin-bottom:10px;\"><strong>Fokus:</strong> ")
                .Append(Enc(compass.CurrentBlockFocus))
                .Append(" &mdash; ")
                .Append(Enc(compass.LastDecisionSummary))
                .AppendLine("</p>");
        }

        sb.AppendLine("      <h3>Tage 1-7 (Garmin Watch Commit Window)</h3>");
        AppendScheduleList(sb, watchItems, "Keine offenen Einheiten im Watch-Fenster.");
        sb.AppendLine("      <h3 style=\"margin-top:14px;\">Tage 8-14 (Preview Window)</h3>");
        AppendScheduleList(sb, previewItems, "Keine Einheiten im Preview-Fenster.");
        sb.AppendLine("    </div>");

        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <h2>14-Tage Banister CTL/ATL/TSB Projektion</h2>");
        if (snapshot.Banister14DayProjection is { } proj)
        {
            sb.Append("      <p class=\"subtitle\" style=\"margin-bottom:10px;\">CTL ")
                .Append(proj.StartingCtl.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" &rarr; ")
                .Append(proj.EndingCtl.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" | TSB min ")
                .Append(proj.MinProjectedTsb.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture))
                .Append(" (")
                .Append(Enc(proj.MinTsbDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .AppendLine(")</p>");
            AppendBanisterSparklineSvg(sb, proj.DailyPoints);
            sb.AppendLine("      <div class=\"legend-row\">");
            sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#38bdf8;\"></span>CTL (Fitness)</span>");
            sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#f59e0b;\"></span>ATL (Ermuedung)</span>");
            sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#10b981;\"></span>TSB (Form)</span>");
            sb.AppendLine("      </div>");
        }
        else
        {
            sb.AppendLine("      <p class=\"subtitle\">Keine CTL/ATL-Ausgangsbasis verfuegbar.</p>");
        }
        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");
    }

    private static void AppendScheduleList(StringBuilder sb, List<ScheduledCalendarItem> items, string emptyText)
    {
        if (items.Count == 0)
        {
            sb.Append("      <p class=\"subtitle\">").Append(Enc(emptyText)).AppendLine("</p>");
            return;
        }

        sb.AppendLine("      <div class=\"schedule-list\">");
        foreach (var item in items)
        {
            var ownershipBadge = item.IsCompleted ? "sev-success" : (item.Ownership == "user_locked" ? "sev-warning" : "sev-info");
            var ownershipText = item.IsCompleted ? "completed" : item.Ownership;
            var safeTitle = SanitizePlannedWorkoutTitle(item.Name);
            var dur = item.DurationSeconds.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"{item.DurationSeconds.Value / 60}m")
                : "-";

            sb.AppendLine("        <div class=\"schedule-item\">");
            sb.Append("          <div><strong>").Append(Enc(item.Date.ToString("ddd dd.MM.", CultureInfo.InvariantCulture)))
                .Append("</strong> &mdash; ").Append(Enc(safeTitle))
                .Append(" <span class=\"subtitle\">(").Append(Enc(dur)).Append(")</span></div>");
            sb.Append("          <span class=\"badge ").Append(ownershipBadge).Append("\">").Append(Enc(ownershipText)).AppendLine("</span>");
            sb.AppendLine("        </div>");
        }
        sb.AppendLine("      </div>");
    }

    private static void AppendNarratorSection(StringBuilder sb, DashboardSnapshot snapshot)
    {
        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine("    <h2>Coach-Narrativ &amp; Readiness-Einordnung</h2>");

        if (snapshot.LatestNarrative is { } nar)
        {
            var sevClass = MapSeverityClass(nar.StatusSeverity);
            sb.Append("    <p style=\"margin-bottom:8px;\"><span class=\"badge ").Append(sevClass).Append("\">")
                .Append(Enc(nar.StatusBadge)).Append("</span> <strong>").Append(Enc(nar.StatusTitle)).AppendLine("</strong></p>");
            sb.Append("    <p style=\"white-space:pre-line;color:var(--muted);\">").Append(Enc(nar.CommentaryMarkdown)).AppendLine("</p>");

            if (nar.Insights.Count > 0)
            {
                sb.AppendLine("    <div class=\"insight-grid\">");
                foreach (var insight in nar.Insights)
                {
                    sb.AppendLine("      <div class=\"insight-item\">");
                    sb.Append("        <span class=\"badge ").Append(MapSeverityClass(insight.Severity)).Append("\">")
                        .Append(Enc(insight.Badge)).AppendLine("</span>");
                    sb.Append("        <h3 style=\"margin-top:6px;\">").Append(Enc(insight.Title)).AppendLine("</h3>");
                    sb.Append("        <p class=\"subtitle\">").Append(Enc(insight.Text)).AppendLine("</p>");
                    sb.AppendLine("      </div>");
                }
                sb.AppendLine("    </div>");
            }
        }
        else
        {
            var r = snapshot.Dossier.Readiness;
            sb.Append("    <p class=\"subtitle\">Deterministischer Readiness-Status: RHR ")
                .Append(r.RestingHrLatest?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append(" bpm | HRV ")
                .Append(r.HrvLatest?.ToString("F1", CultureInfo.InvariantCulture) ?? "-")
                .Append(" | Schlaf ")
                .Append(r.SleepHoursLatest?.ToString("F1", CultureInfo.InvariantCulture) ?? "-")
                .AppendLine("h.</p>");
        }

        sb.AppendLine("  </section>");
    }

    private static void AppendChartsGridSection(StringBuilder sb, DashboardSnapshot snapshot)
    {
        sb.AppendLine("  <section class=\"charts-grid\">");

        // Chart 1: Weekly Volume + 4-week forecast + Load (clickable week cross-filter)
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <div class=\"chart-header\"><div><h2>Wochenvolumen &amp; Belastung (inkl. Prognose)</h2><p class=\"subtitle\">Klick auf Wochenbalken filtert KPIs &amp; Tabelle</p></div></div>");
        sb.AppendLine("      <div class=\"chart-host\" id=\"chart-weekly-volume\">");
        AppendWeeklyVolumeSvg(sb, snapshot.ReportSnapshot.WeeklyTrends);
        sb.AppendLine("      </div>");
        sb.AppendLine("      <div class=\"legend-row\">");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#10b981;\"></span>Basis &ge;80%</span>");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#38bdf8;\"></span>Gemischt / Aufbau</span>");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#f59e0b;\"></span>Trainingsload</span>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");

        // Chart 2: Stacked Weekly HR Zones (Z1-Z5+)
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <div class=\"chart-header\"><div><h2>Herzfrequenz-Zonenverteilung pro Woche</h2><p class=\"subtitle\">Gestapelte Anteile (Z1-Z5+) &middot; Ziel: 80% Z1/Z2</p></div></div>");
        sb.AppendLine("      <div class=\"chart-host\" id=\"chart-zone-dist\"></div>");
        sb.AppendLine("      <div class=\"legend-row\">");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#94a3b8;\"></span>Z1</span>");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#10b981;\"></span>Z2</span>");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#f59e0b;\"></span>Z3</span>");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#f97316;\"></span>Z4</span>");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#f43f5e;\"></span>Z5+</span>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");

        // Chart 3: Historical Fitness & Form (CTL / ATL / TSB)
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <div class=\"chart-header\"><div><h2>Formkurve Historie: Fitness vs. Ermuedung</h2><p class=\"subtitle\">Verlauf CTL (Fitness), ATL (Ermuedung) &amp; TSB (Form)</p></div></div>");
        sb.AppendLine("      <div class=\"chart-host\" id=\"chart-form-history\"></div>");
        sb.AppendLine("      <div class=\"legend-row\">");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#38bdf8;\"></span>CTL</span>");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#f59e0b;\"></span>ATL</span>");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#10b981;\"></span>TSB</span>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");

        // Chart 4: Aerobic Efficiency & Pace Trend with Base/All toggle
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <div class=\"chart-header\">");
        sb.AppendLine("        <div><h2>Aerobe Effizienz (EF) &amp; Pace-Trend</h2><p class=\"subtitle\" id=\"ef-chart-sub\">Effizienzfaktor vs. Pace</p></div>");
        sb.AppendLine("        <div class=\"chart-toggles\">");
        sb.AppendLine("          <button type=\"button\" class=\"toggle-btn active\" data-ef-mode=\"base\">Nur Z2-Basis</button>");
        sb.AppendLine("          <button type=\"button\" class=\"toggle-btn\" data-ef-mode=\"all\">Alle Laeufe</button>");
        sb.AppendLine("        </div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("      <div class=\"chart-host\" id=\"chart-ef-pace\"></div>");
        sb.AppendLine("      <div class=\"legend-row\">");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#38bdf8;\"></span>AEF (m/min / bpm)</span>");
        sb.AppendLine("        <span class=\"legend-item\"><span class=\"legend-dot\" style=\"background:#10b981;\"></span>Pace (schneller = hoeher)</span>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");

        // Chart 5: Zone-Pace (GAP) Development per HR Zone
        sb.AppendLine("    <div class=\"card full-width\">");
        sb.AppendLine("      <div class=\"chart-header\">");
        sb.AppendLine("        <div>");
        sb.AppendLine("          <h2>Zone-2 GAP &amp; Pace-Entwicklung pro HF-Zone</h2>");
        if (snapshot.ZonePaceTrends.Summary.TryGetValue("z2", out var z2) && z2.Count > 0)
        {
            sb.Append("          <p class=\"subtitle\">Steigungsbereinigt (GAP), Gehpausen gefiltert &middot; Z2: Frueh <strong>")
                .Append(Enc(z2.EarlyAveragePaceText ?? "-"))
                .Append("</strong> &rarr; Aktuell <strong>").Append(Enc(z2.RecentAveragePaceText ?? "-"))
                .Append("</strong> (Delta ")
                .Append(z2.DeltaSecondsPerKm.HasValue ? z2.DeltaSecondsPerKm.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) : "0.0")
                .Append(" s/km bei ").Append(z2.Count).AppendLine(" Laeufen)</p>");
        }
        else
        {
            sb.AppendLine("          <p class=\"subtitle\">Noch keine ausreichenden Zone-2 Stream-Daten (&ge;120s).</p>");
        }
        sb.AppendLine("        </div>");
        sb.AppendLine("        <div class=\"chart-toggles\">");
        sb.AppendLine("          <button type=\"button\" class=\"toggle-btn active\" data-zp-zone=\"z2\">Z2</button>");
        sb.AppendLine("          <button type=\"button\" class=\"toggle-btn active\" data-zp-zone=\"z3\">Z3</button>");
        sb.AppendLine("          <button type=\"button\" class=\"toggle-btn active\" data-zp-zone=\"z4\">Z4</button>");
        sb.AppendLine("          <button type=\"button\" class=\"toggle-btn\" data-zp-zone=\"z1\">Z1</button>");
        sb.AppendLine("          <button type=\"button\" class=\"toggle-btn active\" data-zp-mode=\"rolling\">Geglaettet (5er)</button>");
        sb.AppendLine("          <button type=\"button\" class=\"toggle-btn\" data-zp-mode=\"raw\">Einzellaeufe</button>");
        sb.AppendLine("        </div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("      <div class=\"chart-host\" id=\"chart-zone-pace\"></div>");
        sb.AppendLine("    </div>");

        sb.AppendLine("  </section>");
    }

    private static void AppendForecastAndZonesSection(StringBuilder sb, DashboardSnapshot snapshot)
    {
        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine("    <h2>Riegel Wettkampf-Prognosen &amp; 4-Wochen-Volumen</h2>");
        sb.AppendLine("    <div class=\"insight-grid\" style=\"margin-bottom:16px;\">");
        foreach (var pred in snapshot.RacePredictions)
        {
            sb.AppendLine("      <div class=\"insight-item\">");
            sb.Append("        <span class=\"badge ").Append(MapSeverityClass(pred.ReadinessSeverity)).Append("\">")
                .Append(Enc(pred.ReadinessLabel)).AppendLine("</span>");
            sb.Append("        <div style=\"font-weight:700;font-size:1.1rem;margin-top:6px;\">")
                .Append(Enc(pred.Name)).Append(": ").Append(Enc(pred.PredictedTimeText)).AppendLine("</div>");
            sb.Append("        <div class=\"subtitle\">Zieltempo ").Append(Enc(pred.TargetPaceText)).Append(" &mdash; ").Append(Enc(pred.Note)).AppendLine("</div>");
            sb.AppendLine("      </div>");
        }
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"table-wrap\">");
        sb.AppendLine("      <table><thead><tr><th>Woche</th><th>Phase</th><th>Distanz</th><th>Load</th></tr></thead><tbody>");
        foreach (var week in snapshot.VolumeForecast)
        {
            sb.Append("        <tr><td>").Append(Enc(week.IsoWeek)).Append("</td><td>").Append(Enc(week.PhaseTitle))
                .Append("</td><td>").Append(week.ProjectedDistanceKm.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" km</td><td>").Append(week.ProjectedLoad.ToString("F0", CultureInfo.InvariantCulture)).AppendLine("</td></tr>");
        }
        sb.AppendLine("      </tbody></table>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");

        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine("    <h2>Kalibrierte Herzfrequenzzonen</h2>");
        sb.AppendLine("    <div class=\"zones-grid\">");
        foreach (var zone in snapshot.HeartRateProfile.Zones.Values.OrderBy(z => z.Zone))
        {
            var color = ZoneColorMap.GetValueOrDefault(zone.Zone, "#94a3b8");
            var rangeStr = zone.Min <= 0
                ? string.Create(CultureInfo.InvariantCulture, $"< {zone.Max + 1} bpm")
                : string.Create(CultureInfo.InvariantCulture, $"{zone.Min} - {zone.Max} bpm");
            sb.AppendLine("      <div class=\"zone-row\">");
            sb.Append("        <span style=\"font-weight:600;color:").Append(color).Append(";\">Z").Append(zone.Zone)
                .Append(' ').Append(Enc(zone.Name)).AppendLine("</span>");
            sb.Append("        <span>").Append(Enc(rangeStr)).AppendLine("</span>");
            sb.AppendLine("      </div>");
        }
        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");
    }

    private static void AppendReviewsTableSection(StringBuilder sb, DashboardSnapshot snapshot)
    {
        var totalCount = snapshot.AllRuns.Count > 0
            ? snapshot.AllRuns.Count
            : Math.Max(snapshot.RecentReviews.Count, snapshot.ReportSnapshot.RecentRuns.Count);

        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine("    <h2>Letzte Laeufe &amp; 4-Stufen-Reviews</h2>");
        sb.AppendLine("    <div class=\"controls\">");
        sb.AppendLine("      <div class=\"filter-group\">");
        sb.AppendLine("        <input id=\"run-filter\" type=\"search\" placeholder=\"Laeufe filtern (Name, Typ, Notiz)...\">");
        sb.AppendLine("        <select id=\"zone-filter\" class=\"filter-select\">");
        sb.AppendLine("          <option value=\"all\">Alle Intensitaeten</option>");
        sb.AppendLine("          <option value=\"base\">Nur Grundlagenausdauer (Z1/Z2 &ge; 80%)</option>");
        sb.AppendLine("          <option value=\"mixed\">Ueberwiegend aerob (60-79%)</option>");
        sb.AppendLine("          <option value=\"quality\">Temporeiz / Intensiv (&lt; 60%)</option>");
        sb.AppendLine("        </select>");
        sb.AppendLine("      </div>");
        sb.Append("      <span class=\"subtitle\" id=\"runs-count-summary\">Zeige ")
            .Append(totalCount).Append(" von ").Append(totalCount).AppendLine(" Laeufen</span>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"table-wrap\">");
        sb.AppendLine("      <table id=\"runs-table\">");
        sb.AppendLine("        <thead><tr><th>Datum</th><th>Lauf</th><th>Distanz</th><th>Zeit</th><th>Pace / Ø HF</th><th>Z1/Z2</th><th>Bewertung</th></tr></thead>");
        sb.AppendLine("        <tbody id=\"runs-tbody\">");

        if (snapshot.AllRuns.Count == 0 && snapshot.RecentReviews.Count == 0 && snapshot.ReportSnapshot.RecentRuns.Count == 0)
        {
            sb.AppendLine("          <tr><td colspan=\"7\" class=\"subtitle\">Keine Laufaktivitaeten gefunden.</td></tr>");
        }
        else if (snapshot.AllRuns.Count > 0)
        {
            foreach (var run in snapshot.AllRuns)
            {
                sb.AppendLine("          <tr class=\"run-row\">");
                sb.Append("            <td>").Append(Enc(run.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).AppendLine("</td>");
                sb.Append("            <td><strong>").Append(Enc(run.Name)).Append("</strong>");
                if (!string.IsNullOrWhiteSpace(run.AthleteNote))
                {
                    sb.Append("<div class=\"subtitle\">Notiz: ").Append(Enc(run.AthleteNote)).Append("</div>");
                }
                sb.AppendLine("</td>");
                sb.Append("            <td>").Append(run.DistanceKm.ToString("F2", CultureInfo.InvariantCulture)).AppendLine(" km</td>");
                sb.Append("            <td>").Append(Enc(run.MovingTimeText)).AppendLine("</td>");
                sb.Append("            <td>").Append(Enc(run.PaceText)).Append("<div class=\"subtitle\">")
                    .Append(run.AverageHeartRate > 0 ? string.Create(CultureInfo.InvariantCulture, $"{run.AverageHeartRate:F0} bpm") : "-").AppendLine("</div></td>");
                sb.Append("            <td>").Append(run.Z1Z2Percentage.HasValue ? string.Create(CultureInfo.InvariantCulture, $"{run.Z1Z2Percentage.Value:F1}%") : "-")
                    .Append(string.IsNullOrWhiteSpace(run.Z2GapPaceText) ? string.Empty : $"<div class=\"subtitle\">Z2 GAP {Enc(run.Z2GapPaceText)}</div>").AppendLine("</td>");
                if (!string.IsNullOrWhiteSpace(run.Directive))
                {
                    sb.Append("            <td><details><summary>").Append(Enc(run.RatingLabel)).Append("</summary>")
                        .Append("<p class=\"subtitle\" style=\"margin-top:4px;\">").Append(Enc(run.Directive)).Append("</p></details></td>");
                }
                else
                {
                    sb.Append("            <td>").Append(Enc(run.RatingLabel)).Append("</td>");
                }
                sb.AppendLine("          </tr>");
            }
        }
        else if (snapshot.RecentReviews.Count > 0)
        {
            foreach (var rev in snapshot.RecentReviews)
            {
                var z2Gap = rev.ZonePaceStats?.Zones.Values.FirstOrDefault(z => z.Zone == 2)?.GapPaceText;
                sb.AppendLine("          <tr class=\"run-row\">");
                sb.Append("            <td>").Append(Enc(rev.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).AppendLine("</td>");
                sb.Append("            <td><strong>").Append(Enc(rev.Name)).AppendLine("</strong></td>");
                sb.Append("            <td>").Append(rev.DistanceKm.ToString("F2", CultureInfo.InvariantCulture)).AppendLine(" km</td>");
                sb.Append("            <td>").Append(Enc(rev.MovingTimeText)).AppendLine("</td>");
                sb.Append("            <td>").Append(Enc(rev.PaceText)).Append("<div class=\"subtitle\">")
                    .Append(rev.AverageHeartRate.ToString("F0", CultureInfo.InvariantCulture)).AppendLine(" bpm</div></td>");
                sb.Append("            <td>").Append(rev.Z1Z2Percentage.ToString("F1", CultureInfo.InvariantCulture)).Append('%')
                    .Append(string.IsNullOrWhiteSpace(z2Gap) ? string.Empty : $"<div class=\"subtitle\">Z2 GAP {Enc(z2Gap)}</div>").AppendLine("</td>");
                sb.Append("            <td><details><summary>").Append(Enc(rev.Review.RatingLabel)).Append("</summary>")
                    .Append("<p class=\"subtitle\" style=\"margin-top:4px;\">").Append(Enc(rev.Review.Stages.Stage4NextRun.Directive)).Append("</p></details></td>");
                sb.AppendLine("          </tr>");
            }
        }

        sb.AppendLine("        </tbody>");
        sb.AppendLine("      </table>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");
    }

    private static void AppendBanisterSparklineSvg(StringBuilder sb, IReadOnlyList<BanisterProjectedDay> points)
    {
        if (points.Count == 0)
        {
            return;
        }

        const int w = 520;
        const int h = 150;
        const int pad = 18;
        var minVal = Math.Min(points.Min(p => p.Tsb), 0.0);
        var maxVal = Math.Max(points.Max(p => Math.Max(p.Ctl, p.Atl)), 10.0);
        var range = Math.Max(1.0, maxVal - minVal);

        string BuildPolyline(Func<BanisterProjectedDay, double> selector)
        {
            var pts = new StringBuilder();
            for (var i = 0; i < points.Count; i++)
            {
                var x = pad + (i * (w - (2 * pad)) / Math.Max(1, points.Count - 1));
                var norm = (selector(points[i]) - minVal) / range;
                var y = h - pad - (norm * (h - (2 * pad)));
                if (i > 0)
                {
                    pts.Append(' ');
                }
                pts.Append(string.Create(CultureInfo.InvariantCulture, $"{x},{y:F1}"));
            }
            return pts.ToString();
        }

        sb.Append("      <svg id=\"banister-svg\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h).AppendLine("\" role=\"img\" aria-label=\"Banister 14d CTL ATL TSB\">");
        sb.Append("        <polyline fill=\"none\" stroke=\"#38bdf8\" stroke-width=\"2.5\" points=\"").Append(BuildPolyline(p => p.Ctl)).AppendLine("\"/>");
        sb.Append("        <polyline fill=\"none\" stroke=\"#f59e0b\" stroke-width=\"2\" points=\"").Append(BuildPolyline(p => p.Atl)).AppendLine("\"/>");
        sb.Append("        <polyline fill=\"none\" stroke=\"#10b981\" stroke-width=\"2\" stroke-dasharray=\"4 3\" points=\"").Append(BuildPolyline(p => p.Tsb)).AppendLine("\"/>");

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var x = pad + (i * (w - (2 * pad)) / Math.Max(1, points.Count - 1));
            var yTsb = h - pad - (((p.Tsb - minVal) / range) * (h - (2 * pad)));
            var yCtl = h - pad - (((p.Ctl - minVal) / range) * (h - (2 * pad)));
            var tip = string.Create(
                CultureInfo.InvariantCulture,
                $"{p.Date:yyyy-MM-dd}\nCTL: {p.Ctl:F1} | ATL: {p.Atl:F1} | TSB: {p.Tsb:+0.0;-0.0;0.0}");
            sb.Append("        <circle class=\"svg-dot\" cx=\"").Append(x).Append("\" cy=\"").Append(yCtl.ToString("F1", CultureInfo.InvariantCulture))
                .Append("\" r=\"3.2\" fill=\"#38bdf8\" data-tip=\"").Append(Enc(tip)).AppendLine("\"/>");
            sb.Append("        <circle class=\"svg-dot\" cx=\"").Append(x).Append("\" cy=\"").Append(yTsb.ToString("F1", CultureInfo.InvariantCulture))
                .Append("\" r=\"3.2\" fill=\"#10b981\" data-tip=\"").Append(Enc(tip)).AppendLine("\"/>");
        }
        sb.AppendLine("      </svg>");
    }

    private static void AppendWeeklyVolumeSvg(StringBuilder sb, IReadOnlyList<WeeklyTrendSummary> trends)
    {
        if (trends.Count == 0)
        {
            sb.AppendLine("      <p class=\"subtitle\">Noch keine Woechentrends vorhanden.</p>");
            return;
        }

        const int w = 520;
        const int h = 160;
        const int padBottom = 24;
        var maxKm = Math.Max(5.0, trends.Max(t => t.TotalDistanceKm));
        var barW = Math.Max(12, (w - 20) / Math.Max(1, trends.Count) - 6);

        sb.Append("      <svg viewBox=\"0 0 ").Append(w).Append(' ').Append(h).AppendLine("\" role=\"img\" aria-label=\"Wochenvolumen\">");
        for (var i = 0; i < trends.Count; i++)
        {
            var t = trends[i];
            var x = 10 + (i * (barW + 6));
            var barH = (t.TotalDistanceKm / maxKm) * (h - padBottom - 16);
            var y = h - padBottom - barH;
            var fill = (t.Z1Z2Percentage ?? 0.0) >= 80.0 ? "#10b981" : "#38bdf8";
            var tip = string.Create(CultureInfo.InvariantCulture, $"{t.IsoWeek}: {t.TotalDistanceKm:F1} km ({t.RunsCount} Laeufe)");
            sb.Append("        <rect class=\"svg-bar\" x=\"").Append(x).Append("\" y=\"").Append(y.ToString("F1", CultureInfo.InvariantCulture))
                .Append("\" width=\"").Append(barW).Append("\" height=\"").Append(barH.ToString("F1", CultureInfo.InvariantCulture))
                .Append("\" rx=\"4\" fill=\"").Append(fill).Append("\" data-week=\"").Append(Enc(t.IsoWeek))
                .Append("\" data-tip=\"").Append(Enc(tip)).Append("\"></rect>");
        }
        sb.AppendLine("      </svg>");
    }

    private static void AppendEmbeddedDataScript(StringBuilder sb, DashboardSnapshot snapshot)
    {
        var payload = new
        {
            referenceDate = snapshot.ReferenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            runs = snapshot.AllRuns,
            fitnessTimeline = snapshot.ReportSnapshot.FitnessForm.Timeline,
            latestForm = new
            {
                ctl = snapshot.ReportSnapshot.FitnessForm.LatestCtl,
                atl = snapshot.ReportSnapshot.FitnessForm.LatestAtl,
                tsb = snapshot.ReportSnapshot.FitnessForm.CurrentTsb,
                level = snapshot.ReportSnapshot.FitnessForm.TsbLevel
            },
            volumeForecast = snapshot.VolumeForecast,
            zonePaceSeries = snapshot.ZonePaceTrends.Series
        };

        var json = JsonSerializer.Serialize(payload, EmbeddedJsonOptions);
        sb.Append("<script id=\"dashboard-data\" type=\"application/json\">");
        sb.Append(json);
        sb.AppendLine("</script>");
    }

    private static void AppendScript(StringBuilder sb)
    {
        sb.AppendLine("""
              <script>
                (function () {
                  var SVG_NS = 'http://www.w3.org/2000/svg';
                  var dataEl = document.getElementById('dashboard-data');
                  if (!dataEl) return;
                  var data = JSON.parse(dataEl.textContent || '{}');
                  var allRuns = data.runs || [];
                  var refDateStr = data.referenceDate || '';
                  var state = {
                    range: 'all',
                    customStart: null,
                    customEnd: null,
                    activeWeek: null,
                    zoneFilter: 'all',
                    searchQuery: '',
                    efMode: 'base',
                    zpMode: 'rolling',
                    zpZones: { z1: false, z2: true, z3: true, z4: true, z5: false }
                  };

                  var tooltip = document.getElementById('chart-tooltip');
                  document.addEventListener('mousemove', function (e) {
                    var target = e.target && e.target.closest ? e.target.closest('[data-tip]') : null;
                    if (!target || !tooltip) {
                      if (tooltip) tooltip.hidden = true;
                      return;
                    }
                    tooltip.textContent = target.getAttribute('data-tip') || '';
                    tooltip.hidden = false;
                    var left = Math.max(10, Math.min(window.innerWidth - 305, e.clientX + 14));
                    var top = Math.max(10, e.clientY - 46);
                    tooltip.style.left = left + 'px';
                    tooltip.style.top = top + 'px';
                  });
                  document.addEventListener('mouseleave', function () {
                    if (tooltip) tooltip.hidden = true;
                  });
                  document.addEventListener('click', function (e) {
                    var bar = e.target && e.target.closest ? e.target.closest('[data-week]') : null;
                    if (!bar) return;
                    var wk = bar.getAttribute('data-week');
                    state.activeWeek = state.activeWeek === wk ? null : wk;
                    renderAll();
                  });

                  function shiftDate(iso, deltaDays) {
                    var p = iso.split('-');
                    var dt = new Date(Date.UTC(+p[0], +p[1] - 1, +p[2] + deltaDays));
                    return dt.toISOString().slice(0, 10);
                  }

                  function getWindowBounds() {
                    if (!refDateStr) return { start: null, end: null, label: 'Alle Aktivitaeten' };
                    if (state.range === '12w') return { start: shiftDate(refDateStr, -83), end: refDateStr, label: 'Letzte 12 Wochen' };
                    if (state.range === '4w') return { start: shiftDate(refDateStr, -27), end: refDateStr, label: 'Letzte 4 Wochen' };
                    if (state.range === 'month') return { start: refDateStr.slice(0, 7) + '-01', end: refDateStr, label: 'Dieser Monat' };
                    if (state.range === 'custom') {
                      var s = state.customStart || null;
                      var e = state.customEnd || refDateStr;
                      return { start: s, end: e, label: (s || 'Start') + ' bis ' + e };
                    }
                    return { start: null, end: null, label: 'Alle Aktivitaeten' };
                  }

                  function inBounds(dateStr, b) {
                    if (b.start && dateStr < b.start) return false;
                    if (b.end && dateStr > b.end) return false;
                    return true;
                  }

                  function fmtDur(secs) {
                    var h = Math.floor(secs / 3600);
                    var m = Math.floor((secs % 3600) / 60);
                    return h > 0 ? h + 'h ' + (m < 10 ? '0' : '') + m + 'm' : m + 'm';
                  }

                  function fmtPace(secPerKm) {
                    if (!secPerKm || !isFinite(secPerKm) || secPerKm <= 0) return '-:-- /km';
                    var tot = Math.round(secPerKm);
                    var m = Math.floor(tot / 60);
                    var s = tot % 60;
                    return m + ':' + (s < 10 ? '0' : '') + s + ' /km';
                  }

                  function elSvg(tag, attrs, parent) {
                    var node = document.createElementNS(SVG_NS, tag);
                    if (attrs) {
                      for (var k in attrs) {
                        if (Object.prototype.hasOwnProperty.call(attrs, k)) {
                          node.setAttribute(k, String(attrs[k]));
                        }
                      }
                    }
                    if (parent) parent.appendChild(node);
                    return node;
                  }

                  function setText(id, val) {
                    var n = document.getElementById(id);
                    if (n) n.textContent = val;
                  }

                  function groupWeekly(runs) {
                    var map = {};
                    var order = [];
                    var sorted = runs.slice().sort(function (a, b) { return a.date.localeCompare(b.date); });
                    for (var i = 0; i < sorted.length; i++) {
                      var r = sorted[i];
                      var w = r.isoWeek;
                      if (!map[w]) {
                        map[w] = { isoWeek: w, runs: 0, km: 0, load: 0, sec: 0, z1z2Sec: 0, totZSec: 0, zones: [0, 0, 0, 0, 0] };
                        order.push(map[w]);
                      }
                      var g = map[w];
                      g.runs += 1;
                      g.km += r.distanceKm || 0;
                      g.load += r.trainingLoad || 0;
                      g.sec += r.movingTimeSeconds || 0;
                      g.z1z2Sec += r.z1Z2Seconds || 0;
                      g.totZSec += r.totalZoneSeconds || 0;
                      var zt = r.zoneTimes || [];
                      for (var z = 0; z < 4; z++) g.zones[z] += zt[z] || 0;
                      for (var z5 = 4; z5 < zt.length; z5++) g.zones[4] += zt[z5] || 0;
                    }
                    return order;
                  }

                  function updateKpis(runs, bounds) {
                    var totKm = 0, totSec = 0, hrWeight = 0, hrSec = 0, z12 = 0, zTot = 0;
                    var baseEfs = [], allEfs = [];
                    for (var i = 0; i < runs.length; i++) {
                      var r = runs[i];
                      totKm += r.distanceKm || 0;
                      totSec += r.movingTimeSeconds || 0;
                      if (r.averageHeartRate > 0) {
                        hrWeight += r.averageHeartRate * r.movingTimeSeconds;
                        hrSec += r.movingTimeSeconds;
                      }
                      z12 += r.z1Z2Seconds || 0;
                      zTot += r.totalZoneSeconds || 0;
                      if (r.aerobicEf !== null && r.aerobicEf !== undefined) {
                        allEfs.push(r.aerobicEf);
                        if (r.z1Z2Percentage !== null && r.z1Z2Percentage >= 80) baseEfs.push(r.aerobicEf);
                      }
                    }
                    setText('kpi-dist', totKm.toFixed(1) + ' km');
                    setText('kpi-runs', runs.length + ' Laeufe');
                    setText('kpi-pace', totKm > 0 ? fmtPace(totSec / totKm) : '-:-- /km');
                    setText('kpi-time', 'Laufzeit ' + fmtDur(totSec));
                    setText('kpi-hr', hrSec > 0 ? Math.round(hrWeight / hrSec) + ' bpm' : '-');
                    setText('kpi-z1z2', 'Z1/Z2 Anteil ' + (zTot > 0 ? (z12 / zTot * 100).toFixed(1) : '0.0') + '%');
                    var efs = baseEfs.length > 0 ? baseEfs : allEfs;
                    var aefSum = 0;
                    for (var j = 0; j < efs.length; j++) aefSum += efs[j];
                    setText('kpi-aef', efs.length > 0 ? (aefSum / efs.length).toFixed(3) : '-');

                    if (!state.activeWeek && state.range === 'all' && data.latestForm) {
                      var f = data.latestForm;
                      setText('kpi-ctl-atl', 'CTL ' + f.ctl.toFixed(1) + ' / ATL ' + f.atl.toFixed(1));
                      setText('kpi-tsb', 'TSB ' + (f.tsb >= 0 ? '+' : '') + f.tsb.toFixed(1) + ' (' + f.level + ')');
                    } else {
                      var tl = (data.fitnessTimeline || []).filter(function (pt) {
                        return inBounds(pt.date, bounds);
                      });
                      if (tl.length > 0) {
                        var last = tl[tl.length - 1];
                        setText('kpi-ctl-atl', 'CTL ' + last.ctl.toFixed(1) + ' / ATL ' + last.atl.toFixed(1));
                        setText('kpi-tsb', 'TSB ' + (last.tsb >= 0 ? '+' : '') + last.tsb.toFixed(1) + ' (' + last.date + ')');
                      }
                    }
                  }

                  function renderChartEmpty(host, msg) {
                    host.innerHTML = '';
                    var p = document.createElement('p');
                    p.className = 'subtitle';
                    p.style.padding = '36px 8px';
                    p.textContent = msg;
                    host.appendChild(p);
                  }

                  function renderWeeklyVolumeChart(runs) {
                    var host = document.getElementById('chart-weekly-volume');
                    if (!host) return;
                    var weeks = groupWeekly(runs);
                    var items = weeks.map(function (w) {
                      var zPct = w.totZSec > 0 ? (w.z1z2Sec / w.totZSec * 100) : 0;
                      return { week: w.isoWeek, km: w.km, load: w.load, runs: w.runs, zPct: zPct, forecast: false };
                    });
                    if (state.range === 'all' && !state.activeWeek && data.volumeForecast) {
                      for (var fi = 0; fi < data.volumeForecast.length; fi++) {
                        var fv = data.volumeForecast[fi];
                        items.push({ week: fv.isoWeek, km: fv.projectedDistanceKm, load: fv.projectedLoad, runs: 0, zPct: 80, forecast: true, phase: fv.phaseTitle });
                      }
                    }
                    if (items.length === 0) return renderChartEmpty(host, 'Keine Wochendaten im Zeitraum.');
                    host.innerHTML = '';
                    var w = 540, h = 195, padL = 34, padR = 14, padT = 16, padB = 26;
                    var svg = elSvg('svg', { viewBox: '0 0 ' + w + ' ' + h, role: 'img' }, host);
                    var maxKm = 5, maxLoad = 40;
                    for (var i = 0; i < items.length; i++) {
                      if (items[i].km > maxKm) maxKm = items[i].km;
                      if (items[i].load > maxLoad) maxLoad = items[i].load;
                    }
                    var plotW = w - padL - padR, plotH = h - padT - padB;
                    for (var g = 0; g <= 4; g++) {
                      var gy = padT + (plotH * g / 4);
                      elSvg('line', { x1: padL, y1: gy, x2: w - padR, y2: gy, stroke: '#1e293b', 'stroke-width': 1 }, svg);
                      var lbl = elSvg('text', { x: padL - 5, y: gy + 3, fill: '#94a3b8', 'font-size': 9, 'text-anchor': 'end' }, svg);
                      lbl.textContent = (maxKm * (1 - g / 4)).toFixed(0);
                    }
                    var step = plotW / items.length;
                    var bw = Math.max(8, Math.min(34, step * 0.72));
                    var loadPts = [];
                    for (var k = 0; k < items.length; k++) {
                      var it = items[k];
                      var cx = padL + (k + 0.5) * step;
                      var bh = (it.km / maxKm) * plotH;
                      var by = padT + plotH - bh;
                      var fill = it.forecast ? 'rgba(56,189,248,0.22)' : (it.zPct >= 80 ? '#10b981' : '#38bdf8');
                      var cls = it.forecast ? 'svg-bar' : 'svg-bar' + (state.activeWeek ? (state.activeWeek === it.week ? ' selected' : ' dimmed') : '');
                      var tip = it.forecast
                        ? (it.week + ' (' + it.phase + ')\nPrognose: ' + it.km.toFixed(1) + ' km | Load: ' + Math.round(it.load))
                        : (it.week + '\nDistanz: ' + it.km.toFixed(1) + ' km (' + it.runs + ' Laeufe)\nZ1/Z2: ' + it.zPct.toFixed(1) + '% | Load: ' + Math.round(it.load));
                      var attrs = { 'class': cls, x: (cx - bw / 2).toFixed(1), y: by.toFixed(1), width: bw.toFixed(1), height: Math.max(1, bh).toFixed(1), rx: 3, fill: fill, 'data-tip': tip };
                      if (it.forecast) {
                        attrs.stroke = '#38bdf8';
                        attrs['stroke-dasharray'] = '3 2';
                      } else {
                        attrs['data-week'] = it.week;
                      }
                      elSvg('rect', attrs, svg);
                      var ly = padT + plotH - ((it.load / maxLoad) * plotH);
                      loadPts.push(cx.toFixed(1) + ',' + ly.toFixed(1));
                      if (k % Math.max(1, Math.ceil(items.length / 7)) === 0 || k === items.length - 1) {
                        var xt = elSvg('text', { x: cx.toFixed(1), y: h - 7, fill: '#94a3b8', 'font-size': 8.5, 'text-anchor': 'middle' }, svg);
                        xt.textContent = it.week.slice(5);
                      }
                    }
                    elSvg('polyline', { fill: 'none', stroke: '#f59e0b', 'stroke-width': 1.8, points: loadPts.join(' ') }, svg);
                  }

                  function renderZoneDistChart(runs) {
                    var host = document.getElementById('chart-zone-dist');
                    if (!host) return;
                    var weeks = groupWeekly(runs).filter(function (wk) { return (wk.zones[0] + wk.zones[1] + wk.zones[2] + wk.zones[3] + wk.zones[4]) > 0; });
                    if (weeks.length === 0) return renderChartEmpty(host, 'Keine HF-Zonendaten im Zeitraum.');
                    host.innerHTML = '';
                    var w = 540, h = 195, padL = 34, padR = 12, padT = 14, padB = 26;
                    var svg = elSvg('svg', { viewBox: '0 0 ' + w + ' ' + h, role: 'img' }, host);
                    var plotW = w - padL - padR, plotH = h - padT - padB;
                    var colors = ['#94a3b8', '#10b981', '#f59e0b', '#f97316', '#f43f5e'];
                    for (var g = 0; g <= 4; g++) {
                      var gy = padT + (plotH * g / 4);
                      elSvg('line', { x1: padL, y1: gy, x2: w - padR, y2: gy, stroke: '#1e293b', 'stroke-width': 1 }, svg);
                      var lbl = elSvg('text', { x: padL - 5, y: gy + 3, fill: '#94a3b8', 'font-size': 9, 'text-anchor': 'end' }, svg);
                      lbl.textContent = (100 - g * 25) + '%';
                    }
                    var step = plotW / weeks.length;
                    var bw = Math.max(8, Math.min(34, step * 0.72));
                    for (var i = 0; i < weeks.length; i++) {
                      var wk = weeks[i];
                      var sum = wk.zones[0] + wk.zones[1] + wk.zones[2] + wk.zones[3] + wk.zones[4];
                      var cx = padL + (i + 0.5) * step;
                      var pcts = wk.zones.map(function (v) { return (v / sum) * 100; });
                      var tip = wk.isoWeek + '\nZ1: ' + pcts[0].toFixed(0) + '% | Z2: ' + pcts[1].toFixed(0) + '% (Z1+Z2: ' + (pcts[0] + pcts[1]).toFixed(1) + '%)\nZ3: ' + pcts[2].toFixed(0) + '% | Z4: ' + pcts[3].toFixed(0) + '% | Z5+: ' + pcts[4].toFixed(0) + '%';
                      var curY = padT + plotH;
                      var cls = 'svg-bar' + (state.activeWeek ? (state.activeWeek === wk.isoWeek ? ' selected' : ' dimmed') : '');
                      for (var z = 0; z < 5; z++) {
                        if (pcts[z] <= 0) continue;
                        var segH = (pcts[z] / 100) * plotH;
                        curY -= segH;
                        elSvg('rect', { 'class': cls, x: (cx - bw / 2).toFixed(1), y: curY.toFixed(1), width: bw.toFixed(1), height: Math.max(1, segH).toFixed(1), fill: colors[z], 'data-week': wk.isoWeek, 'data-tip': tip }, svg);
                      }
                      if (i % Math.max(1, Math.ceil(weeks.length / 7)) === 0 || i === weeks.length - 1) {
                        var xt = elSvg('text', { x: cx.toFixed(1), y: h - 7, fill: '#94a3b8', 'font-size': 8.5, 'text-anchor': 'middle' }, svg);
                        xt.textContent = wk.isoWeek.slice(5);
                      }
                    }
                    var y80 = padT + plotH * 0.2;
                    elSvg('line', { x1: padL, y1: y80, x2: w - padR, y2: y80, stroke: '#10b981', 'stroke-dasharray': '4 3', 'stroke-width': 1.3 }, svg);
                  }

                  function renderFormHistoryChart(bounds) {
                    var host = document.getElementById('chart-form-history');
                    if (!host) return;
                    var pts = (data.fitnessTimeline || []).filter(function (p) { return inBounds(p.date, bounds); });
                    if (pts.length === 0) return renderChartEmpty(host, 'Keine Form-Daten im Zeitraum.');
                    host.innerHTML = '';
                    var w = 540, h = 195, padL = 34, padR = 14, padT = 14, padB = 26;
                    var svg = elSvg('svg', { viewBox: '0 0 ' + w + ' ' + h, role: 'img' }, host);
                    var minV = 0, maxV = 15;
                    for (var i = 0; i < pts.length; i++) {
                      minV = Math.min(minV, pts[i].tsb);
                      maxV = Math.max(maxV, pts[i].ctl, pts[i].atl);
                    }
                    var span = Math.max(10, maxV - minV);
                    var plotW = w - padL - padR, plotH = h - padT - padB;
                    var yZero = padT + plotH - ((0 - minV) / span) * plotH;
                    elSvg('line', { x1: padL, y1: yZero.toFixed(1), x2: w - padR, y2: yZero.toFixed(1), stroke: '#475569', 'stroke-dasharray': '3 3', 'stroke-width': 1 }, svg);
                    var ctlPts = [], atlPts = [], tsbPts = [];
                    for (var k = 0; k < pts.length; k++) {
                      var p = pts[k];
                      var x = padL + (pts.length === 1 ? plotW / 2 : (k / (pts.length - 1)) * plotW);
                      var yCtl = padT + plotH - ((p.ctl - minV) / span) * plotH;
                      var yAtl = padT + plotH - ((p.atl - minV) / span) * plotH;
                      var yTsb = padT + plotH - ((p.tsb - minV) / span) * plotH;
                      ctlPts.push(x.toFixed(1) + ',' + yCtl.toFixed(1));
                      atlPts.push(x.toFixed(1) + ',' + yAtl.toFixed(1));
                      tsbPts.push(x.toFixed(1) + ',' + yTsb.toFixed(1));
                    }
                    elSvg('polyline', { fill: 'none', stroke: '#38bdf8', 'stroke-width': 2.1, points: ctlPts.join(' ') }, svg);
                    elSvg('polyline', { fill: 'none', stroke: '#f59e0b', 'stroke-width': 1.8, points: atlPts.join(' ') }, svg);
                    elSvg('polyline', { fill: 'none', stroke: '#10b981', 'stroke-width': 1.8, points: tsbPts.join(' ') }, svg);
                    for (var m = 0; m < pts.length; m++) {
                      var pm = pts[m];
                      var mx = padL + (pts.length === 1 ? plotW / 2 : (m / (pts.length - 1)) * plotW);
                      var myCtl = padT + plotH - ((pm.ctl - minV) / span) * plotH;
                      var myTsb = padT + plotH - ((pm.tsb - minV) / span) * plotH;
                      var tip = pm.date + ' (' + pm.name + ')\nCTL: ' + pm.ctl.toFixed(1) + ' | ATL: ' + pm.atl.toFixed(1) + ' | TSB: ' + (pm.tsb >= 0 ? '+' : '') + pm.tsb.toFixed(1);
                      elSvg('circle', { 'class': 'svg-dot', cx: mx.toFixed(1), cy: myCtl.toFixed(1), r: 2.8, fill: '#38bdf8', 'data-tip': tip }, svg);
                      elSvg('circle', { 'class': 'svg-dot', cx: mx.toFixed(1), cy: myTsb.toFixed(1), r: 2.6, fill: '#10b981', 'data-tip': tip }, svg);
                      if (m === 0 || m === Math.floor(pts.length / 2) || m === pts.length - 1) {
                        var xl = elSvg('text', { x: mx.toFixed(1), y: h - 7, fill: '#94a3b8', 'font-size': 8.5, 'text-anchor': m === 0 ? 'start' : (m === pts.length - 1 ? 'end' : 'middle') }, svg);
                        xl.textContent = pm.date.slice(2);
                      }
                    }
                    var lblTop = elSvg('text', { x: padL - 5, y: padT + 4, fill: '#94a3b8', 'font-size': 9, 'text-anchor': 'end' }, svg);
                    lblTop.textContent = maxV.toFixed(0);
                    var lblBot = elSvg('text', { x: padL - 5, y: padT + plotH, fill: '#94a3b8', 'font-size': 9, 'text-anchor': 'end' }, svg);
                    lblBot.textContent = minV.toFixed(0);
                  }

                  function renderEfPaceChart(runs) {
                    var host = document.getElementById('chart-ef-pace');
                    if (!host) return;
                    var pts = runs.filter(function (r) {
                      if (r.aerobicEf === null || r.aerobicEf === undefined || r.paceSecondsPerKm <= 0) return false;
                      if (state.efMode === 'base') return (r.z1Z2Percentage || 0) >= 80;
                      return true;
                    }).slice().sort(function (a, b) { return a.date.localeCompare(b.date); });
                    setText('ef-chart-sub', (state.efMode === 'base' ? 'Nur Z2-Basis (>=80% Z1/Z2)' : 'Alle Laeufe') + ' · ' + pts.length + ' Laeufe');
                    if (pts.length === 0) return renderChartEmpty(host, 'Keine passenden Laeufe im ausgewaehlten Modus/Zeitraum.');
                    host.innerHTML = '';
                    var w = 540, h = 195, padL = 38, padR = 42, padT = 14, padB = 26;
                    var svg = elSvg('svg', { viewBox: '0 0 ' + w + ' ' + h, role: 'img' }, host);
                    var minEf = 9, maxEf = 0, minPace = 9999, maxPace = 0;
                    for (var i = 0; i < pts.length; i++) {
                      minEf = Math.min(minEf, pts[i].aerobicEf);
                      maxEf = Math.max(maxEf, pts[i].aerobicEf);
                      minPace = Math.min(minPace, pts[i].paceSecondsPerKm);
                      maxPace = Math.max(maxPace, pts[i].paceSecondsPerKm);
                    }
                    var efSpan = Math.max(0.08, (maxEf - minEf) * 1.15);
                    var paceSpan = Math.max(30, (maxPace - minPace) * 1.15);
                    var plotW = w - padL - padR, plotH = h - padT - padB;
                    var efPts = [], pacePts = [];
                    for (var k = 0; k < pts.length; k++) {
                      var p = pts[k];
                      var x = padL + (pts.length === 1 ? plotW / 2 : (k / (pts.length - 1)) * plotW);
                      var yEf = padT + plotH - ((p.aerobicEf - minEf) / efSpan) * plotH;
                      var yPace = padT + plotH - ((maxPace - p.paceSecondsPerKm) / paceSpan) * plotH;
                      efPts.push(x.toFixed(1) + ',' + yEf.toFixed(1));
                      pacePts.push(x.toFixed(1) + ',' + yPace.toFixed(1));
                    }
                    elSvg('polyline', { fill: 'none', stroke: '#10b981', 'stroke-width': 1.6, 'stroke-opacity': 0.85, points: pacePts.join(' ') }, svg);
                    elSvg('polyline', { fill: 'none', stroke: '#38bdf8', 'stroke-width': 2.2, points: efPts.join(' ') }, svg);
                    for (var m = 0; m < pts.length; m++) {
                      var r = pts[m];
                      var mx = padL + (pts.length === 1 ? plotW / 2 : (m / (pts.length - 1)) * plotW);
                      var myEf = padT + plotH - ((r.aerobicEf - minEf) / efSpan) * plotH;
                      var tip = r.date + ' - ' + r.name + '\nAEF: ' + r.aerobicEf.toFixed(3) + ' | Pace: ' + r.paceText + '\nHF: ' + Math.round(r.averageHeartRate) + ' bpm | Z1/Z2: ' + (r.z1Z2Percentage || 0).toFixed(1) + '%';
                      elSvg('circle', { 'class': 'svg-dot', cx: mx.toFixed(1), cy: myEf.toFixed(1), r: 3.1, fill: '#38bdf8', 'data-tip': tip }, svg);
                      if (m === 0 || m === pts.length - 1) {
                        var xl = elSvg('text', { x: mx.toFixed(1), y: h - 7, fill: '#94a3b8', 'font-size': 8.5, 'text-anchor': m === 0 ? 'start' : 'end' }, svg);
                        xl.textContent = r.date.slice(2);
                      }
                    }
                    var efTop = elSvg('text', { x: padL - 5, y: padT + 4, fill: '#38bdf8', 'font-size': 9, 'text-anchor': 'end' }, svg);
                    efTop.textContent = (minEf + efSpan).toFixed(2);
                    var efBot = elSvg('text', { x: padL - 5, y: padT + plotH, fill: '#38bdf8', 'font-size': 9, 'text-anchor': 'end' }, svg);
                    efBot.textContent = minEf.toFixed(2);
                  }

                  function renderZonePaceChart(bounds) {
                    var host = document.getElementById('chart-zone-pace');
                    if (!host) return;
                    var seriesMap = data.zonePaceSeries || {};
                    var colors = { z1: '#94a3b8', z2: '#10b981', z3: '#f59e0b', z4: '#f97316', z5: '#f43f5e' };
                    var activeSeries = [];
                    var allDates = {};
                    var minSec = 9999, maxSec = 0;
                    ['z1', 'z2', 'z3', 'z4', 'z5'].forEach(function (zk) {
                      if (!state.zpZones[zk]) return;
                      var rawPts = (seriesMap[zk] || []).filter(function (pt) { return inBounds(pt.date, bounds); });
                      if (rawPts.length === 0) return;
                      activeSeries.push({ key: zk, points: rawPts });
                      rawPts.forEach(function (pt) {
                        var val = state.zpMode === 'rolling' ? pt.rollingGapPaceSecondsPerKm : pt.gapPaceSecondsPerKm;
                        minSec = Math.min(minSec, val);
                        maxSec = Math.max(maxSec, val);
                        allDates[pt.date] = true;
                      });
                    });
                    if (activeSeries.length === 0) return renderChartEmpty(host, 'Keine Zone-GAP-Daten fuer die ausgewaehlten Zonen/Zeitraum.');
                    host.innerHTML = '';
                    var dates = Object.keys(allDates).sort();
                    var w = 960, h = 230, padL = 54, padR = 20, padT = 16, padB = 28;
                    var svg = elSvg('svg', { viewBox: '0 0 ' + w + ' ' + h, role: 'img' }, host);
                    var span = Math.max(20, maxSec - minSec);
                    minSec = Math.max(150, minSec - span * 0.08);
                    maxSec = maxSec + span * 0.08;
                    var fullSpan = Math.max(20, maxSec - minSec);
                    var plotW = w - padL - padR, plotH = h - padT - padB;
                    for (var g = 0; g <= 4; g++) {
                      var gy = padT + (plotH * g / 4);
                      var pVal = minSec + (fullSpan * g / 4);
                      elSvg('line', { x1: padL, y1: gy.toFixed(1), x2: w - padR, y2: gy.toFixed(1), stroke: '#1e293b', 'stroke-width': 1 }, svg);
                      var yl = elSvg('text', { x: padL - 6, y: gy + 3, fill: '#94a3b8', 'font-size': 9.5, 'text-anchor': 'end' }, svg);
                      yl.textContent = fmtPace(pVal).replace(' /km', '');
                    }
                    var dateIdx = {};
                    for (var d = 0; d < dates.length; d++) dateIdx[dates[d]] = d;
                    activeSeries.forEach(function (s) {
                      var polyPts = [];
                      var col = colors[s.key] || '#38bdf8';
                      s.points.forEach(function (pt) {
                        var idx = dateIdx[pt.date] || 0;
                        var x = padL + (dates.length === 1 ? plotW / 2 : (idx / (dates.length - 1)) * plotW);
                        var sec = state.zpMode === 'rolling' ? pt.rollingGapPaceSecondsPerKm : pt.gapPaceSecondsPerKm;
                        var y = padT + ((sec - minSec) / fullSpan) * plotH;
                        polyPts.push(x.toFixed(1) + ',' + y.toFixed(1));
                        var tip = s.key.toUpperCase() + ' | ' + pt.date + ' - ' + pt.name + '\nGAP: ' + pt.gapPaceText + ' (Raw: ' + pt.rawPaceText + ')\nØ HF: ' + pt.averageHeartRate.toFixed(1) + ' bpm | ' + Math.round(pt.runningSeconds / 60) + 'm';
                        elSvg('circle', { 'class': 'svg-dot', cx: x.toFixed(1), cy: y.toFixed(1), r: 3.1, fill: col, 'data-tip': tip }, svg);
                      });
                      elSvg('polyline', { fill: 'none', stroke: col, 'stroke-width': 2.1, points: polyPts.join(' ') }, svg);
                    });
                    if (dates.length > 0) {
                      var x0 = elSvg('text', { x: padL, y: h - 7, fill: '#94a3b8', 'font-size': 9 }, svg);
                      x0.textContent = dates[0];
                      if (dates.length > 1) {
                        var xN = elSvg('text', { x: w - padR, y: h - 7, fill: '#94a3b8', 'font-size': 9, 'text-anchor': 'end' }, svg);
                        xN.textContent = dates[dates.length - 1];
                      }
                    }
                  }

                  function renderTable(dateWeekRuns) {
                    var tbody = document.getElementById('runs-tbody');
                    if (!tbody || allRuns.length === 0) return;
                    var q = (state.searchQuery || '').toLowerCase();
                    var visible = dateWeekRuns.filter(function (r) {
                      if (state.zoneFilter !== 'all' && r.intensityBucket !== state.zoneFilter) return false;
                      if (!q) return true;
                      var hay = (r.date + ' ' + r.name + ' ' + (r.athleteNote || '') + ' ' + r.ratingLabel).toLowerCase();
                      return hay.indexOf(q) !== -1;
                    });
                    setText('runs-count-summary', 'Zeige ' + visible.length + ' von ' + allRuns.length + ' Laeufen');
                    tbody.innerHTML = '';
                    if (visible.length === 0) {
                      var trEmpty = document.createElement('tr');
                      var tdEmpty = document.createElement('td');
                      tdEmpty.colSpan = 7;
                      tdEmpty.className = 'subtitle';
                      tdEmpty.textContent = 'Keine Laeufe fuer den aktuellen Filter gefunden.';
                      trEmpty.appendChild(tdEmpty);
                      tbody.appendChild(trEmpty);
                      return;
                    }
                    visible.forEach(function (r) {
                      var tr = document.createElement('tr');
                      tr.className = 'run-row';
                      var tdDate = document.createElement('td'); tdDate.textContent = r.date; tr.appendChild(tdDate);
                      var tdName = document.createElement('td');
                      var st = document.createElement('strong'); st.textContent = r.name; tdName.appendChild(st);
                      if (r.athleteNote) {
                        var nd = document.createElement('div'); nd.className = 'subtitle'; nd.textContent = 'Notiz: ' + r.athleteNote; tdName.appendChild(nd);
                      }
                      tr.appendChild(tdName);
                      var tdDist = document.createElement('td'); tdDist.textContent = r.distanceKm.toFixed(2) + ' km'; tr.appendChild(tdDist);
                      var tdTime = document.createElement('td'); tdTime.textContent = r.movingTimeText; tr.appendChild(tdTime);
                      var tdPace = document.createElement('td'); tdPace.textContent = r.paceText;
                      var subHr = document.createElement('div'); subHr.className = 'subtitle';
                      subHr.textContent = r.averageHeartRate > 0 ? Math.round(r.averageHeartRate) + ' bpm' : '-';
                      tdPace.appendChild(subHr); tr.appendChild(tdPace);
                      var tdZ = document.createElement('td');
                      tdZ.textContent = r.z1Z2Percentage !== null && r.z1Z2Percentage !== undefined ? r.z1Z2Percentage.toFixed(1) + '%' : '-';
                      if (r.z2GapPaceText) {
                        var subGap = document.createElement('div'); subGap.className = 'subtitle'; subGap.textContent = 'Z2 GAP ' + r.z2GapPaceText; tdZ.appendChild(subGap);
                      }
                      tr.appendChild(tdZ);
                      var tdRat = document.createElement('td');
                      if (r.directive) {
                        var det = document.createElement('details');
                        var sum = document.createElement('summary'); sum.textContent = r.ratingLabel; det.appendChild(sum);
                        var p = document.createElement('p'); p.className = 'subtitle'; p.style.marginTop = '4px'; p.textContent = r.directive; det.appendChild(p);
                        tdRat.appendChild(det);
                      } else {
                        tdRat.textContent = r.ratingLabel;
                      }
                      tr.appendChild(tdRat);
                      tbody.appendChild(tr);
                    });
                  }

                  function renderAll() {
                    var bounds = getWindowBounds();
                    var dateRuns = allRuns.filter(function (r) { return inBounds(r.date, bounds); });
                    var activeRuns = state.activeWeek
                      ? dateRuns.filter(function (r) { return r.isoWeek === state.activeWeek; })
                      : dateRuns;

                    var badgeText = bounds.label + (state.activeWeek ? ' · ' + state.activeWeek : '') + ' (' + activeRuns.length + ' Laeufe)';
                    setText('active-filter-text', badgeText);
                    var resetWeekBtn = document.getElementById('btn-reset-week');
                    if (resetWeekBtn) resetWeekBtn.hidden = !state.activeWeek;

                    updateKpis(activeRuns, bounds);
                    renderWeeklyVolumeChart(dateRuns);
                    renderZoneDistChart(dateRuns);
                    renderFormHistoryChart(bounds);
                    renderEfPaceChart(activeRuns);
                    renderZonePaceChart(bounds);
                    renderTable(activeRuns);
                  }

                  var rangeBtns = document.querySelectorAll('#date-filter-buttons .filter-btn');
                  rangeBtns.forEach(function (btn) {
                    btn.addEventListener('click', function () {
                      rangeBtns.forEach(function (b) { b.classList.remove('active'); });
                      btn.classList.add('active');
                      state.range = btn.getAttribute('data-range') || 'all';
                      state.activeWeek = null;
                      var customPanel = document.getElementById('custom-date-range');
                      if (customPanel) customPanel.hidden = state.range !== 'custom';
                      renderAll();
                    });
                  });

                  var applyCustom = document.getElementById('btn-apply-custom');
                  if (applyCustom) applyCustom.addEventListener('click', function () {
                    state.customStart = (document.getElementById('filter-start') || {}).value || null;
                    state.customEnd = (document.getElementById('filter-end') || {}).value || null;
                    state.activeWeek = null;
                    renderAll();
                  });

                  var resetFilter = document.getElementById('btn-reset-filter');
                  if (resetFilter) resetFilter.addEventListener('click', function () {
                    state.range = 'all';
                    state.activeWeek = null;
                    rangeBtns.forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-range') === 'all'); });
                    var customPanel = document.getElementById('custom-date-range');
                    if (customPanel) customPanel.hidden = true;
                    renderAll();
                  });

                  var resetWeek = document.getElementById('btn-reset-week');
                  if (resetWeek) resetWeek.addEventListener('click', function () {
                    state.activeWeek = null;
                    renderAll();
                  });

                  var searchInput = document.getElementById('run-filter');
                  if (searchInput) searchInput.addEventListener('input', function () {
                    state.searchQuery = searchInput.value || '';
                    renderAll();
                  });

                  var zoneSelect = document.getElementById('zone-filter');
                  if (zoneSelect) zoneSelect.addEventListener('change', function () {
                    state.zoneFilter = zoneSelect.value || 'all';
                    renderAll();
                  });

                  var efBtns = document.querySelectorAll('[data-ef-mode]');
                  efBtns.forEach(function (btn) {
                    btn.addEventListener('click', function () {
                      efBtns.forEach(function (b) { b.classList.remove('active'); });
                      btn.classList.add('active');
                      state.efMode = btn.getAttribute('data-ef-mode') || 'base';
                      renderAll();
                    });
                  });

                  var zpModeBtns = document.querySelectorAll('[data-zp-mode]');
                  zpModeBtns.forEach(function (btn) {
                    btn.addEventListener('click', function () {
                      zpModeBtns.forEach(function (b) { b.classList.remove('active'); });
                      btn.classList.add('active');
                      state.zpMode = btn.getAttribute('data-zp-mode') || 'rolling';
                      renderAll();
                    });
                  });

                  document.querySelectorAll('[data-zp-zone]').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                      var z = btn.getAttribute('data-zp-zone');
                      if (!z) return;
                      state.zpZones[z] = !state.zpZones[z];
                      btn.classList.toggle('active', !!state.zpZones[z]);
                      renderAll();
                    });
                  });

                  renderAll();
                })();
              </script>
            """);
    }

    private static string SanitizePlannedWorkoutTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Lauf";
        }

        var isQuality = false;
        foreach (var kw in QualityScheduleKeywords)
        {
            if (title.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                isQuality = true;
                break;
            }
        }

        if (isQuality)
        {
            return title;
        }

        var cleaned = PaceTokenRegex().Replace(title, string.Empty).Trim();
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "Aerobe Basis-Einheit (Z2 HR)" : cleaned;
    }

    private static string MapSeverityClass(string severity) => severity.ToLowerInvariant() switch
    {
        "success" => "sev-success",
        "warning" => "sev-warning",
        "danger" => "sev-danger",
        _ => "sev-info"
    };

    private static string FormatDuration(int seconds)
    {
        var hrs = seconds / 3600;
        var mins = (seconds % 3600) / 60;
        return hrs > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hrs}h {mins:D2}m")
            : string.Create(CultureInfo.InvariantCulture, $"{mins}m");
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}
