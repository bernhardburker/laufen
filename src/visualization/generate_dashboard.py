#!/usr/bin/env python3
"""HTML Dashboard Generator for running trends, forecasts, and activities.

Loads running activity data, computes weekly trends, KPI metrics,
race time predictions (Riegel formula), training volume forecasts,
fitness/fatigue curves (CTL/ATL/TSB), and dynamic coaching recommendations,
rendering a German-localized responsive dashboard.
"""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.forecast import (
    calculate_fitness_form_trend,
    generate_training_summary,
    generate_volume_forecast,
    predict_race_times,
)
from src.analysis.trends import (
    aggregate_weekly_trends,
    calculate_run_metrics,
    filter_runs,
    format_duration,
    format_pace,
    load_activities,
)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate an interactive HTML dashboard from running activities."
    )
    default_input = Path(
        os.getenv(
            "ACTIVITIES_PATH",
            str(PROJECT_ROOT / "data" / "intervals_activities.json"),
        )
    )
    parser.add_argument(
        "--input",
        "-i",
        type=Path,
        default=default_input,
        help="Path to activity JSON file (default: data/intervals_activities.json or ACTIVITIES_PATH)",
    )
    parser.add_argument(
        "--template",
        "-t",
        type=Path,
        default=PROJECT_ROOT / "templates" / "dashboard.html",
        help="Path to HTML template (default: templates/dashboard.html)",
    )
    parser.add_argument(
        "--output",
        "-o",
        type=Path,
        default=PROJECT_ROOT / "dist" / "index.html",
        help="Output HTML path (default: dist/index.html)",
    )
    parser.add_argument(
        "--weeks",
        "-w",
        type=int,
        default=12,
        help="Number of recent calendar weeks to analyze (default: 12)",
    )
    parser.add_argument(
        "--title",
        default="Lauf- & Leistungs-Dashboard",
        help="Title displayed in the dashboard header (default: Lauf- & Leistungs-Dashboard)",
    )
    parser.add_argument(
        "--ai-coach",
        type=Path,
        default=PROJECT_ROOT / "data" / "ai_coach_commentary.md",
        help="Path to AI coach markdown commentary (default: data/ai_coach_commentary.md)",
    )
    parser.add_argument(
        "--ai-summary",
        type=Path,
        default=PROJECT_ROOT / "data" / "ai_coach_summary.json",
        help="Path to AI coach structured JSON summary (default: data/ai_coach_summary.json)",
    )
    parser.add_argument(
        "--athlete",
        "-a",
        type=Path,
        default=PROJECT_ROOT / "config" / "athlete.json",
        help="Path to athlete profile JSON (default: config/athlete.json)",
    )
    return parser.parse_args()


def load_athlete_profile(file_path: Optional[Path] = None) -> Dict[str, Any]:
    athlete_path = file_path or (PROJECT_ROOT / "config" / "athlete.json")
    if athlete_path.is_file():
        try:
            with open(athlete_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}
    return {}


def build_table_rows(processed_runs: List[Dict[str, Any]]) -> str:
    if not processed_runs:
        return (
            '<tr><td colspan="9" style="text-align: center; color: #94a3b8; padding: 24px;">'
            "Keine Laufaktivitäten im Datensatz gefunden. (No running activities found)</td></tr>"
        )

    # Chronological reverse (newest first)
    sorted_runs = sorted(
        processed_runs, key=lambda r: r.get("date") or "", reverse=True
    )
    rows: List[str] = []

    for run in sorted_runs:
        date_str = str(run.get("date", ""))[:10] or "N/A"
        name = html.escape(str(run.get("name", "Lauf")))
        dist_km = run.get("distance_km", 0.0)
        dur_str = format_duration(run.get("moving_time_s", 0))
        pace_str = format_pace(run.get("pace_s_per_km", 0.0))

        avg_hr = run.get("avg_hr", 0.0)
        avg_hr_str = f"{int(round(avg_hr))} bpm" if avg_hr > 0 else "-"

        max_hr = run.get("max_heartrate")
        max_hr_str = f"{int(round(max_hr))} bpm" if max_hr else "-"

        z1_z2_sec = run.get("z1_z2_seconds", 0)
        tot_sec = run.get("total_zone_seconds", 0)
        if tot_sec > 0:
            z_pct = round((z1_z2_sec / tot_sec) * 100.0, 1)
            tag_class = "tag-green" if z_pct >= 75.0 else "tag-amber"
            z_badge = f'<span class="tag {tag_class}">{z_pct:.1f}%</span>'
        else:
            z_badge = '<span style="color: #64748b;">-</span>'

        load_val = run.get("training_load", 0.0)
        load_str = f"{int(round(load_val))}" if load_val > 0 else "-"

        row = (
            f"<tr>"
            f"<td>{date_str}</td>"
            f"<td><strong>{name}</strong></td>"
            f"<td>{dist_km:.2f} km</td>"
            f"<td>{dur_str}</td>"
            f"<td>{pace_str}</td>"
            f"<td>{avg_hr_str}</td>"
            f"<td>{max_hr_str}</td>"
            f"<td>{z_badge}</td>"
            f"<td>{load_str}</td>"
            f"</tr>"
        )
        rows.append(row)

    return "\n".join(rows)


def render_markdown_to_html(md_text: str) -> str:
    lines = md_text.strip().splitlines()
    html_lines = []
    in_list = False

    for line in lines:
        stripped = line.strip()
        if not stripped:
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            continue

        if stripped in ("---", "***", "___"):
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            html_lines.append(
                '<hr style="border: none; border-top: 1px solid var(--card-border); margin: 16px 0;">'
            )
            continue

        if stripped.startswith("### "):
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            text = html.escape(stripped[4:])
            html_lines.append(
                f'<h4 style="font-size: 1.05rem; font-weight: 600; color: var(--cyan); margin: 14px 0 6px;">{text}</h4>'
            )
            continue
        elif stripped.startswith("## "):
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            text = html.escape(stripped[3:])
            html_lines.append(
                f'<h3 style="font-size: 1.2rem; font-weight: 700; color: var(--text-main); margin: 16px 0 8px;">{text}</h3>'
            )
            continue

        if stripped.startswith("> "):
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            q_text = html.escape(stripped[2:])
            q_text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", q_text)
            html_lines.append(
                f'<blockquote style="border-left: 3px solid var(--cyan); padding: 8px 14px; background: rgba(56, 189, 248, 0.05); border-radius: 4px; color: var(--text-main); font-style: italic; margin: 12px 0;">{q_text}</blockquote>'
            )
            continue

        if stripped.startswith(("- ", "* ")) or (
            len(stripped) > 2
            and stripped[0].isdigit()
            and stripped[1:3] in (". ", ") ")
        ):
            if not in_list:
                html_lines.append(
                    '<ul style="margin: 8px 0 12px 20px; color: var(--text-muted); font-size: 0.88rem; line-height: 1.6;">'
                )
                in_list = True
            item_text = (
                stripped[2:]
                if stripped.startswith(("- ", "* "))
                else stripped.split(". ", 1)[-1]
            )
            item_text = html.escape(item_text)
            item_text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", item_text)
            item_text = re.sub(r"\*(.+?)\*", r"<em>\1</em>", item_text)
            html_lines.append(f'<li style="margin-bottom: 6px;">{item_text}</li>')
            continue

        if in_list:
            html_lines.append("</ul>")
            in_list = False

        p_text = html.escape(stripped)
        p_text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", p_text)
        p_text = re.sub(r"\*(.+?)\*", r"<em>\1</em>", p_text)
        html_lines.append(
            f'<p style="font-size: 0.88rem; color: var(--text-muted); line-height: 1.55; margin-bottom: 10px;">{p_text}</p>'
        )

    if in_list:
        html_lines.append("</ul>")

    return "\n".join(html_lines)


def build_summary_html(
    summary: Dict[str, Any], ai_coach_markdown: Optional[str] = None
) -> str:
    if not summary or not summary.get("insights"):
        return ""

    badge_level = summary.get("status_level", "info")
    badge_color = "#38bdf8"
    if badge_level == "warning":
        badge_color = "#f59e0b"
    elif badge_level == "success":
        badge_color = "#10b981"

    insights_html = []
    for ins in summary.get("insights", []):
        tag_class = (
            "tag-amber"
            if ins.get("type") == "warning"
            else (
                "tag-green" if ins.get("type") == "success" else "tag-cyan"
            )
        )
        escaped_title = html.escape(ins["title"])
        escaped_badge = html.escape(ins["badge"])
        escaped_text = html.escape(ins["text"])
        insights_html.append(
            f'<div class="summary-item">'
            f'  <div class="summary-item-header">'
            f'    <span class="summary-item-title">{escaped_title}</span>'
            f'    <span class="tag {tag_class}">{escaped_badge}</span>'
            f"  </div>"
            f'  <p class="summary-item-text">{escaped_text}</p>'
            f"</div>"
        )

    recommendations_html = []
    for rec in summary.get("recommendations", []):
        tag_class = rec.get("tag_class", "tag-cyan")
        escaped_tag = html.escape(rec["tag"])
        escaped_title = html.escape(rec["title"])
        escaped_text = html.escape(rec["text"])
        recommendations_html.append(
            f'<div class="recommendation-card">'
            f'  <div class="rec-header">'
            f'    <span class="tag {tag_class}">{escaped_tag}</span>'
            f'    <h4 class="rec-title">{escaped_title}</h4>'
            f"  </div>"
            f'  <p class="rec-text">{escaped_text}</p>'
            f"</div>"
        )

    status_title_esc = html.escape(summary.get("status_title", ""))
    status_badge_esc = html.escape(summary.get("status_badge", ""))

    ai_coach_block = ""
    if ai_coach_markdown and ai_coach_markdown.strip():
        coach_html = render_markdown_to_html(ai_coach_markdown)
        ai_coach_block = f"""
        <div class="ai-coach-banner" style="background: rgba(56, 189, 248, 0.04); border: 1px solid rgba(56, 189, 248, 0.25); border-radius: 10px; padding: 20px; margin-bottom: 24px;">
          <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px; flex-wrap: wrap; gap: 8px;">
            <div style="display: flex; align-items: center; gap: 8px;">
              <span class="badge-dot" style="background-color: var(--cyan); box-shadow: 0 0 8px var(--cyan);"></span>
              <span style="font-weight: 700; color: var(--cyan); font-size: 0.95rem;">🤖 KI-Laufcoach (Antigravity AI Agent)</span>
            </div>
            <span class="tag tag-cyan">Agentic Coaching</span>
          </div>
          <div class="ai-coach-body" style="font-size: 0.9rem; line-height: 1.6;">
            {coach_html}
          </div>
        </div>
        """

    return f"""
    <section class="summary-section">
      {ai_coach_block}
      <div class="summary-header">
        <div class="summary-header-left">
          <div class="status-indicator" style="background-color: {badge_color}; box-shadow: 0 0 10px {badge_color};"></div>
          <div>
            <h2 class="section-title">Trainings-Status &amp; Kernaussagen</h2>
            <p class="section-subtitle">{status_title_esc}</p>
          </div>
        </div>
        <span class="tag" style="background: rgba(245, 158, 11, 0.15); color: {badge_color}; border: 1px solid {badge_color}40; font-size: 0.85rem; padding: 6px 14px;">
          {status_badge_esc}
        </span>
      </div>
      <div class="summary-grid">
        <div class="summary-column">
          <h3 class="column-subtitle">📊 Wichtigste Erkenntnisse</h3>
          <div class="summary-list">
            {"".join(insights_html)}
          </div>
        </div>
        <div class="summary-column">
          <h3 class="column-subtitle">🎯 Konkrete Handlungsempfehlungen</h3>
          <div class="recommendations-list">
            {"".join(recommendations_html)}
          </div>
        </div>
      </div>
    </section>
    """


def build_forecast_cards_html(predictions: List[Dict[str, Any]]) -> str:
    if not predictions:
        return ""

    cards = []
    for pred in predictions:
        dist_name = html.escape(pred["name"])
        readiness = html.escape(pred["readiness"])
        time_str = html.escape(pred["predicted_time_str"])
        pace_str = html.escape(pred["target_pace_str"])
        note = html.escape(pred["note"])
        basis = html.escape(pred["basis"])
        tag_class = pred.get("readiness_tag", "tag-cyan")

        cards.append(
            f'<div class="forecast-card">'
            f'  <div class="forecast-card-header">'
            f'    <span class="forecast-dist">{dist_name}</span>'
            f'    <span class="tag {tag_class}">{readiness}</span>'
            f"  </div>"
            f'  <div class="forecast-time">{time_str}</div>'
            f'  <div class="forecast-pace">Ziel-Pace: <strong>{pace_str}</strong></div>'
            f'  <p class="forecast-note">{note}</p>'
            f'  <div class="forecast-basis">{basis}</div>'
            f"</div>"
        )
    return "\n".join(cards)


def generate_dashboard(
    input_file: Path,
    template_file: Path,
    output_file: Path,
    weeks: int = 12,
    title: str = "Lauf- & Leistungs-Dashboard",
    ai_coach_file: Optional[Path] = None,
    ai_summary_file: Optional[Path] = None,
    athlete_file: Optional[Path] = None,
) -> None:
    if not input_file.exists():
        raise FileNotFoundError(f"Input file not found: {input_file}")

    if not template_file.exists():
        raise FileNotFoundError(f"Template not found at: {template_file}")

    activities = load_activities(input_file)
    runs = filter_runs(activities)
    athlete_profile = load_athlete_profile(athlete_file)

    # Process runs
    processed_runs = []
    for r in runs:
        metrics = calculate_run_metrics(r)
        metrics["max_heartrate"] = r.get("max_heartrate")
        metrics["moving_time_str"] = format_duration(metrics["moving_time_s"])
        metrics["pace_str"] = format_pace(metrics["pace_s_per_km"])
        metrics["icu_ctl"] = r.get("icu_ctl")
        metrics["icu_atl"] = r.get("icu_atl")
        processed_runs.append(metrics)

    generation_date = datetime.now(timezone.utc).strftime("%d.%m.%Y %H:%M UTC")

    strava_placeholders = [
        a
        for a in activities
        if isinstance(a, dict) and a.get("source") == "STRAVA" and a.get("type") is None
    ]
    strava_count = len(strava_placeholders)

    if not processed_runs:
        empty_datasource_html = ""
        if strava_count > 0:
            empty_datasource_html = (
                f'<div class="datasource-banner">'
                f'  <span class="datasource-icon">⚠️</span>'
                f'  <div class="datasource-text">'
                f'    <strong>Keine exportierbaren Läufe:</strong> Es wurden {strava_count} Aktivitäten aus Strava gefunden, '
                f'    diese dürfen jedoch laut Strava-API-Vorgaben nicht als Rohdaten weitergegeben werden. '
                f'    Bitte Garmin Connect direkt verbinden oder Aktivitäten manuell importieren.'
                f'  </div>'
                f'</div>'
            )
        context = {
            "DASHBOARD_TITLE": title,
            "GENERATION_DATE": generation_date,
            "DATA_SOURCE_INFO_HTML": empty_datasource_html,
            "TOTAL_DISTANCE_KM": "0.0",
            "TOTAL_RUNS": "0",
            "TOTAL_DURATION": "0m",
            "AVG_PACE": "-:-- /km",
            "AVG_HR": "-",
            "Z1_Z2_PERCENT": "0.0",
            "AEROBIC_EF": "-",
            "CURRENT_CTL": "0.0",
            "CURRENT_ATL": "0.0",
            "CURRENT_TSB": "0.0",
            "CURRENT_TSB_STATUS": "Keine Daten",
            "SUMMARY_SECTION_HTML": "",
            "FORECAST_CARDS_HTML": "",
            "EMPTY_STATE_DISPLAY": "block",
            "CONTENT_DISPLAY": "none",
            "RUNS_TABLE_ROWS": (
                '<tr><td colspan="9" style="text-align: center; color: #94a3b8; padding: 24px;">'
                "Keine Laufaktivitäten gefunden. (no running activities)</td></tr>"
            ),
            "TRENDS_JSON": "[]",
            "ACTIVITIES_JSON": "[]",
            "ATHLETE_JSON": json.dumps(athlete_profile),
            "FORECAST_JSON": "{}",
            "SUMMARY_JSON": "{}",
        }
    else:
        trends = aggregate_weekly_trends(runs, max_weeks=weeks)
        datasource_info_html = ""
        if strava_count > 0:
            datasource_info_html = (
                f'<div class="datasource-banner">'
                f'  <span class="datasource-icon">💡</span>'
                f'  <div class="datasource-text">'
                f'    <strong>Datenquellen-Hinweis:</strong> Es werden aktuell <strong>{len(processed_runs)} Läufe</strong> '
                f'    (Garmin Connect) vollständig analysiert. <strong>{strava_count} ältere Aktivitäten</strong> stammen aus Strava '
                f'    und enthalten laut Strava-API-Bestimmungen keine Telemetriewerte über die Schnittstelle.<br>'
                f'    <span class="datasource-tip">'
                f'      Tipp: Für lückenlose historische Analysen in Intervals.icu unter <em>Einstellungen &rarr; Verbindungen &rarr; Garmin Connect</em> '
                f'      auf <em>„Download old data“</em> klicken.'
                f'    </span>'
                f'  </div>'
                f'</div>'
            )
        total_dist_km = round(sum(r["distance_km"] for r in processed_runs), 2)
        total_time_s = sum(r["moving_time_s"] for r in processed_runs)
        total_runs_count = len(processed_runs)

        avg_pace_s = (
            (total_time_s / total_dist_km) if total_dist_km > 0 else 0.0
        )
        avg_pace_formatted = format_pace(avg_pace_s)

        hr_weighted = sum(
            r["avg_hr"] * r["moving_time_s"]
            for r in processed_runs
            if r["avg_hr"] > 0
        )
        hr_time = sum(
            r["moving_time_s"] for r in processed_runs if r["avg_hr"] > 0
        )
        avg_hr_val = round(hr_weighted / hr_time, 1) if hr_time > 0 else 0.0

        z1_z2_total_s = sum(r["z1_z2_seconds"] for r in processed_runs)
        zone_total_s = sum(r["total_zone_seconds"] for r in processed_runs)
        z1_z2_pct_val = (
            round((z1_z2_total_s / zone_total_s) * 100.0, 1)
            if zone_total_s > 0
            else 0.0
        )

        ef_list = [
            r["aerobic_ef"]
            for r in processed_runs
            if r["aerobic_ef"] is not None
        ]
        avg_ef_val = (
            round(sum(ef_list) / len(ef_list), 3) if ef_list else "-"
        )

        # Forecast and form calculations
        predictions = predict_race_times(processed_runs, athlete_profile)
        volume_forecast = generate_volume_forecast(trends, num_weeks=4)
        form_data = calculate_fitness_form_trend(processed_runs)

        # Base deterministic summary (used if no dynamic AI summary is provided)
        summary_data = generate_training_summary(
            processed_runs, trends, athlete_profile
        )

        ai_coach_text = None
        # Check if dynamic AI coach summary JSON exists (generated by agy)
        target_ai_summary = ai_summary_file or (
            PROJECT_ROOT / "data" / "ai_coach_summary.json"
        )
        if target_ai_summary and target_ai_summary.is_file():
            try:
                ai_sum = json.loads(target_ai_summary.read_text(encoding="utf-8"))
                if (
                    isinstance(ai_sum, dict)
                    and "status_title" in ai_sum
                    and "insights" in ai_sum
                    and "recommendations" in ai_sum
                ):
                    summary_data = ai_sum
                    if "coach_commentary" in ai_sum:
                        ai_coach_text = ai_sum["coach_commentary"]
            except Exception:
                pass

        if not ai_coach_text and ai_coach_file and ai_coach_file.is_file():
            try:
                ai_coach_text = ai_coach_file.read_text(encoding="utf-8")
            except Exception:
                ai_coach_text = None

        table_rows = build_table_rows(processed_runs)
        summary_html = build_summary_html(
            summary_data, ai_coach_markdown=ai_coach_text
        )
        forecast_html = build_forecast_cards_html(predictions)

        forecast_payload = {
            "predictions": predictions,
            "volume_forecast": volume_forecast,
            "form": form_data,
        }

        context = {
            "DASHBOARD_TITLE": title,
            "GENERATION_DATE": generation_date,
            "DATA_SOURCE_INFO_HTML": datasource_info_html,
            "TOTAL_DISTANCE_KM": f"{total_dist_km:.1f}",
            "TOTAL_RUNS": str(total_runs_count),
            "TOTAL_DURATION": format_duration(total_time_s),
            "AVG_PACE": avg_pace_formatted,
            "AVG_HR": f"{avg_hr_val:.0f}",
            "Z1_Z2_PERCENT": f"{z1_z2_pct_val:.1f}",
            "AEROBIC_EF": str(avg_ef_val),
            "CURRENT_CTL": f"{form_data['latest_ctl']:.1f}",
            "CURRENT_ATL": f"{form_data['latest_atl']:.1f}",
            "CURRENT_TSB": f"{form_data['current_tsb']:+.1f}",
            "CURRENT_TSB_STATUS": form_data["tsb_status"],
            "SUMMARY_SECTION_HTML": summary_html,
            "FORECAST_CARDS_HTML": forecast_html,
            "EMPTY_STATE_DISPLAY": "none",
            "CONTENT_DISPLAY": "block",
            "RUNS_TABLE_ROWS": table_rows,
            "TRENDS_JSON": json.dumps(trends, ensure_ascii=False),
            "ACTIVITIES_JSON": json.dumps(processed_runs, ensure_ascii=False),
            "ATHLETE_JSON": json.dumps(athlete_profile, ensure_ascii=False),
            "FORECAST_JSON": json.dumps(forecast_payload, ensure_ascii=False),
            "SUMMARY_JSON": json.dumps(summary_data, ensure_ascii=False),
        }

    template_content = template_file.read_text(encoding="utf-8")

    # Render template placeholders
    rendered = template_content
    for key, value in context.items():
        rendered = rendered.replace(f"{{{{ {key} }}}}", str(value))
        rendered = rendered.replace(f"{{{{{key}}}}}", str(value))

    output_file.parent.mkdir(parents=True, exist_ok=True)
    output_file.write_text(rendered, encoding="utf-8")
    print(f"Dashboard successfully generated: {output_file}")


def main() -> int:
    args = parse_arguments()
    try:
        generate_dashboard(
            input_file=args.input,
            template_file=args.template,
            output_file=args.output,
            weeks=args.weeks,
            title=args.title,
            ai_coach_file=args.ai_coach,
            ai_summary_file=args.ai_summary,
            athlete_file=args.athlete,
        )
        return 0
    except FileNotFoundError as e:
        print(f"Error: {e}", file=sys.stderr)
        return 1
    except Exception as e:
        print(f"Unexpected error generating dashboard: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
