#!/usr/bin/env python3
"""Tool wrapper for weekly trend analysis."""
import argparse
import json
import os
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
sys.path.insert(0, str(REPO_ROOT))

from src.analysis.trends import load_activities, filter_runs, aggregate_weekly_trends, render_trends_table

def main():
    parser = argparse.ArgumentParser(description="Weekly running trends")
    parser.add_argument("--weeks", type=int, default=8, help="Number of weeks")
    args = parser.parse_args()

    data_dir = Path(os.environ.get("AGENT_DATA_DIR", REPO_ROOT / "data"))
    cache_file = data_dir / "activities_cache.json"
    static_file = REPO_ROOT / "data" / "intervals_activities.json"

    input_file = cache_file if cache_file.exists() else static_file
    if not input_file.exists():
        print(json.dumps({
            "text": "Es sind noch keine Aktivitätsdaten vorhanden. Rufe zuerst `get_activities` auf.",
            "chips": []
        }))
        return

    activities = load_activities(input_file)
    runs = filter_runs(activities)
    trends = aggregate_weekly_trends(runs, weeks=args.weeks)

    table_text = render_trends_table(trends)
    total_km = sum(t["total_distance_km"] for t in trends)

    response_text = f"### 📊 Wöchentlicher Trainingstrend ({args.weeks} Wochen, {total_km:.1f} km gesamt)\n\n"
    response_text += "```text\n" + table_text + "\n```"

    output_payload = {
        "text": response_text,
        "chips": [
            {"label": "📊 Intervals.icu Analyse", "url": "https://intervals.icu", "icon": "📊", "action_type": "link"}
        ],
        "data": {"weeks": len(trends), "total_km": round(total_km, 2)}
    }
    print(json.dumps(output_payload, ensure_ascii=False))

if __name__ == "__main__":
    main()
