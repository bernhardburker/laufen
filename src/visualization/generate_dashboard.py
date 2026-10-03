#!/usr/bin/env python3
"""HTML Dashboard Generator for running trends and activities.

Loads running activity data, computes weekly trends, summary KPIs,
and fills an HTML template for interactive visualization on GitHub Pages or locally.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

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
    parser.add_argument(
        "--input",
        "-i",
        type=Path,
        default=PROJECT_ROOT / "data" / "intervals_activities.json",
        help="Path to activity JSON file (default: data/intervals_activities.json)",
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
        default="Running & Trends Dashboard",
        help="Title displayed in the dashboard header",
    )
    return parser.parse_args()


def load_athlete_profile() -> Dict[str, Any]:
    athlete_path = PROJECT_ROOT / "config" / "athlete.json"
    if athlete_path.is_file():
        try:
            with open(athlete_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}
    return {}


def build_table_rows(processed_runs: List[Dict[str, Any]]) -> str:
    if not processed_runs:
        return '<tr><td colspan="9" style="text-align: center; color: #94a3b8; padding: 24px;">No running activities found in dataset.</td></tr>'

    # Chronological reverse (newest first)
    sorted_runs = sorted(processed_runs, key=lambda r: r.get("date") or "", reverse=True)
    rows: List[str] = []

    for run in sorted_runs:
        date_str = str(run.get("date", ""))[:10] or "N/A"
        name = str(run.get("name", "Run"))
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
            tag_class = "tag-green" if z_pct >= 80.0 else "tag-amber"
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


def generate_dashboard(
    input_file: Path,
    template_file: Path,
    output_file: Path,
    weeks: int = 12,
    title: str = "Running & Trends Dashboard",
) -> None:
    if not input_file.exists():
        raise FileNotFoundError(f"Input file not found: {input_file}")

    if not template_file.exists():
        raise FileNotFoundError(f"Template not found at: {template_file}")

    activities = load_activities(input_file)
    runs = filter_runs(activities)

    # Process runs
    processed_runs = []
    for r in runs:
        metrics = calculate_run_metrics(r)
        metrics["max_heartrate"] = r.get("max_heartrate")
        metrics["moving_time_str"] = format_duration(metrics["moving_time_s"])
        metrics["pace_str"] = format_pace(metrics["pace_s_per_km"])
        processed_runs.append(metrics)

    generation_date = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")

    if not processed_runs:
        context = {
            "DASHBOARD_TITLE": title,
            "GENERATION_DATE": generation_date,
            "TOTAL_DISTANCE_KM": "0.0",
            "TOTAL_RUNS": "0",
            "TOTAL_DURATION": "0m",
            "AVG_PACE": "-:-- /km",
            "AVG_HR": "-",
            "Z1_Z2_PERCENT": "0.0",
            "AEROBIC_EF": "-",
            "EMPTY_STATE_DISPLAY": "block",
            "CONTENT_DISPLAY": "none",
            "RUNS_TABLE_ROWS": '<tr><td colspan="9" style="text-align: center; color: #94a3b8; padding: 24px;">No running activities found in dataset.</td></tr>',
            "TRENDS_JSON": "[]",
            "ACTIVITIES_JSON": "[]",
            "ATHLETE_JSON": json.dumps(load_athlete_profile()),
        }
    else:
        trends = aggregate_weekly_trends(runs, max_weeks=weeks)
        total_dist_km = round(sum(r["distance_km"] for r in processed_runs), 2)
        total_time_s = sum(r["moving_time_s"] for r in processed_runs)
        total_runs_count = len(processed_runs)

        avg_pace_s = (total_time_s / total_dist_km) if total_dist_km > 0 else 0.0
        avg_pace_formatted = format_pace(avg_pace_s)

        hr_weighted = sum(
            r["avg_hr"] * r["moving_time_s"] for r in processed_runs if r["avg_hr"] > 0
        )
        hr_time = sum(r["moving_time_s"] for r in processed_runs if r["avg_hr"] > 0)
        avg_hr_val = round(hr_weighted / hr_time, 1) if hr_time > 0 else 0.0

        z1_z2_total_s = sum(r["z1_z2_seconds"] for r in processed_runs)
        zone_total_s = sum(r["total_zone_seconds"] for r in processed_runs)
        z1_z2_pct_val = (
            round((z1_z2_total_s / zone_total_s) * 100.0, 1)
            if zone_total_s > 0
            else 0.0
        )

        ef_list = [
            r["aerobic_ef"] for r in processed_runs if r["aerobic_ef"] is not None
        ]
        avg_ef_val = round(sum(ef_list) / len(ef_list), 3) if ef_list else "-"

        table_rows = build_table_rows(processed_runs)

        context = {
            "DASHBOARD_TITLE": title,
            "GENERATION_DATE": generation_date,
            "TOTAL_DISTANCE_KM": f"{total_dist_km:.1f}",
            "TOTAL_RUNS": str(total_runs_count),
            "TOTAL_DURATION": format_duration(total_time_s),
            "AVG_PACE": avg_pace_formatted,
            "AVG_HR": f"{avg_hr_val:.0f}",
            "Z1_Z2_PERCENT": f"{z1_z2_pct_val:.1f}",
            "AEROBIC_EF": str(avg_ef_val),
            "EMPTY_STATE_DISPLAY": "none",
            "CONTENT_DISPLAY": "block",
            "RUNS_TABLE_ROWS": table_rows,
            "TRENDS_JSON": json.dumps(trends, ensure_ascii=False),
            "ACTIVITIES_JSON": json.dumps(processed_runs, ensure_ascii=False),
            "ATHLETE_JSON": json.dumps(load_athlete_profile(), ensure_ascii=False),
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
