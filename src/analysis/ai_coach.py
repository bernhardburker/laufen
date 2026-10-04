#!/usr/bin/env python3
"""AI Running Coach context preparation and commentary integration.

Extracts recent running activity metrics, training load, heart rate zone
discipline, and athlete profile targets to prepare a targeted coaching prompt
for Antigravity CLI (agy), and parses/integrates generated AI coaching notes
and structured training summaries.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.forecast import calculate_fitness_form_trend, predict_race_times
from src.analysis.trends import (
    aggregate_weekly_trends,
    calculate_run_metrics,
    filter_runs,
    format_duration,
    format_pace,
    load_activities,
)


def load_athlete_profile(file_path: Optional[Path] = None) -> Dict[str, Any]:
    athlete_path = file_path or (PROJECT_ROOT / "config" / "athlete.json")
    if athlete_path.is_file():
        try:
            with open(athlete_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}
    return {}


def build_coach_prompt(
    activities_file: Path,
    athlete_file: Optional[Path] = None,
) -> str:
    """Prepare a detailed, context-rich prompt for Antigravity AI Coach."""
    activities = load_activities(activities_file)
    runs = filter_runs(activities)
    athlete_profile = load_athlete_profile(athlete_file)

    if not runs:
        return (
            "Du bist ein professioneller Lauf- und Ausdauertrainer. "
            "Es liegen aktuell noch keine Laufaktivitäten vor. "
            "Gib dem Läufer eine kurze, motivierende Empfehlung für den Einstieg in das strukturierte Lauftraining als JSON."
        )

    processed_runs = []
    for r in runs:
        m = calculate_run_metrics(r)
        m["moving_time_str"] = format_duration(m["moving_time_s"])
        m["pace_str"] = format_pace(m["pace_s_per_km"])
        processed_runs.append(m)

    # Sort reverse chronological
    processed_runs.sort(key=lambda r: r.get("date") or "", reverse=True)
    recent_runs = processed_runs[:6]

    trends = aggregate_weekly_trends(runs, max_weeks=8)
    form_data = calculate_fitness_form_trend(runs)
    predictions = predict_race_times(processed_runs, athlete_profile)

    # Athlete specs
    athlete_info = athlete_profile.get("athlete", {})
    hr_info = athlete_info.get("heart_rate", {})
    lthr = hr_info.get("lthr", 166)
    max_hr = hr_info.get("max_hr", 183)
    resting_hr = hr_info.get("resting_hr", 51)
    z2_max = (
        hr_info.get("zones", {}).get("z2_aerobic", {}).get("max", 148)
    )

    # Overall base discipline
    total_z1_z2_s = sum(r["z1_z2_seconds"] for r in processed_runs)
    total_zone_s = sum(r["total_zone_seconds"] for r in processed_runs)
    overall_z1_z2_pct = (
        round((total_z1_z2_s / total_zone_s) * 100.0, 1)
        if total_zone_s > 0
        else 0.0
    )

    # Format runs table for prompt
    runs_lines = []
    for r in recent_runs:
        date_str = str(r.get("date", ""))[:10]
        name = r.get("name", "Lauf")
        dist = r.get("distance_km", 0.0)
        pace = r.get("pace_str", "-")
        hr = int(r.get("avg_hr", 0))
        z_sec = r.get("z1_z2_seconds", 0)
        tot_sec = r.get("total_zone_seconds", 0)
        z_pct = round((z_sec / tot_sec) * 100.0, 1) if tot_sec > 0 else 0.0
        load = int(r.get("training_load", 0))
        runs_lines.append(
            f"- {date_str} | {name}: {dist:.2f} km in {r['moving_time_str']} (Pace: {pace}, Puls: {hr} bpm, Z1/Z2-Anteil: {z_pct}%, Load: {load})"
        )

    # Format trends for prompt
    trends_lines = []
    for t in trends[-4:]:
        trends_lines.append(
            f"- {t['iso_week']}: {t['total_distance_km']:.1f} km, {t['runs_count']} Läufe, Ø Pace: {t['avg_pace']}, Ø Puls: {t['avg_hr']:.0f} bpm, Z1/Z2: {t['z1_z2_pct']}%, Load: {t['total_load']:.0f}"
        )

    # Format predictions
    pred_lines = []
    for p in predictions:
        pred_lines.append(
            f"- {p['name']}: {p['predicted_time_str']} (Pace: {p['target_pace_str']}) - {p['readiness']}"
        )

    prompt = f"""Du bist ein erfahrener Lauf- und Ausdauertrainer mit Spezialisierung auf sportwissenschaftliche Trainingssteuerung, Herzfrequenz-Zonen und polarisiertes Training (80/20-Methode nach Stephen Seiler / Matt Fitzgerald).

Analysiere die aktuellen Trainingsdaten des Athleten (Berni) und erstelle die vollständige dynamische Trainings-Zusammenfassung und Handlungsempfehlungen für das Dashboard.

### Athleten-Profil:
- Ruhepuls: {resting_hr} bpm | Maximalpuls: {max_hr} bpm
- Laktatschwelle (LTHR): {lthr} bpm
- Grundlagen-Obergrenze (Zone 2 max): {z2_max} bpm
- Bisherige Zone 1 & 2 Disziplin im gesamten Zeitraum: {overall_z1_z2_pct}% (Soll: ~80%)
- Aktuelle Form (TSB): {form_data['current_tsb']:+.1f} (Fitness CTL: {form_data['latest_ctl']}, Ermüdung ATL: {form_data['latest_atl']})

### Letzte Aktivitäten:
{chr(10).join(runs_lines)}

### Wöchentliche Entwicklung (letzte 4 Wochen):
{chr(10).join(trends_lines)}

### Aktuelle Wettkampf-Prognosen (Riegel-Modell):
{chr(10).join(pred_lines)}

---

### Deine Coaching-Aufgabe:
Erstelle eine vollständige, dynamische und ehrliche Trainingsbewertung.
Antworte AUSSCHLIESSLICH mit einem validen JSON-Objekt (kein Markdown-Backticks ```json davor oder danach, kein sonstiger Text außenherum) in folgender Struktur:

{{
  "status_title": "Prägnanter Status-Titel (z.B. Aufbauphase mit Optimierungspotenzial / Z2-Fokus nötig)",
  "status_badge": "Kurzer Badge-Text (z.B. Intensität drosseln / Hervorragende Basis)",
  "status_level": "warning" oder "success" oder "info",
  "coach_commentary": "Dein persönlicher, motivierender Coaching-Brief an Berni als Markdown formatiert (ca. 180-260 Wörter) mit Absätzen, Überschriften (###) und einer konkreten Wochenaufgabe für nächste Woche.",
  "insights": [
    {{
      "title": "Titel der Erkenntnis (z.B. Intensitätsfalle / Zu hohes Tempo)",
      "type": "warning" oder "success" oder "info",
      "badge": "Kurzer Tag (z.B. 10.6% Z1/Z2)",
      "text": "Detaillierte, faktenbasierte Erklärung mit deinen echten gemessenen Werten..."
    }},
    {{
      "title": "Zweite Erkenntnis (z.B. Trainingsvolumen & Kontinuität)",
      "type": "info",
      "badge": "Ø 13.1 km / Wo.",
      "text": "Einschätzung zu Umfang, Regelmäßigkeit und Long Run..."
    }}
  ],
  "recommendations": [
    {{
      "title": "Titel Empfehlung 1 (z.B. Pace bei Grundlagenläufen bewusst drosseln)",
      "tag": "Priorität 1: Grundlagenausdauer",
      "tag_class": "tag-amber",
      "text": "Konkrete Praxisanweisung (z.B. Pace 6:45-7:15 min/km, Puls strikt unter {z2_max} bpm)..."
    }},
    {{
      "title": "Titel Empfehlung 2 (z.B. Klare Reiztrennung / Polarisiertes 80/20)",
      "tag": "Trainingsstruktur",
      "tag_class": "tag-cyan",
      "text": "Trennung zwischen 80% lockerer Z2-Basis und gezielten Schwellenreizen an LTHR {lthr} bpm..."
    }},
    {{
      "title": "Titel Empfehlung 3 (z.B. Volumen nach der 10%-Regel steigern)",
      "tag": "Verletzungsprävention",
      "tag_class": "tag-green",
      "text": "Empfehlung für das nächste Wochenziel und optionale Laufeinheiten..."
    }}
  ]
}}
"""
    return prompt.strip()


def parse_coach_response(raw_text: str) -> Optional[Dict[str, Any]]:
    """Parse and validate JSON response from Antigravity AI Coach."""
    if not raw_text or not raw_text.strip():
        return None

    cleaned = raw_text.strip()
    # Strip markdown code block if present
    match = re.search(r"```(?:json)?\s*([\s\S]*?)\s*```", cleaned)
    if match:
        cleaned = match.group(1).strip()
    else:
        # Find first { and last }
        start = cleaned.find("{")
        end = cleaned.rfind("}")
        if start != -1 and end != -1 and end > start:
            cleaned = cleaned[start : end + 1]

    try:
        data = json.loads(cleaned)
        if not isinstance(data, dict):
            return None

        # Validate minimum expected fields
        if "status_title" in data and "insights" in data and "recommendations" in data:
            return data
    except Exception:
        pass

    return None


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Prepare AI coach prompt or parse AI coach response."
    )
    parser.add_argument(
        "--prepare-prompt",
        action="store_true",
        help="Generate AI Coach prompt",
    )
    parser.add_argument(
        "--parse-response",
        type=Path,
        help="Parse raw agy response file and save structured JSON and commentary",
    )
    parser.add_argument(
        "--input",
        "-i",
        type=Path,
        default=PROJECT_ROOT / "data" / "intervals_activities.json",
        help="Path to activity JSON",
    )
    parser.add_argument(
        "--athlete",
        "-a",
        type=Path,
        default=PROJECT_ROOT / "config" / "athlete.json",
        help="Path to athlete profile JSON",
    )
    parser.add_argument(
        "--output-prompt",
        type=Path,
        help="Write generated prompt directly to file",
    )
    parser.add_argument(
        "--output-json",
        type=Path,
        default=PROJECT_ROOT / "data" / "ai_coach_summary.json",
        help="Path to write parsed structured JSON summary",
    )
    parser.add_argument(
        "--output-md",
        type=Path,
        default=PROJECT_ROOT / "data" / "ai_coach_commentary.md",
        help="Path to write extracted Markdown commentary",
    )

    args = parser.parse_args()

    if args.parse_response:
        if not args.parse_response.exists():
            print(f"Error: Response file not found: {args.parse_response}", file=sys.stderr)
            return 1
        raw_text = args.parse_response.read_text(encoding="utf-8")
        parsed = parse_coach_response(raw_text)
        if not parsed:
            print("Warning: Could not parse valid structured JSON from agy response.", file=sys.stderr)
            return 1

        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(parsed, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"Structured AI summary saved: {args.output_json}")

        if "coach_commentary" in parsed and parsed["coach_commentary"]:
            args.output_md.write_text(parsed["coach_commentary"], encoding="utf-8")
            print(f"Extracted AI commentary saved: {args.output_md}")
        return 0

    try:
        prompt = build_coach_prompt(args.input, args.athlete)
        if args.output_prompt:
            args.output_prompt.parent.mkdir(parents=True, exist_ok=True)
            args.output_prompt.write_text(prompt, encoding="utf-8")
        else:
            print(prompt)
        return 0
    except Exception as e:
        print(f"Error generating AI coach prompt: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
