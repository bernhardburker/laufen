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

from src.analysis.coach_history import (
    find_runs_since,
    get_open_recommendation,
    initialize_cold_start_history,
    load_coach_history,
    update_history_with_coach_review,
)
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


def extract_athlete_display_name(athlete_info: Dict[str, Any]) -> str:
    """Extract a friendly first name or display name for coaching dialogue."""
    if "display_name" in athlete_info and athlete_info["display_name"]:
        return str(athlete_info["display_name"]).strip()
    name = str(athlete_info.get("name") or "").strip()
    if not name:
        return "Athlet"
    # Match CamelCase like "BerniBurker" -> "Berni"
    camel_match = re.match(r"^([A-Z][a-z]{2,})[A-Z]", name)
    if camel_match:
        return camel_match.group(1)
    # Default to first whitespace-delimited word (e.g. "Max Mustermann" -> "Max")
    return name.split()[0]


def determine_athlete_classification(
    runs: List[Dict[str, Any]],
    athlete_profile: Optional[Dict[str, Any]] = None,
    trends: Optional[List[Dict[str, Any]]] = None,
    form_data: Optional[Dict[str, Any]] = None,
) -> Dict[str, str]:
    """Classify athlete level (Hobby, Erfahren, Profi) from profile or metrics."""
    athlete_info = (athlete_profile or {}).get("athlete", {})
    explicit_level = athlete_info.get("classification") or athlete_info.get("level")
    if explicit_level:
        return {
            "category": str(explicit_level),
            "description": "Im Athleten-Profil hinterlegt",
            "source": "config",
        }

    # Fallback when no activities available
    if not runs:
        return {
            "category": "Einsteiger / Wiedereinsteiger",
            "description": "Keine aktuellen Läufe verzeichnet",
            "source": "dynamic",
        }

    num_weeks = max(len(trends or []), 1)
    total_km = sum(
        float(r.get("distance_km") or (r.get("distance", 0) / 1000.0)) for r in runs
    )
    avg_weekly_km = total_km / num_weeks
    avg_runs_per_week = len(runs) / num_weeks
    ctl = float((form_data or {}).get("latest_ctl") or 0.0)

    if avg_weekly_km < 20.0 or avg_runs_per_week < 2.5 or ctl < 18.0:
        category = "Hobbyläufer (Basisaufbau)"
        desc = f"Überschaubares Pensum ({avg_weekly_km:.1f} km/Wo., {avg_runs_per_week:.1f} Läufe/Wo., CTL {ctl:.1f})"
    elif avg_weekly_km < 45.0 or avg_runs_per_week < 3.5 or ctl < 38.0:
        category = "Regelmäßiger Hobbyläufer"
        desc = f"Solides Breitensportpensum ({avg_weekly_km:.1f} km/Wo., {avg_runs_per_week:.1f} Läufe/Wo., CTL {ctl:.1f})"
    elif avg_weekly_km < 70.0 or avg_runs_per_week < 4.5 or ctl < 60.0:
        category = "Ambitionierter Läufer"
        desc = f"Ambitioniertes Pensum ({avg_weekly_km:.1f} km/Wo., {avg_runs_per_week:.1f} Läufe/Wo., CTL {ctl:.1f})"
    else:
        category = "Leistungssportler / Profi"
        desc = f"Hohes Wettkampfpensum ({avg_weekly_km:.1f} km/Wo., {avg_runs_per_week:.1f} Läufe/Wo., CTL {ctl:.1f})"

    return {
        "category": category,
        "description": desc,
        "source": "dynamic",
    }


def save_athlete_classification(
    athlete_file: Optional[Path], classification_str: str
) -> None:
    """Persist athlete classification into athlete configuration JSON."""
    if not athlete_file or not athlete_file.is_file():
        return
    try:
        with open(athlete_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        if "athlete" in data and isinstance(data["athlete"], dict):
            if data["athlete"].get("classification") != classification_str:
                data["athlete"]["classification"] = classification_str
                with open(athlete_file, "w", encoding="utf-8") as f:
                    json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def build_coach_prompt(
    activities_file: Path,
    athlete_file: Optional[Path] = None,
    history_file: Optional[Path] = None,
    save_classification: bool = True,
) -> str:
    """Prepare a detailed, context-rich prompt for Antigravity AI Coach."""
    activities = load_activities(activities_file)
    runs = filter_runs(activities)
    athlete_profile = load_athlete_profile(athlete_file)
    athlete_info = athlete_profile.get("athlete", {})
    athlete_name = extract_athlete_display_name(athlete_info)
    full_name = athlete_info.get("name") or athlete_name

    if not runs:
        return (
            "Du bist ein professioneller, bodenständiger Lauf- und Ausdauertrainer. "
            f"Für {athlete_name} liegen aktuell noch keine Laufaktivitäten vor. "
            f"Gib {athlete_name} eine kurze, sachliche und realistische Empfehlung für den Einstieg in das strukturierte Lauftraining als JSON. "
            "Vermeide jegliche Übertreibung oder Schmeichelei."
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

    # Weekly stats from recent trends
    num_trend_weeks = max(len(trends), 1)
    avg_weekly_km = sum(t["total_distance_km"] for t in trends) / num_trend_weeks
    avg_runs_per_week = sum(t["runs_count"] for t in trends) / num_trend_weeks

    # Athlete classification
    classification = determine_athlete_classification(
        processed_runs, athlete_profile, trends, form_data
    )
    if save_classification and athlete_file:
        save_athlete_classification(athlete_file, classification["category"])

    # Athlete specs
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

    # Incorporate Coach History & Feedback Loop Context
    history_section = ""
    history_list = load_coach_history(history_file) if history_file and history_file.is_file() else []
    open_rec = get_open_recommendation(history_list)
    if open_rec:
        rec_date = open_rec.get("date", "Unbekannt")
        rec_text = open_rec.get("recommendation", "")
        runs_since = find_runs_since(processed_runs, rec_date)
        runs_since_lines = []
        for r in runs_since:
            d_s = str(r.get("date", ""))[:10]
            z_pct = round((r.get("z1_z2_seconds", 0) / r.get("total_zone_seconds", 1)) * 100.0, 1) if r.get("total_zone_seconds", 0) > 0 else 0.0
            runs_since_lines.append(
                f"  * {d_s} | {r.get('name', 'Lauf')}: {r.get('distance_km', 0.0):.2f} km, Pace: {r.get('pace_str', '-')}, Ø Puls: {int(round(r.get('avg_hr', 0)))} bpm, Z1/Z2: {z_pct}%, Load: {int(round(r.get('training_load', 0)))}"
            )

        runs_since_str = "\n".join(runs_since_lines) if runs_since_lines else "  * Noch keine neuen Läufe seit dieser Empfehlung eingetragen."

        history_section = f"""
### Bisheriges Coaching-Gedächtnis & Vorgaben (Feedback-Schleife):
- Bisherige offene Empfehlung (vom {rec_date}):
  "{rec_text}"
- Seitdem absolvierte Läufe ({len(runs_since)} Einheit(en)):
{runs_since_str}

### Feedback-Schleife & Adaptive Trainingssteuerung:
Analysiere ehrlich und nüchtern den Abgleich zwischen der letzten Vorgabe und den tatsächlich absolvierten Läufen:
1. Compliance: Hat der Athlet die Empfehlung befolgt, oder ist er z.B. zu schnell gelaufen, hat einen Tempolauf / Intervalle eingeschoben oder Einheiten ausgelassen?
2. Adaptive Steuerung: Wenn der Athlet ungeplant einen intensiven Tempolauf oder Schwellenlauf absolviert hat, schimpfe nicht, sondern reagiere adaptiv – plane als Nächstes gezielt eine lockere Einheit / aktive Regeneration (Zone 1/2) ein, um Überlastung zu verhindern.
3. Physiologischer Effekt: Was lässt sich aus den Daten über die aerobe Entwicklung, Pace/Puls-Verhältnis und Ermüdung (TSB) ablesen?
"""

    prompt = f"""Du bist ein erfahrener Lauf- und Ausdauertrainer mit Spezialisierung auf sportwissenschaftliche Trainingssteuerung, Herzfrequenz-Zonen und polarisiertes Training (80/20-Methode nach Stephen Seiler / Matt Fitzgerald).

Analysiere die aktuellen Trainingsdaten des Athleten ({athlete_name}) und erstelle eine vollständige, dynamische Trainings-Zusammenfassung und Handlungsempfehlungen für das Dashboard.

### Tonalität & Coaching-Haltung (STRIKTE VORGABE):
- **Basiere deine Analyse und Tonalität ausschließlich auf den realen Daten**:
  Der Athlet ({athlete_name}) ist anhand der Daten eingestuft als: **{classification['category']}** ({classification['description']}).
  Passe deine Sprache, Erwartungshaltung und Ratschläge exakt diesem aktuellen Leistungsstand an.
- **Keine Übertreibungen, kein Hype, keine Schmeicheleien**: Verzichte komplett auf Schmeicheleien, künstlichen Cheerleader-Ton und unrealistische Floskeln ("Zeiten werden explodieren", "herausragendes Fundament", "Spitzenleistung").
- **Sachlich, ehrlich und ungeschminkt auf Augenhöhe**: Benenne Schwachstellen (z.B. zu geringer Z1/Z2-Anteil, zu hohes Grundlagentempo, sprunghafte Belastungsspitzen) direkt und physiologisch fundiert. Behandle Hobbyläufer wie Hobbyläufer und Leistungssportler wie Leistungssportler – respektvoll, partnerschaftlich und unaufgeregt.
- **Herzfrequenz-Steuerung bei Grundlageneinheiten**: Für Zone-2-Läufe NIEMALS eine feste Ziel-Pace vorgeben! Grundlagenläufe werden ausschließlich über die Herzfrequenz (< {z2_max} bpm) gesteuert. Die Pace ist ein reines Resultat und zweitrangig (Gehpausen an Anstiegen sind normal und erwünscht).

### Athleten-Profil:
- Name: {full_name} ({athlete_name})
- Einstufung / Level: {classification['category']} ({classification['description']})
- Ruhepuls: {resting_hr} bpm | Maximalpuls: {max_hr} bpm
- Laktatschwelle (LTHR): {lthr} bpm
- Grundlagen-Obergrenze (Zone 2 max): {z2_max} bpm
- Bisherige Zone 1 & 2 Disziplin im gesamten Zeitraum: {overall_z1_z2_pct}% (Soll: ~80%)
- Aktuelle Form (TSB): {form_data['current_tsb']:+.1f} (Fitness CTL: {form_data['latest_ctl']}, Ermüdung ATL: {form_data['latest_atl']})
- Aktuelles Pensum: Ø {avg_weekly_km:.1f} km / Woche bei Ø {avg_runs_per_week:.1f} Läufen
{history_section}
### Letzte Aktivitäten:
{chr(10).join(runs_lines)}

### Wöchentliche Entwicklung (letzte 4 Wochen):
{chr(10).join(trends_lines)}

### Aktuelle Wettkampf-Prognosen (Riegel-Modell):
{chr(10).join(pred_lines)}

---

### Deine Coaching-Aufgabe:
Erstelle eine vollständige, dynamische und ehrliche Trainingsbewertung für {athlete_name}.
Antworte AUSSCHLIESSLICH mit einem validen JSON-Objekt (kein Markdown-Backticks ```json davor oder danach, kein sonstiger Text außenherum) in folgender Struktur:

{{
  "status_title": "Prägnanter Status-Titel basierend auf den Daten (z.B. Aerobe Basis fehlt / Hoher Grauzonen-Anteil / Solide Aufbauphase)",
  "status_badge": "Kurzer Badge-Text (z.B. Intensität drosseln / Z2-Defizit / Pensum stabilisieren)",
  "status_level": "warning" oder "info" oder "success",
  "coach_commentary": "Dein ehrlicher, bodenständiger Coaching-Brief an {athlete_name} als Markdown formatiert (ca. 160-220 Wörter) mit Absätzen, Überschriften (###) und einer konkreten Wochenaufgabe für nächste Woche. Bleibe nüchtern, sachlich und realistisch – absolut keine Übertreibungen oder Schmeicheleien.",
  "history_evaluation": {{
    "compliance_status": "erfüllt" oder "angepasst_nach_tempo" oder "teilweise" oder "verfehlt",
    "actual_summary": "Nüchterne Zusammenfassung der seit der letzten Vorgabe absolvierten Läufe...",
    "effect_analysis": "Messbarer physiologischer Effekt (Pace/Puls-Verhältnis, Ermüdung, Zonen)...",
    "next_recommendation": "Konkrete nächste Aufgabe / Trainingsanweisung...",
    "next_workout_type": "easy_run" oder "intervals" oder "tempo_run" oder "recovery_run",
    "next_target_hr": {z2_max},
    "next_duration_min": 45
  }},
  "insights": [
    {{
      "title": "Titel der Erkenntnis (z.B. Intensitätsfalle / Fehlende aerobe Basis)",
      "type": "warning" oder "info" oder "success",
      "badge": "Kurzer Tag (z.B. {overall_z1_z2_pct}% Z1/Z2)",
      "text": "Nüchterne, faktenbasierte Erklärung mit den echten gemessenen Werten..."
    }},
    {{
      "title": "Zweite Erkenntnis (z.B. Trainingsvolumen & Häufigkeit)",
      "type": "info",
      "badge": "Ø {avg_weekly_km:.1f} km / Wo.",
      "text": "Realistische Einschätzung zu aktuellem Umfang, Häufigkeit und Long Run..."
    }}
  ],
  "recommendations": [
    {{
      "title": "Titel Empfehlung 1 (z.B. Grundlagenläufe strikt nach Herzfrequenz steuern)",
      "tag": "Priorität 1: Aerobe Basis",
      "tag_class": "tag-amber",
      "text": "Konkrete Praxisanweisung (Puls strikt unter {z2_max} bpm, Pace ignorieren, bei Bedarf Gehpausen)..."
    }},
    {{
      "title": "Titel Empfehlung 2 (z.B. Klare Reiztrennung nach 80/20)",
      "tag": "Trainingsstruktur",
      "tag_class": "tag-cyan",
      "text": "Trennung zwischen 80% lockerer Z2-Basis und gezielten Schwellenreizen an LTHR {lthr} bpm..."
    }},
    {{
      "title": "Titel Empfehlung 3 (z.B. Behutsamer Umfangsausbau)",
      "tag": "Umfangsaufbau",
      "tag_class": "tag-green",
      "text": "Empfehlung für das nächste Wochenziel, angepasst an das Niveau ({classification['category']})..."
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
        "--history",
        type=Path,
        default=PROJECT_ROOT / "data" / "coach_history.json",
        help="Path to coach history JSON",
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

        profile = {}
        if args.athlete and args.athlete.is_file():
            profile = load_athlete_profile(args.athlete)
            athlete_info = profile.get("athlete", {})
            if "athlete" not in parsed or not isinstance(parsed.get("athlete"), dict):
                parsed["athlete"] = {}
            parsed["athlete"]["name"] = extract_athlete_display_name(athlete_info)
            if athlete_info.get("classification"):
                parsed["athlete"]["classification"] = athlete_info["classification"]

        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(parsed, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"Structured AI summary saved: {args.output_json}")

        if "coach_commentary" in parsed and parsed["coach_commentary"]:
            args.output_md.write_text(parsed["coach_commentary"], encoding="utf-8")
            print(f"Extracted AI commentary saved: {args.output_md}")

        # Update coach history if evaluation present
        if "history_evaluation" in parsed and isinstance(parsed["history_evaluation"], dict):
            activities = load_activities(args.input) if args.input.is_file() else []
            runs = filter_runs(activities)
            history = load_coach_history(args.history)
            if not history and runs:
                history = initialize_cold_start_history(runs, profile, args.history)
            open_rec = get_open_recommendation(history)
            rec_date = open_rec.get("date", "2000-01-01") if open_rec else "2000-01-01"
            runs_since = find_runs_since(runs, rec_date)
            update_history_with_coach_review(
                history=history,
                review_data=parsed["history_evaluation"],
                runs_since=runs_since,
                athlete_profile=profile,
                history_file=args.history,
            )
            print(f"Coach history successfully updated: {args.history}")

        return 0

    try:
        prompt = build_coach_prompt(args.input, args.athlete, history_file=args.history)
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
