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

from src.analysis.coach_history import load_coach_history
from src.analysis.run_reviewer import load_run_reviews
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
    parser.add_argument(
        "--coach-history",
        type=Path,
        default=PROJECT_ROOT / "data" / "coach_history.json",
        help="Path to coach history JSON (default: data/coach_history.json)",
    )
    parser.add_argument(
        "--run-reviews",
        type=Path,
        default=PROJECT_ROOT / "data" / "run_reviews.json",
        help="Path to per-run reviews JSON cache (default: data/run_reviews.json)",
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


def build_table_rows(
    processed_runs: List[Dict[str, Any]],
    run_reviews: Optional[Dict[str, Any]] = None,
) -> str:
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

        run_id = str(run.get("id"))
        rev_info = (run_reviews or {}).get(run_id)
        review_html = ""
        cadence_str = ""
        if rev_info:
            r_eval = rev_info.get("review", {})
            rating = r_eval.get("rating", "grauzone")
            badge_class = (
                "tag-green"
                if rating == "optimal"
                else ("tag-cyan" if rating in ("solide", "intensiv") else "tag-amber")
            )
            r_label = html.escape(r_eval.get("rating_label", ""))
            r_summary = html.escape(r_eval.get("summary", ""))
            cad = int(rev_info.get("cadence_spm") or 0)
            if cad > 0:
                cadence_str = f' <span style="color: var(--text-muted); font-size: 0.75rem;">({cad} spm)</span>'
            coach_tip = html.escape(r_eval.get("coach_tip", ""))
            tip_html = (
                f'<div class="run-review-tip">💡 {coach_tip}</div>'
                if coach_tip
                else ""
            )
            review_html = (
                f'<details class="run-review-details">'
                f'<summary class="run-review-summary">'
                f'<span class="tag {badge_class}">{r_label}</span>'
                f'<span class="review-toggle-hint">Analyse anzeigen <span class="chevron">▼</span></span>'
                f'</summary>'
                f'<div class="run-review-content">'
                f'<div class="run-review-text">{r_summary}</div>'
                f'{tip_html}'
                f'</div>'
                f'</details>'
            )
            if not max_hr:
                max_hr = rev_info.get("max_hr")

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

        name_cell = f"<strong>{name}</strong>{cadence_str}{review_html}"

        row = (
            f'<tr class="run-row" data-run-id="{run_id}">'
            f'<td class="col-date" data-label="Datum">{date_str}</td>'
            f'<td class="col-activity" data-label="Aktivität">{name_cell}</td>'
            f'<td class="col-dist" data-label="Distanz">{dist_km:.2f} km</td>'
            f'<td class="col-duration" data-label="Dauer">{dur_str}</td>'
            f'<td class="col-pace" data-label="Ø Pace">{pace_str}</td>'
            f'<td class="col-hr" data-label="Ø Puls">{avg_hr_str}</td>'
            f'<td class="col-maxhr" data-label="Max Puls">{max_hr_str}</td>'
            f'<td class="col-zpct" data-label="Z1/Z2 %">{z_badge}</td>'
            f'<td class="col-load" data-label="Belastung">{load_str}</td>'
            f'</tr>'
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


def build_coach_history_html(history: List[Dict[str, Any]]) -> str:
    if not history:
        return ""

    rows: List[str] = []
    for entry in reversed(history):
        date_str = html.escape(str(entry.get("date", "-"))[:10])
        rec_text = html.escape(str(entry.get("recommendation", "-")))
        actual_text = html.escape(str(entry.get("actual", "-")))
        effect_text = html.escape(str(entry.get("effect", "-")))
        status = str(entry.get("status", "offen")).lower()

        if status == "erfüllt":
            status_badge = '<span class="tag tag-green">✓ Erfüllt</span>'
        elif status == "angepasst_nach_tempo":
            status_badge = '<span class="tag tag-cyan">⚡ Adaptiert (Tempo)</span>'
        elif status == "verfehlt":
            status_badge = '<span class="tag tag-amber">⚠ Verfehlt</span>'
        elif status == "teilweise":
            status_badge = '<span class="tag tag-amber">~ Teilweise</span>'
        else:
            status_badge = '<span class="tag" style="background: rgba(148, 163, 184, 0.15); color: #94a3b8; border: 1px solid rgba(148, 163, 184, 0.3);">⏳ Offen</span>'

        rows.append(
            f"<tr>"
            f"<td style=\"white-space: nowrap; font-weight: 600;\">{date_str}</td>"
            f"<td>{rec_text}</td>"
            f"<td>{actual_text}</td>"
            f"<td style=\"text-align: center;\">{status_badge}</td>"
            f"<td>{effect_text}</td>"
            f"</tr>"
        )

    rows_html = "".join(rows)
    return f"""
    <div class="coach-history-card" style="margin-top: 24px; background: rgba(30, 41, 59, 0.7); border: 1px solid rgba(56, 189, 248, 0.2); border-radius: 12px; padding: 20px;">
      <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 16px; flex-wrap: wrap; gap: 8px;">
        <div style="display: flex; align-items: center; gap: 10px;">
          <span class="badge-dot" style="background-color: var(--cyan); box-shadow: 0 0 8px var(--cyan);"></span>
          <h3 style="font-size: 1.05rem; font-weight: 700; color: #f8fafc; margin: 0;">🧠 Coach-Gedächtnis &amp; Feedback-Schleife</h3>
        </div>
        <span class="tag tag-cyan">{len(history)} Zyklen erfasst</span>
      </div>
      <p style="color: #94a3b8; font-size: 0.85rem; margin-top: 0; margin-bottom: 16px;">
        Nüchterne Verlaufskontrolle: Was hat der Trainer empfohlen, was wurde gelaufen und welcher physiologische Effekt ist messbar?
      </p>
      <div class="table-container" style="overflow-x: auto;">
        <table class="activities-table">
          <thead>
            <tr>
              <th style="width: 105px;">Datum</th>
              <th>Empfehlung des Coaches</th>
              <th>Tatsächliche Umsetzung (Abgleich)</th>
              <th style="width: 140px; text-align: center;">Status</th>
              <th>Gemessener Effekt / Trend</th>
            </tr>
          </thead>
          <tbody>
            {rows_html}
          </tbody>
        </table>
      </div>
    </div>
    """


def build_summary_html(
    summary: Dict[str, Any],
    ai_coach_markdown: Optional[str] = None,
    coach_history: Optional[List[Dict[str, Any]]] = None,
) -> str:
    coach_history_block = build_coach_history_html(coach_history or [])
    if not summary or not summary.get("insights"):
        if coach_history_block:
            return f'<section class="summary-section">{coach_history_block}</section>'
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
      {coach_history_block}
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


def build_zones_reference_html(athlete_profile: Dict[str, Any]) -> str:
    ath = athlete_profile.get("athlete", {}) if isinstance(athlete_profile, dict) else {}
    hr = ath.get("heart_rate", {}) if isinstance(ath, dict) else {}
    zones_dict = hr.get("zones", {})
    raw_zones = hr.get("raw_hr_zones") or []
    lthr = hr.get("lthr") or 178

    zone_colors = {
        1: "#94a3b8",
        2: "var(--emerald)",
        3: "var(--amber)",
        4: "var(--orange)",
        5: "var(--rose)",
        6: "#c084fc",
        7: "#f43f5e",
    }
    zone_descs = {
        1: "Sehr lockere aktive Erholung, Kapillarisierung & Stoffwechselaktivierung.",
        2: "75–80% des Trainingsvolumens. Fettstoffwechsel, Mitochondrien, Sprechtest problemlos.",
        3: "Zügiger Dauerlauf. Erhöhte Ermüdung, Sprechen in kurzen Sätzen. Dosiert einsetzen!",
        4: f"Unterhalb der Schwelle (LTHR: {lthr} bpm). Hart aber kontrolliert, Tempohärte.",
        5: "Oberhalb der Schwelle. Wettkampfspezifische Ausdauer, spürbarer Laktatanstieg.",
        6: "VO2 Max / Aerobe Kapazität. Maximale Sauerstoffaufnahme, kurze harte Intervalle.",
        7: "Anaerobe Kapazität / Sprint. Zielsprints, maximale Ausbelastung und Laktattoleranz.",
    }

    german_names = {
        "recovery": "Regeneration",
        "aerobic": "Grundlagenausdauer",
        "tempo": "Tempo / Grauzone",
        "subthreshold": "Schwellenbereich",
        "superthreshold": "Über Schwelle",
        "aerobic capacity": "VO2 Max",
        "anaerobic": "Anaerob / Maximal",
    }

    items = []
    if zones_dict:
        for key, z_info in zones_dict.items():
            z_num = int(z_info.get("zone", 1))
            orig_name = z_info.get("name", f"Zone {z_num}")
            de_name = german_names.get(orig_name.lower(), orig_name)
            title_display = f"Z{z_num} {de_name} ({orig_name})" if de_name != orig_name else f"Z{z_num} {orig_name}"
            z_min = int(z_info.get("min", 0))
            z_max = int(z_info.get("max", 0))
            color = zone_colors.get(z_num, "#94a3b8")
            border = ' style="border-color: rgba(16, 185, 129, 0.4);"' if z_num == 2 else ""

            range_str = f"&lt; {z_max + 1} bpm" if z_min <= 0 else f"{z_min} – {z_max} bpm"
            desc = zone_descs.get(z_num, f"Intensitätszone {z_num} aus Intervals.icu.")
            items.append(
                f'          <div class="zone-item"{border}>\n'
                f'            <div class="zone-item-header">\n'
                f'              <span class="zone-name" style="color: {color};">{html.escape(title_display)}</span>\n'
                f'              <span class="zone-range">{range_str}</span>\n'
                f'            </div>\n'
                f'            <div class="zone-desc">{desc}</div>\n'
                f'          </div>'
            )
    elif raw_zones:
        prev = 0
        for i, cutoff in enumerate(raw_zones):
            z_num = i + 1
            color = zone_colors.get(z_num, "#94a3b8")
            border = ' style="border-color: rgba(16, 185, 129, 0.4);"' if z_num == 2 else ""
            z_min = prev + 1 if prev > 0 else 0
            range_str = f"&lt; {cutoff + 1} bpm" if z_min <= 0 else f"{z_min} – {cutoff} bpm"
            desc = zone_descs.get(z_num, f"Intensitätszone {z_num} aus Intervals.icu.")
            items.append(
                f'          <div class="zone-item"{border}>\n'
                f'            <div class="zone-item-header">\n'
                f'              <span class="zone-name" style="color: {color};">Z{z_num} Zone {z_num}</span>\n'
                f'              <span class="zone-range">{range_str}</span>\n'
                f'            </div>\n'
                f'            <div class="zone-desc">{desc}</div>\n'
                f'          </div>'
            )
            prev = int(cutoff)

    return "\n".join(items)


def generate_dashboard(
    input_file: Path,
    template_file: Path,
    output_file: Path,
    weeks: int = 12,
    title: str = "Lauf- & Leistungs-Dashboard",
    ai_coach_file: Optional[Path] = None,
    ai_summary_file: Optional[Path] = None,
    athlete_file: Optional[Path] = None,
    coach_history_file: Optional[Path] = None,
    run_reviews_file: Optional[Path] = None,
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
            "REVIEWS_JSON": "{}",
            "ATHLETE_JSON": json.dumps(athlete_profile),
            "ZONES_REFERENCE_HTML": build_zones_reference_html(athlete_profile),
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

        target_history_file = (
            coach_history_file
            if coach_history_file is not None
            else (PROJECT_ROOT / "data" / "coach_history.json")
        )
        coach_history = (
            load_coach_history(target_history_file)
            if target_history_file and target_history_file.is_file()
            else []
        )

        target_reviews_file = (
            run_reviews_file
            if run_reviews_file is not None
            else (PROJECT_ROOT / "data" / "run_reviews.json")
        )
        run_reviews = (
            load_run_reviews(target_reviews_file)
            if target_reviews_file and target_reviews_file.is_file()
            else {}
        )

        table_rows = build_table_rows(processed_runs, run_reviews=run_reviews)
        summary_html = build_summary_html(
            summary_data, ai_coach_markdown=ai_coach_text, coach_history=coach_history
        )
        forecast_html = build_forecast_cards_html(predictions)

        forecast_payload = {
            "predictions": predictions,
            "volume_forecast": volume_forecast,
            "form": form_data,
        }

        context = {
            "DASHBOARD_TITLE": title,
            "COACH_HISTORY_HTML": build_coach_history_html(coach_history),
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
            "REVIEWS_JSON": json.dumps(run_reviews, ensure_ascii=False),
            "ATHLETE_JSON": json.dumps(athlete_profile, ensure_ascii=False),
            "ZONES_REFERENCE_HTML": build_zones_reference_html(athlete_profile),
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
            coach_history_file=args.coach_history,
            run_reviews_file=args.run_reviews,
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
