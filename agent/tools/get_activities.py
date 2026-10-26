#!/usr/bin/env python3
"""Tool wrapper for fetching running activities with a 15-minute file cache."""
import argparse
import datetime
import json
import os
import sys
from pathlib import Path

# Add project root to sys.path
SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
sys.path.insert(0, str(REPO_ROOT))

from src.integrations.download_intervals import fetch_intervals_activities, format_pace, format_duration

def main():
    parser = argparse.ArgumentParser(description="Fetch running activities with caching")
    parser.add_argument("--days", type=int, default=28, help="Days in the past")
    parser.add_argument("--force-refresh", action="store_true", help="Force fresh download")
    args = parser.parse_args()

    data_dir = Path(os.environ.get("AGENT_DATA_DIR", REPO_ROOT / "data"))
    data_dir.mkdir(parents=True, exist_ok=True)
    cache_file = data_dir / "activities_cache.json"

    needs_download = args.force_refresh or not cache_file.exists()
    if not needs_download:
        mtime = datetime.datetime.fromtimestamp(cache_file.stat().st_mtime)
        age_seconds = (datetime.datetime.now() - mtime).total_seconds()
        if age_seconds > 900:  # 15 minutes
            needs_download = True

    activities = []
    if needs_download:
        api_key = os.environ.get("INTERVALS_API_KEY")
        athlete_id = os.environ.get("INTERVALS_ATHLETE_ID", "0")
        if not api_key or api_key == "YOUR_INTERVALS_API_KEY_HERE":
            # Fallback auf lokale statische intervals_activities.json falls vorhanden
            static_file = REPO_ROOT / "data" / "intervals_activities.json"
            if static_file.exists():
                activities = json.loads(static_file.read_text(encoding="utf-8"))
            else:
                print(json.dumps({
                    "text": "⚠️ **Intervals.icu API-Key fehlt**: Bitte hinterlege `INTERVALS_API_KEY` in der Bundle-Konfiguration.",
                    "chips": []
                }))
                sys.exit(0)
        else:
            oldest_dt = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=args.days)
            oldest_str = oldest_dt.strftime("%Y-%m-%d")
            try:
                activities = fetch_intervals_activities(
                    api_key=api_key,
                    athlete_id=athlete_id,
                    oldest=oldest_str,
                )
                cache_file.write_text(json.dumps(activities, ensure_ascii=False, indent=2), encoding="utf-8")
            except Exception as e:
                # Bei API-Fehler Cache nutzen falls vorhanden
                if cache_file.exists():
                    activities = json.loads(cache_file.read_text(encoding="utf-8"))
                else:
                    print(json.dumps({"text": f"❌ Fehler beim Abruf von Intervals.icu: {e}", "chips": []}))
                    sys.exit(1)
    else:
        activities = json.loads(cache_file.read_text(encoding="utf-8"))

    # Nur Läufe (Run / TrailRun / VirtualRun) filtern
    runs = [a for a in activities if a.get("type") in ("Run", "TrailRun", "VirtualRun")]
    runs.sort(key=lambda r: r.get("start_date_local", ""), reverse=True)

    if not runs:
        print(json.dumps({
            "text": f"Keine Laufaktivitäten in den letzten {args.days} Tagen gefunden.",
            "chips": [{"label": "Intervals.icu öffnen", "url": "https://intervals.icu", "icon": "📊"}]
        }))
        return

    total_km = sum(r.get("distance", 0) for r in runs) / 1000.0
    lines = [f"### 🏃 Laufaktivitäten der letzten {args.days} Tage ({len(runs)} Läufe, {total_km:.1f} km gesamt)\n"]
    lines.append("| Datum | Name | Distanz | Dauer | Pace | Ø HF |")
    lines.append("|---|---|---|---|---|---|")

    for r in runs[:12]:  # Die letzten 12 anzeigen
        date_str = r.get("start_date_local", "")[:10]
        name = r.get("name", "Lauf")
        dist_km = (r.get("distance", 0) or 0) / 1000.0
        dur_str = format_duration(r.get("moving_time") or r.get("elapsed_time"))
        pace_str = format_pace(r.get("average_speed"))
        hr_str = f"{int(r.get('average_heartrate'))} bpm" if r.get("average_heartrate") else "-"
        lines.append(f"| {date_str} | {name} | {dist_km:.2f} km | {dur_str} | {pace_str} | {hr_str} |")

    output_payload = {
        "text": "\n".join(lines),
        "chips": [
            {"label": "📊 Zu Intervals.icu", "url": "https://intervals.icu", "icon": "📊", "action_type": "link"}
        ],
        "data": {"count": len(runs), "total_km": round(total_km, 2)}
    }
    print(json.dumps(output_payload, ensure_ascii=False))

if __name__ == "__main__":
    main()
