using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Laufen.Domain;

/// <summary>
/// Pure self-contained static HTML5 dashboard renderer with zero external JS/CDN dependencies,
/// inline SVG charts, full HTML encoding, and strict Coaching DNA aerobic pace hygiene.
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

    [GeneratedRegex(@"\b\d{1,2}:\d{2}(?:\s*-\s*\d{1,2}:\d{2})?\s*(?:/km|min/km)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PaceTokenRegex();

    public static string Render(DashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder(32768);
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
        AppendKpiStrip(sb, snapshot);
        AppendMilestonesSection(sb, snapshot);
        AppendAutopilotScheduleSection(sb, snapshot);
        AppendNarratorSection(sb, snapshot);
        AppendTrendsAndZonePaceSection(sb, snapshot);
        AppendForecastAndZonesSection(sb, snapshot);
        AppendReviewsTableSection(sb, snapshot);

        sb.AppendLine("</div>");
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
                  --border: #1e293b;
                  --text: #f8fafc;
                  --muted: #94a3b8;
                  --cyan: #38bdf8;
                  --emerald: #10b981;
                  --amber: #f59e0b;
                  --rose: #f43f5e;
                }
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body { background: var(--bg); color: var(--text); font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; line-height: 1.5; padding: 24px; }
                .container { max-width: 1240px; margin: 0 auto; display: flex; flex-direction: column; gap: 22px; }
                header { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 14px; padding-bottom: 16px; border-bottom: 1px solid var(--border); }
                h1 { font-size: 1.7rem; font-weight: 700; color: var(--text); }
                h2 { font-size: 1.15rem; font-weight: 600; margin-bottom: 12px; color: var(--text); }
                h3 { font-size: 0.98rem; font-weight: 600; margin-bottom: 8px; color: var(--cyan); }
                .subtitle { color: var(--muted); font-size: 0.9rem; }
                .badge { display: inline-flex; align-items: center; gap: 6px; padding: 4px 11px; border-radius: 999px; font-size: 0.78rem; font-weight: 600; border: 1px solid var(--border); background: rgba(56,189,248,0.12); color: var(--cyan); }
                .sev-success { background: rgba(16,185,129,0.15); color: var(--emerald); border-color: rgba(16,185,129,0.35); }
                .sev-warning { background: rgba(245,158,11,0.15); color: var(--amber); border-color: rgba(245,158,11,0.35); }
                .sev-danger { background: rgba(244,63,94,0.16); color: var(--rose); border-color: rgba(244,63,94,0.38); }
                .sev-info { background: rgba(56,189,248,0.12); color: var(--cyan); border-color: rgba(56,189,248,0.32); }
                .card { background: var(--card); border: 1px solid var(--border); border-radius: 12px; padding: 18px; }
                .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(175px, 1fr)); gap: 14px; }
                .kpi-label { font-size: 0.78rem; color: var(--muted); text-transform: uppercase; letter-spacing: 0.04em; }
                .kpi-value { font-size: 1.5rem; font-weight: 700; margin-top: 4px; }
                .kpi-sub { font-size: 0.8rem; color: var(--muted); margin-top: 2px; }
                .two-col { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 18px; }
                .schedule-list { display: flex; flex-direction: column; gap: 10px; }
                .schedule-item { padding: 10px 12px; border-radius: 8px; background: rgba(15,23,42,0.65); border: 1px solid var(--border); display: flex; justify-content: space-between; gap: 12px; align-items: center; }
                .insight-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 12px; margin-top: 12px; }
                .insight-item { padding: 12px; border-radius: 8px; background: rgba(15,23,42,0.55); border: 1px solid var(--border); }
                table { width: 100%; border-collapse: collapse; font-size: 0.88rem; }
                th, td { text-align: left; padding: 9px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
                th { color: var(--muted); font-weight: 600; font-size: 0.8rem; text-transform: uppercase; }
                .controls { display: flex; gap: 10px; flex-wrap: wrap; margin-bottom: 12px; }
                input[type="search"] { background: #0f172a; color: var(--text); border: 1px solid var(--border); border-radius: 8px; padding: 7px 11px; min-width: 240px; }
                details summary { cursor: pointer; color: var(--cyan); font-size: 0.82rem; }
                .banner { padding: 12px 14px; border-radius: 10px; border: 1px solid rgba(245,158,11,0.35); background: rgba(245,158,11,0.1); font-size: 0.9rem; }
                .zone-list { display: flex; flex-direction: column; gap: 8px; }
                .zone-row { display: flex; justify-content: space-between; padding: 7px 10px; border-radius: 8px; background: rgba(15,23,42,0.5); border: 1px solid var(--border); }
                svg { width: 100%; height: auto; display: block; }
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

    private static void AppendKpiStrip(StringBuilder sb, DashboardSnapshot snapshot)
    {
        var form = snapshot.ReportSnapshot.FitnessForm;
        var aefText = snapshot.BaseAerobicEf.HasValue
            ? snapshot.BaseAerobicEf.Value.ToString("F3", CultureInfo.InvariantCulture)
            : "-";

        sb.AppendLine("  <section class=\"kpi-grid\">");
        AppendKpiCard(sb, "Gesamtdistanz", string.Create(CultureInfo.InvariantCulture, $"{snapshot.TotalDistanceKm:F1} km"), string.Create(CultureInfo.InvariantCulture, $"{snapshot.TotalRuns} Laeufe"));
        AppendKpiCard(sb, "Durchschnittstempo", snapshot.AveragePaceText, string.Create(CultureInfo.InvariantCulture, $"Laufzeit {FormatDuration(snapshot.TotalDurationSeconds)}"));
        AppendKpiCard(sb, "Herzfrequenz Ø", snapshot.AverageHeartRate > 0 ? string.Create(CultureInfo.InvariantCulture, $"{snapshot.AverageHeartRate:F0} bpm") : "-", string.Create(CultureInfo.InvariantCulture, $"Z1/Z2 Anteil {snapshot.OverallZ1Z2Percentage:F1}%"));
        AppendKpiCard(sb, "Aerobe Effizienz (AEF)", aefText, "Basis-Laeufe (>=80% Z1/Z2)");
        AppendKpiCard(sb, "Fitness / Ermuedung", string.Create(CultureInfo.InvariantCulture, $"CTL {form.LatestCtl:F1} / ATL {form.LatestAtl:F1}"), string.Create(CultureInfo.InvariantCulture, $"TSB {form.CurrentTsb:+0.0;-0.0;0.0} ({form.TsbLevel})"));
        sb.AppendLine("  </section>");
    }


    private static void AppendKpiCard(StringBuilder sb, string label, string value, string sub)
    {
        sb.AppendLine("    <div class=\"card\">");
        sb.Append("      <div class=\"kpi-label\">").Append(Enc(label)).AppendLine("</div>");
        sb.Append("      <div class=\"kpi-value\">").Append(Enc(value)).AppendLine("</div>");
        sb.Append("      <div class=\"kpi-sub\">").Append(Enc(sub)).AppendLine("</div>");
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


    private static void AppendTrendsAndZonePaceSection(StringBuilder sb, DashboardSnapshot snapshot)
    {
        sb.AppendLine("  <section class=\"two-col\">");
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <h2>Wochenvolumen &amp; Z1/Z2-Disziplin</h2>");
        AppendWeeklyVolumeSvg(sb, snapshot.ReportSnapshot.WeeklyTrends);
        sb.AppendLine("    </div>");

        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <h2>Zone-2 GAP Tempo-Trend</h2>");
        if (snapshot.ZonePaceTrends.Summary.TryGetValue("z2", out var z2) && z2.Count > 0)
        {
            sb.Append("      <p style=\"margin-bottom:8px;\">Frueh: <strong>").Append(Enc(z2.EarlyAveragePaceText ?? "-"))
                .Append("</strong> &rarr; Aktuell: <strong>").Append(Enc(z2.RecentAveragePaceText ?? "-"))
                .Append("</strong> (Delta ")
                .Append(z2.DeltaSecondsPerKm.HasValue ? z2.DeltaSecondsPerKm.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) : "0.0")
                .Append(" s/km bei ").Append(z2.Count).AppendLine(" Laeufen)</p>");
        }
        else
        {
            sb.AppendLine("      <p class=\"subtitle\">Noch keine ausreichenden Zone-2 Stream-Daten (&ge;120s).</p>");
        }
        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");
    }


    private static void AppendForecastAndZonesSection(StringBuilder sb, DashboardSnapshot snapshot)
    {
        sb.AppendLine("  <section class=\"two-col\">");
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <h2>Riegel Wettkampf-Prognosen &amp; 4-Wochen-Volumen</h2>");
        sb.AppendLine("      <div class=\"insight-grid\" style=\"margin-bottom:14px;\">");
        foreach (var pred in snapshot.RacePredictions)
        {
            sb.AppendLine("        <div class=\"insight-item\">");
            sb.Append("          <span class=\"badge ").Append(MapSeverityClass(pred.ReadinessSeverity)).Append("\">")
                .Append(Enc(pred.ReadinessLabel)).AppendLine("</span>");
            sb.Append("          <div style=\"font-weight:700;font-size:1.1rem;margin-top:6px;\">")
                .Append(Enc(pred.Name)).Append(": ").Append(Enc(pred.PredictedTimeText)).AppendLine("</div>");
            sb.Append("          <div class=\"subtitle\">Zieltempo ").Append(Enc(pred.TargetPaceText)).Append(" &mdash; ").Append(Enc(pred.Note)).AppendLine("</div>");
            sb.AppendLine("        </div>");
        }
        sb.AppendLine("      </div>");
        sb.AppendLine("      <table><thead><tr><th>Woche</th><th>Phase</th><th>Distanz</th><th>Load</th></tr></thead><tbody>");
        foreach (var week in snapshot.VolumeForecast)
        {
            sb.Append("        <tr><td>").Append(Enc(week.IsoWeek)).Append("</td><td>").Append(Enc(week.PhaseTitle))
                .Append("</td><td>").Append(week.ProjectedDistanceKm.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" km</td><td>").Append(week.ProjectedLoad.ToString("F0", CultureInfo.InvariantCulture)).AppendLine("</td></tr>");
        }
        sb.AppendLine("      </tbody></table>");
        sb.AppendLine("    </div>");

        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <h2>Kalibrierte Herzfrequenzzonen</h2>");
        sb.AppendLine("      <div class=\"zone-list\">");
        foreach (var zone in snapshot.HeartRateProfile.Zones.Values.OrderBy(z => z.Zone))
        {
            var color = ZoneColorMap.GetValueOrDefault(zone.Zone, "#94a3b8");
            var rangeStr = zone.Min <= 0
                ? string.Create(CultureInfo.InvariantCulture, $"< {zone.Max + 1} bpm")
                : string.Create(CultureInfo.InvariantCulture, $"{zone.Min} - {zone.Max} bpm");
            sb.AppendLine("        <div class=\"zone-row\">");
            sb.Append("          <span style=\"font-weight:600;color:").Append(color).Append(";\">Z").Append(zone.Zone)
                .Append(' ').Append(Enc(zone.Name)).AppendLine("</span>");
            sb.Append("          <span>").Append(Enc(rangeStr)).AppendLine("</span>");
            sb.AppendLine("        </div>");
        }
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");
    }


    private static void AppendReviewsTableSection(StringBuilder sb, DashboardSnapshot snapshot)
    {
        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine("    <h2>Letzte Laeufe &amp; 4-Stufen-Reviews</h2>");
        sb.AppendLine("    <div class=\"controls\"><input id=\"run-filter\" type=\"search\" placeholder=\"Laeufe filtern (Name, Typ, Notiz)...\"></div>");
        sb.AppendLine("    <table id=\"runs-table\">");
        sb.AppendLine("      <thead><tr><th>Datum</th><th>Lauf</th><th>Distanz</th><th>Zeit</th><th>HF Ø</th><th>Z1/Z2</th><th>Bewertung</th></tr></thead>");
        sb.AppendLine("      <tbody>");

        if (snapshot.RecentReviews.Count == 0 && snapshot.ReportSnapshot.RecentRuns.Count == 0)
        {
            sb.AppendLine("        <tr><td colspan=\"7\" class=\"subtitle\">Keine Laufaktivitaeten gefunden.</td></tr>");
        }
        else if (snapshot.RecentReviews.Count > 0)
        {
            foreach (var rev in snapshot.RecentReviews)
            {
                var z2Gap = rev.ZonePaceStats?.Zones.Values.FirstOrDefault(z => z.Zone == 2)?.GapPaceText;
                sb.AppendLine("        <tr class=\"run-row\">");
                sb.Append("          <td>").Append(Enc(rev.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).AppendLine("</td>");
                sb.Append("          <td><strong>").Append(Enc(rev.Name)).Append("</strong>");
                if (!string.IsNullOrWhiteSpace(rev.AthleteNote))
                {
                    sb.Append("<div class=\"subtitle\">Notiz: ").Append(Enc(rev.AthleteNote)).Append("</div>");
                }
                sb.AppendLine("</td>");
                sb.Append("          <td>").Append(rev.DistanceKm.ToString("F2", CultureInfo.InvariantCulture)).AppendLine(" km</td>");
                sb.Append("          <td>").Append(Enc(rev.MovingTimeText)).AppendLine("</td>");
                sb.Append("          <td>").Append(rev.AverageHeartRate.ToString("F0", CultureInfo.InvariantCulture)).AppendLine(" bpm</td>");
                sb.Append("          <td>").Append(rev.Z1Z2Percentage.ToString("F1", CultureInfo.InvariantCulture)).Append('%')
                    .Append(string.IsNullOrWhiteSpace(z2Gap) ? string.Empty : $"<div class=\"subtitle\">Z2 GAP {Enc(z2Gap)}</div>").AppendLine("</td>");
                sb.Append("          <td><details><summary>").Append(Enc(rev.Review.RatingLabel)).Append("</summary>")
                    .Append("<p class=\"subtitle\" style=\"margin-top:4px;\">").Append(Enc(rev.Review.Stages.Stage4NextRun.Directive)).Append("</p></details></td>");
                sb.AppendLine("        </tr>");
            }
        }
        else
        {
            foreach (var run in snapshot.ReportSnapshot.RecentRuns)
            {
                sb.AppendLine("        <tr class=\"run-row\">");
                sb.Append("          <td>").Append(Enc(run.StartDateLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).AppendLine("</td>");
                sb.Append("          <td><strong>").Append(Enc(run.Name)).AppendLine("</strong></td>");
                sb.Append("          <td>").Append(run.DistanceKm.ToString("F2", CultureInfo.InvariantCulture)).AppendLine(" km</td>");
                sb.Append("          <td>").Append(Enc(FormatDuration(run.MovingTimeSeconds))).AppendLine("</td>");
                sb.Append("          <td>").Append(run.AverageHeartRate.ToString("F0", CultureInfo.InvariantCulture)).AppendLine(" bpm</td>");
                sb.Append("          <td>").Append(run.Z1Z2Percentage.HasValue ? string.Create(CultureInfo.InvariantCulture, $"{run.Z1Z2Percentage.Value:F1}%") : "-").AppendLine("</td>");
                sb.AppendLine("          <td>-</td>");
                sb.AppendLine("        </tr>");
            }
        }

        sb.AppendLine("      </tbody>");
        sb.AppendLine("    </table>");
        sb.AppendLine("  </section>");
    }


    private static void AppendBanisterSparklineSvg(StringBuilder sb, IReadOnlyList<BanisterProjectedDay> points)
    {
        if (points.Count == 0)
        {
            return;
        }

        const int w = 520;
        const int h = 140;
        const int pad = 16;
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

        sb.Append("      <svg viewBox=\"0 0 ").Append(w).Append(' ').Append(h).AppendLine("\" role=\"img\" aria-label=\"Banister 14d CTL ATL TSB\">");
        sb.Append("        <polyline fill=\"none\" stroke=\"#38bdf8\" stroke-width=\"2.5\" points=\"").Append(BuildPolyline(p => p.Ctl)).AppendLine("\"/>");
        sb.Append("        <polyline fill=\"none\" stroke=\"#f59e0b\" stroke-width=\"2\" points=\"").Append(BuildPolyline(p => p.Atl)).AppendLine("\"/>");
        sb.Append("        <polyline fill=\"none\" stroke=\"#10b981\" stroke-width=\"2\" stroke-dasharray=\"4 3\" points=\"").Append(BuildPolyline(p => p.Tsb)).AppendLine("\"/>");
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
            sb.Append("        <rect x=\"").Append(x).Append("\" y=\"").Append(y.ToString("F1", CultureInfo.InvariantCulture))
                .Append("\" width=\"").Append(barW).Append("\" height=\"").Append(barH.ToString("F1", CultureInfo.InvariantCulture))
                .Append("\" rx=\"4\" fill=\"").Append(fill).Append("\"><title>")
                .Append(Enc(string.Create(CultureInfo.InvariantCulture, $"{t.IsoWeek}: {t.TotalDistanceKm:F1} km")))
                .AppendLine("</title></rect>");
        }
        sb.AppendLine("      </svg>");
    }

    private static void AppendScript(StringBuilder sb)
    {
        sb.AppendLine("""
              <script>
                (function () {
                  var input = document.getElementById('run-filter');
                  if (!input) return;
                  input.addEventListener('input', function () {
                    var q = input.value.toLowerCase();
                    var rows = document.querySelectorAll('#runs-table tbody .run-row');
                    for (var i = 0; i < rows.length; i++) {
                      var txt = rows[i].textContent.toLowerCase();
                      rows[i].style.display = txt.indexOf(q) !== -1 ? '' : 'none';
                    }
                  });
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
