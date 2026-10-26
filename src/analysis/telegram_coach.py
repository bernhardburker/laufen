#!/usr/bin/env python3
"""telegram_coach.py

Parses athlete text feedback received via Telegram and computes updated
workout parameters or coaching responses using LLMClient or deterministic
fallback heuristics.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass, field
import json
from pathlib import Path
import re
import sys
from typing import Any, Dict, List, Optional

PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.llm import LLMClient, get_llm_client
from src.planner.schedule_workout import build_workout_payload


SUPPORTED_WORKOUT_TYPES = {
    "easy_run",
    "recovery_run",
    "long_run",
    "intervals",
    "tempo_run",
    "threshold_intervals",
    "micro_threshold",
    "progressive_threshold",
    "fartlek",
}


@dataclass
class CoachIntent:
    """Structured coaching decision derived from an athlete's Telegram message."""

    action: str  # "accept", "modify", "rest_day", "question"
    reply_text: str
    workout_params: Dict[str, Any] = field(default_factory=dict)


TELEGRAM_COACH_SYSTEM_PROMPT = """You are an intelligent, empathetic German Running Coach communicating with an athlete via Telegram.
The athlete is responding to their daily workout proposal.

Analyze the athlete's message and the current context (readiness markers, guardrails, and currently scheduled workout).
Return ONLY a valid JSON object (no markdown wrapper outside JSON) with this exact schema:
{
  "action": "accept" | "modify" | "rest_day" | "question",
  "reply_text": "Friendly, concise German coaching reply (max 3-4 sentences, suitable for Telegram)",
  "workout_params": {
    "type": "easy_run" | "recovery_run" | "long_run" | "intervals" | "tempo_run" | "threshold_intervals" | "micro_threshold" | "progressive_threshold" | "fartlek",
    "duration": "35m",
    "warmup": "5m",
    "cooldown": "5m",
    "target": "Z2 HR",
    "reps": 4,
    "work": "1km",
    "recovery": "2m",
    "name": "Optional custom workout title"
  }
}

Rules:
1. If the athlete says they have less time (e.g. "Habe heute nur 35 min Zeit"), set action="modify", adjust "duration" to "35m" (and if <= 35m, use "warmup": "5m", "cooldown": "5m").
2. If the athlete feels tired, heavy legs, or sluggish ("Fühle mich schlapp", "schwere Beine", "locker"), set action="modify", downgrade intensity to "easy_run" or "recovery_run" with target "Z2 HR" or "Z1 HR".
3. If the athlete wants to take a day off, is sick, or cannot run today ("Heute Ruhetag", "krank", "schaffe es heute nicht"), set action="rest_day" and leave workout_params empty {}.
4. If the athlete confirms the plan ("Passt", "Mach ich so", "Okay", "Super"), set action="accept".
5. Always respond in German ("Du"-Form) in reply_text.
"""


def build_telegram_coach_prompt(
    user_text: str,
    athlete_name: str,
    current_proposal: Optional[Dict[str, Any]] = None,
    markers: Optional[Dict[str, Any]] = None,
) -> str:
    """Builds the context prompt for the LLM intent parser."""
    context_payload = {
        "athlete_name": athlete_name,
        "current_scheduled_workout": current_proposal or {},
        "readiness_and_guardrails": {
            "readiness": (markers or {}).get("readiness", {}),
            "guardrails": (markers or {}).get("guardrails", {}),
            "periodization": (markers or {}).get("periodization", {}),
        },
        "athlete_message": user_text,
    }
    return (
        "Current Coaching Context (JSON):\n"
        f"{json.dumps(context_payload, indent=2, ensure_ascii=False)}\n\n"
        f"Athlete message: \"{user_text}\"\n"
        "Respond with the JSON decision object:"
    )


def _extract_json_dict(raw_text: str) -> Optional[Dict[str, Any]]:
    """Extracts a JSON dictionary from raw LLM text or fenced code blocks."""
    cleaned = raw_text.strip()
    fence_match = re.search(r"```(?:json)?\s*(\{.*?\})\s*```", cleaned, re.DOTALL)
    if fence_match:
        cleaned = fence_match.group(1)
    else:
        brace_match = re.search(r"(\{.*\})", cleaned, re.DOTALL)
        if brace_match:
            cleaned = brace_match.group(1)
    try:
        parsed = json.loads(cleaned)
        if isinstance(parsed, dict):
            return parsed
    except Exception:
        pass
    return None


def heuristic_parse_intent(
    user_text: str,
    athlete_name: str = "Athlet",
    current_proposal: Optional[Dict[str, Any]] = None,
) -> CoachIntent:
    """Deterministic fallback intent parser when LLM is unavailable or for clear patterns."""
    text_lower = user_text.strip().lower()
    proposal = current_proposal or {}
    target_date = proposal.get("target_date") or "today"

    # 1. Rest day / skip patterns
    rest_keywords = (
        "ruhetag",
        "pause",
        "krank",
        "schaffe es heute nicht",
        "keine zeit",
        "ausfallen",
        "streichen",
        "nicht laufen",
    )
    if any(k in text_lower for k in rest_keywords):
        return CoachIntent(
            action="rest_day",
            reply_text=(
                f"Alles klar, {athlete_name}! Ich habe das geplante Training aus deinem "
                "Intervals.icu-Kalender entfernt. Gönn dir heute den Ruhetag zur Regeneration!"
            ),
            workout_params={},
        )

    # 2. Pure acceptance patterns
    accept_keywords = ("passt", "ok", "okay", "super", "mach ich", "danke", "perfekt", "einverstanden", "👍")
    if text_lower in accept_keywords or (
        any(k in text_lower for k in accept_keywords)
        and not any(w in text_lower for w in ("min", "schlapp", "müde", "locker", "nur", "aber", "kürzer"))
    ):
        w_name = proposal.get("name") or "dein geplantes Training"
        return CoachIntent(
            action="accept",
            reply_text=f"Perfekt, {athlete_name}! '{w_name}' bleibt wie geplant auf deiner Uhr. Viel Spaß beim Lauf!",
            workout_params={},
        )

    # 3. Check for time constraints (e.g. "35 min", "30 Minuten", "40m")
    dur_match = re.search(r"\b(\d{2,3})\s*(?:min(?:uten)?|m)\b", text_lower)
    minutes = int(dur_match.group(1)) if dur_match else None

    # 4. Check for fatigue / easier request
    fatigue_keywords = (
        "schlapp",
        "müde",
        "schwere beine",
        "erschöpft",
        "locker",
        "ruhig",
        "gemütlich",
        "weniger intensiv",
        "z2",
        "easy",
    )
    wants_easier = any(k in text_lower for k in fatigue_keywords)

    # Check if athlete explicitly asks for intervals or tempo
    wants_intervals = any(k in text_lower for k in ("intervall", "speed", "schnell"))
    wants_tempo = any(k in text_lower for k in ("tempo", "schwelle"))

    if minutes is not None or wants_easier or wants_intervals or wants_tempo:
        # Determine workout type
        if wants_easier:
            w_type = "recovery_run" if (minutes and minutes <= 30) else "easy_run"
        elif wants_intervals:
            w_type = "intervals"
        elif wants_tempo:
            w_type = "tempo_run"
        else:
            w_type = proposal.get("workout_type") or "easy_run"
            if w_type not in SUPPORTED_WORKOUT_TYPES:
                w_type = "easy_run"
            # If time is very short (< 35m) and was threshold/intervals, simplify to easy_run or short intervals
            if minutes is not None and minutes <= 35 and w_type in ("threshold_intervals", "long_run"):
                w_type = "easy_run"

        final_min = minutes or int(proposal.get("duration_min") or (35 if wants_easier else 45))
        dur_str = f"{final_min}m"
        warmup_str = "5m" if final_min <= 35 else "10m"
        cooldown_str = "5m"
        target_str = "Z1 HR" if w_type == "recovery_run" else "Z2 HR"

        if w_type == "easy_run":
            custom_name = f"Easy Run - Angepasst ({dur_str} Z2)"
        elif w_type == "recovery_run":
            custom_name = f"Recovery Run - Locker ({dur_str} Z1)"
        elif w_type == "tempo_run":
            main_min = max(10, final_min - 15)
            dur_str = f"{main_min}m"
            custom_name = f"Tempo Run ({final_min}m gesamt)"
            target_str = "Z3-Z4 HR"
        else:
            custom_name = f"Training - Angepasst ({dur_str})"

        params = {
            "type": w_type,
            "duration": dur_str,
            "warmup": warmup_str,
            "cooldown": cooldown_str,
            "target": target_str,
            "date": target_date,
            "name": custom_name,
        }

        reasons = []
        if minutes is not None:
            reasons.append(f"auf {final_min} Minuten gekürzt")
        if wants_easier:
            reasons.append("Intensität auf lockeren Grundlagenbereich reduziert")
        reason_str = " und ".join(reasons) if reasons else "angepasst"

        return CoachIntent(
            action="modify",
            reply_text=(
                f"Verstanden, {athlete_name}! Ich habe deinen Plan {reason_str}: "
                f"'{custom_name}' wurde direkt an Intervals.icu & deine Garmin-Uhr übertragen."
            ),
            workout_params=params,
        )

    # Default question / general response
    return CoachIntent(
        action="question",
        reply_text=(
            f"Danke für deine Nachricht, {athlete_name}! Du kannst mir jederzeit schreiben, "
            "wenn ich das heutige Training kürzen (z.B. 'Nur 35 min Zeit'), lockerer gestalten "
            "('Fühle mich schlapp') oder als Ruhetag streichen soll."
        ),
        workout_params={},
    )


def evaluate_athlete_feedback(
    user_text: str,
    athlete_name: str = "Athlet",
    current_proposal: Optional[Dict[str, Any]] = None,
    markers: Optional[Dict[str, Any]] = None,
    llm_client: Optional[LLMClient] = None,
) -> CoachIntent:
    """Evaluates athlete text message via LLMClient with deterministic fallback."""
    if llm_client is not None:
        prompt = build_telegram_coach_prompt(
            user_text=user_text,
            athlete_name=athlete_name,
            current_proposal=current_proposal,
            markers=markers,
        )
        res = llm_client.generate(prompt=prompt, system_prompt=TELEGRAM_COACH_SYSTEM_PROMPT)
        if res.success and res.text:
            parsed = _extract_json_dict(res.text)
            if parsed and "action" in parsed and "reply_text" in parsed:
                action = str(parsed["action"]).strip().lower()
                if action in ("accept", "modify", "rest_day", "question"):
                    w_params = parsed.get("workout_params") or {}
                    if not isinstance(w_params, dict):
                        w_params = {}
                    if action == "modify":
                        w_type = str(w_params.get("type") or "easy_run").lower()
                        if w_type not in SUPPORTED_WORKOUT_TYPES:
                            w_params["type"] = "easy_run"
                        else:
                            w_params["type"] = w_type
                        if "date" not in w_params:
                            w_params["date"] = (current_proposal or {}).get("target_date") or "today"
                    return CoachIntent(
                        action=action,
                        reply_text=str(parsed["reply_text"]).strip(),
                        workout_params=w_params,
                    )

    return heuristic_parse_intent(
        user_text=user_text,
        athlete_name=athlete_name,
        current_proposal=current_proposal,
    )


def build_payload_from_workout_params(
    workout_params: Dict[str, Any],
    target_date_iso: str,
    project_root: Optional[Path] = None,
) -> Dict[str, Any]:
    """Converts CoachIntent workout_params into an Intervals.icu workout event payload."""
    root = project_root or PROJECT_ROOT
    ns = argparse.Namespace(
        date=target_date_iso,
        time=workout_params.get("time", "07:00:00"),
        name=workout_params.get("name"),
        type=workout_params.get("type", "easy_run"),
        duration=workout_params.get("duration", "45m"),
        reps=workout_params.get("reps"),
        work=workout_params.get("work"),
        recovery=workout_params.get("recovery"),
        target=workout_params.get("target"),
        recovery_target=workout_params.get("recovery_target"),
        warmup=workout_params.get("warmup"),
        cooldown=workout_params.get("cooldown"),
        raw_text=None,
        raw_file=None,
        template=None,
    )
    return build_workout_payload(ns, root)
