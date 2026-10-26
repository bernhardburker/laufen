#!/usr/bin/env python3
"""Tool wrapper for fetching planned workouts from Intervals.icu."""
import argparse
import base64
import datetime
import json
import os
import sys
import urllib.request
import urllib.error
from pathlib import Path

def main():
    parser = argparse.ArgumentParser(description="Fetch upcoming planned workouts from Intervals.icu")
    parser.add_argument("--days", type=int, default=7, help="Days into the future")
    args = parser.parse_args()

    api_key = os.environ.get("INTERVALS_API_KEY")
    athlete_id = os.environ.get("INTERVALS_ATHLETE_ID", "0")

    if not api_key or api_key == "YOUR_INTERVALS_API_KEY_HERE":
        print(json.dumps({
            "text": "ℹ️ **Kein Intervals.icu API-Schlüssel konfiguriert** (oder Demo-Modus). Keine geplanten Workouts geladen.",
            "chips": []
        }))
        return

    today = datetime.date.today()
    end_date = today + datetime.timedelta(days=args.days)
    oldest_str = today.isoformat()
    newest_str = end_date.isoformat()

    url = f"https://intervals.icu/api/v1/athlete/{athlete_id}/events?oldest={oldest_str}&newest={newest_str}"
    auth_str = f"API_KEY:{api_key}"
    b64_auth = base64.b64encode(auth_str.encode("utf-8")).decode("ascii")

    req = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Basic {b64_auth}",
            "User-Agent": "PersonalAssistant-LaufenAgent/1.0",
            "Accept": "application/json"
        }
    )

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            events = json.loads(resp.read().decode("utf-8"))
    except Exception as e:
        print(json.dumps({
            "text": f"❌ Fehler beim Laden der geplanten Events von Intervals.icu: {e}",
            "chips": []
        }))
        return

    # Filtere nach geplanten Workouts / Events
    planned = [e for e in events if e.get("category") in ("WORKOUT", "TARGET") or e.get("type") in ("Run", "Workout")]
    planned.sort(key=lambda x: x.get("start_date_local", ""))

    if not planned:
        print(json.dumps({
            "text": f"In den nächsten {args.days} Tagen ({oldest_str} bis {newest_str}) sind keine Workouts geplant.",
            "chips": [
                {"label": "📅 Intervals.icu Kalender", "url": "https://intervals.icu", "icon": "📅", "action_type": "link"}
            ]
        }))
        return

    lines = [f"### 📅 Geplante Workouts (nächste {args.days} Tage)\n"]
    lines.append("| Datum | Name | Typ | Dauer/Distanz | Notiz |")
    lines.append("|---|---|---|---|---|")

    for p in planned:
        dt = p.get("start_date_local", "")[:10]
        name = p.get("name", "Workout")
        w_type = p.get("type", "Run")
        dist = p.get("distance")
        dur = p.get("moving_time") or p.get("elapsed_time")
        detail = f"{dist/1000:.1f} km" if dist else (f"{int(dur/60)} min" if dur else "-")
        desc = (p.get("description") or "").replace("\n", " ")[:40]
        lines.append(f"| {dt} | {name} | {w_type} | {detail} | {desc} |")

    output_payload = {
        "text": "\n".join(lines),
        "chips": [
            {"label": "📅 Intervals.icu Kalender", "url": "https://intervals.icu", "icon": "📅", "action_type": "link"}
        ],
        "data": {"count": len(planned)}
    }
    print(json.dumps(output_payload, ensure_ascii=False))

if __name__ == "__main__":
    main()
