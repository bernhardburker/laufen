#!/usr/bin/env python3
"""telegram_bot.py

Bidirectional Telegram Bot integration for the Laufen Management Hub.
Supports:
- Automated athlete onboarding via `/start <slug>` (links telegram_chat_id in registry.json)
- Proactive daily training briefings (Option B: Auto-Sync to Intervals.icu + Override on feedback)
- Inline keyboard quick-actions (Confirm, Easier Z2, Shorter 35m, Rest Day)
- Natural language text feedback parsed via telegram_coach.py (LLM + deterministic fallback)
- Lightweight daemon mode with scheduled daily briefing checks and long-polling
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
from pathlib import Path
import sys
import time
from typing import Any, Dict, List, Optional
import urllib.error
import urllib.request

PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.coach_history import load_coach_history
from src.analysis.run_reviewer import (
    evaluate_run_deep,
    extract_deep_run_metrics,
    load_athlete_profile,
    resolve_run_intent,
)
from src.analysis.telegram_coach import (
    build_payload_from_workout_params,
    evaluate_athlete_feedback,
)
from src.analysis.trends import filter_runs, load_activities
from src.athletes import (
    AthleteContext,
    link_telegram_chat_id,
    list_registered_athletes,
    load_registry,
    resolve_athlete_by_telegram_chat_id,
    resolve_athlete_context,
    update_athlete_notification_settings,
)
from src.integrations.download_intervals import fetch_intervals_activities
from src.integrations.download_wellness import fetch_intervals_wellness
from src.llm import LLMClient, get_llm_client
from src.planner.extract_markers import extract_planning_markers
from src.planner.schedule_workout import (
    load_env_file,
    override_workout_on_date,
    sync_adaptive_plan,
)


class TelegramBotClient:
    """Zero-dependency HTTP client for the Telegram Bot API."""

    def __init__(
        self,
        bot_token: str,
        api_base_url: str = "https://api.telegram.org",
        timeout: int = 30,
    ):
        self.bot_token = bot_token.strip()
        self.api_base_url = api_base_url.rstrip("/")
        self.timeout = timeout

    def _endpoint(self, method: str) -> str:
        return f"{self.api_base_url}/bot{self.bot_token}/{method}"

    def _post(self, method: str, payload: Dict[str, Any]) -> Dict[str, Any]:
        url = self._endpoint(method)
        data = json.dumps(payload).encode("utf-8")
        req = urllib.request.Request(
            url,
            data=data,
            headers={
                "Content-Type": "application/json",
                "Accept": "application/json",
                "User-Agent": "LaufenTelegramBot/1.0",
            },
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=self.timeout) as resp:
            raw = resp.read().decode("utf-8")
            return json.loads(raw) if raw else {"ok": True}

    def send_message(
        self,
        chat_id: str | int,
        text: str,
        reply_markup: Optional[Dict[str, Any]] = None,
        parse_mode: Optional[str] = None,
    ) -> Dict[str, Any]:
        payload: Dict[str, Any] = {
            "chat_id": str(chat_id),
            "text": text,
        }
        if parse_mode:
            payload["parse_mode"] = parse_mode
        if reply_markup:
            payload["reply_markup"] = reply_markup
        return self._post("sendMessage", payload)

    def answer_callback_query(
        self,
        callback_query_id: str,
        text: Optional[str] = None,
        show_alert: bool = False,
    ) -> Dict[str, Any]:
        payload: Dict[str, Any] = {"callback_query_id": callback_query_id}
        if text:
            payload["text"] = text
        if show_alert:
            payload["show_alert"] = True
        return self._post("answerCallbackQuery", payload)

    def get_updates(
        self,
        offset: Optional[int] = None,
        timeout: int = 10,
    ) -> List[Dict[str, Any]]:
        payload: Dict[str, Any] = {"timeout": timeout}
        if offset is not None:
            payload["offset"] = offset
        res = self._post("getUpdates", payload)
        if res.get("ok") and isinstance(res.get("result"), list):
            return res["result"]
        return []


def parse_allowed_user_ids(raw_value: Optional[str] = None) -> List[str]:
    """Parses a comma-separated list of allowed Telegram user IDs from env or string."""
    raw = raw_value if raw_value is not None else os.getenv("TELEGRAM_ALLOWED_USER_IDS", "")
    if not raw:
        return []
    return [uid.strip() for uid in raw.split(",") if uid.strip()]


def load_telegram_state(state_path: Path) -> Dict[str, Any]:
    """Loads persisted Telegram state for an athlete."""
    if not state_path.is_file():
        return {}
    try:
        with open(state_path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def save_telegram_state(state_path: Path, state: Dict[str, Any]) -> None:
    """Saves Telegram state for an athlete to disk."""
    state_path.parent.mkdir(parents=True, exist_ok=True)
    with open(state_path, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2, ensure_ascii=False)
        f.write("\n")


def build_briefing_keyboard(
    target_date_iso: str,
    is_rest_day: bool = False,
    sleep_is_today: bool = True,
) -> Dict[str, Any]:
    """Builds the inline keyboard buttons attached to a daily workout briefing."""
    rows: List[List[Dict[str, str]]] = []

    if is_rest_day:
        rows.append([
            {"text": "✅ Ruhetag beibehalten", "callback_data": f"keep_rest:{target_date_iso}"},
            {"text": "🏃 Heute locker laufen (30m)", "callback_data": f"run_today:{target_date_iso}"},
        ])
    else:
        rows.append([
            {"text": "✅ Passt so", "callback_data": f"accept:{target_date_iso}"},
            {"text": "📉 Lockerer (Z2)", "callback_data": f"easier:{target_date_iso}"},
        ])
        rows.append([
            {"text": "⏱️ Nur 35 Min", "callback_data": f"shorter35:{target_date_iso}"},
            {"text": "🛋️ Ruhetag", "callback_data": f"rest:{target_date_iso}"},
        ])

    if not sleep_is_today:
        rows.append([
            {"text": "🔄 Schlaf aktualisieren", "callback_data": f"refresh_sleep:{target_date_iso}"}
        ])

    return {"inline_keyboard": rows}


def build_settings_keyboard(ctx: AthleteContext) -> Dict[str, Any]:
    """Builds interactive inline keyboard for notification toggles."""
    m_icon = "✅ An" if ctx.briefing_enabled else "❌ Aus"
    e_icon = "✅ An" if ctx.evening_briefing_enabled else "❌ Aus"
    p_icon = "✅ An" if ctx.post_run_summary_enabled else "❌ Aus"
    return {
        "inline_keyboard": [
            [
                {"text": f"🌅 Morgen ({m_icon})", "callback_data": "toggle:morning"},
                {"text": f"🌆 Abend ({e_icon})", "callback_data": "toggle:evening"},
            ],
            [
                {"text": f"🏃 Post-Run Review ({p_icon})", "callback_data": "toggle:postrun"},
            ],
        ]
    }


def format_settings_message(ctx: AthleteContext) -> str:
    """Formats notification settings summary for the athlete."""
    m_status = f"✅ Aktiv ({ctx.briefing_time} Uhr)" if ctx.briefing_enabled else "❌ Deaktiviert"
    e_status = f"✅ Aktiv ({ctx.evening_briefing_time} Uhr)" if ctx.evening_briefing_enabled else "❌ Deaktiviert"
    p_status = "✅ Aktiv" if ctx.post_run_summary_enabled else "❌ Deaktiviert"
    return (
        f"⚙️ **Benachrichtigungen für {ctx.display_name}**\n\n"
        f"🌅 **Morgen-Check:** {m_status}\n"
        f"🌆 **Abend-Vorschau:** {e_status}\n"
        f"🏃 **Lauf-Review:** {p_status}\n\n"
        "Tippe auf einen Button unten, um eine Benachrichtigung umzuschalten."
    )


def build_evening_keyboard(target_date_iso: str, is_rest_day: bool = False) -> Dict[str, Any]:
    """Builds inline keyboard for evening preview."""
    if is_rest_day:
        return {
            "inline_keyboard": [
                [
                    {"text": "🛋️ Ruhetag fixieren", "callback_data": f"keep_rest:{target_date_iso}"},
                    {"text": "🏃 Doch Lauf planen", "callback_data": f"run_today:{target_date_iso}"},
                ]
            ]
        }
    return {
        "inline_keyboard": [
            [
                {"text": "👍 Passt so für morgen", "callback_data": f"accept:{target_date_iso}"},
                {"text": "🛋️ Morgen Ruhetag", "callback_data": f"rest:{target_date_iso}"},
            ],
            [
                {"text": "📉 Morgen lockerer (Z2)", "callback_data": f"easier:{target_date_iso}"},
                {"text": "⏱️ Morgen nur 35m", "callback_data": f"shorter35:{target_date_iso}"},
            ],
        ]
    }


def format_evening_preview_message(
    athlete_name: str,
    target_date_iso: str,
    tomorrow_workout: Optional[Dict[str, Any]] = None,
    is_rest_day: bool = False,
) -> str:
    """Formats evening preview message preparing the athlete mentally and logistically for tomorrow."""
    lines = [
        f"🌆 Guten Abend {athlete_name}!",
        f"Hier ist deine Vorschau auf morgen ({target_date_iso}):",
        "",
    ]
    if is_rest_day or not tomorrow_workout:
        lines.append("🛋️ **Morgen steht ein Ruhetag an.**")
        lines.append("Zeit für aktive Erholung, Mobility oder einfach die Füße hochlegen.")
        lines.append("Dein Körper verarbeitet die Trainingsreize der letzten Tage.")
    else:
        w_name = tomorrow_workout.get("name") or "Geplanter Trainingslauf"
        w_desc = str(tomorrow_workout.get("description") or "").strip()
        lines.append(f"🏃 **Geplantes Training:** {w_name}")
        if w_desc:
            lines.append("")
            lines.append(w_desc)
        lines.append("")
        lines.append("👟 **Logistik & Vorbereitung:**")
        lines.append("• Lege dir heute Abend schon Laufkleidung & Schuhe bereit.")
        lines.append("• Garmin-Uhr und Brustgurt über Nacht laden.")
        lines.append("• Morgen früh reicht ein kurzer Blick auf Schlaf & HRV für das finale Go!")

    return "\n".join(lines)


def format_post_run_review_message(
    run_name: str,
    metrics: Dict[str, Any],
    evaluation: Dict[str, Any],
    athlete_name: str,
) -> str:
    """Formats a coach-centric post-run Telegram message focusing on compliance and upcoming plan guidance."""
    stage2 = evaluation.get("stage2") or {}
    stage3 = evaluation.get("stage3") or []
    stage4 = evaluation.get("stage4") or {}
    summary = evaluation.get("summary") or ""
    tip = evaluation.get("tip") or ""

    status_str = str(stage2.get("status") or "erfüllt")
    if "erfüllt" in status_str.lower():
        status_icon = "✅ Plan optimal erfüllt"
    elif "über" in status_str.lower() or "drift" in status_str.lower():
        status_icon = "⚠️ Intensiver als geplant (Pace/HF drosseln)"
    else:
        status_icon = f"ℹ️ {status_str.title()}"

    target_name = stage2.get("target_name") or stage2.get("target_type") or "Grundlagenlauf"
    compliance_text = stage2.get("comment") or stage2.get("compliance_notes") or summary

    lines = [
        f"🏃 **Coach-Review: {run_name}**",
        "",
        f"🎯 **Plan-Erfüllung:** {status_icon}",
        f"• Vorgabe: {target_name}",
        f"• Coach-Fazit: {compliance_text}",
        "",
    ]

    hebel_lines = []
    if stage3 and isinstance(stage3, list):
        for opt in stage3[:2]:
            advice = opt.get("actionable_advice") or opt.get("advice")
            if advice:
                hebel_lines.append(f"• {advice}")
    elif tip:
        hebel_lines.append(f"• {tip}")

    if hebel_lines:
        lines.append("💡 **Coach-Beobachtung & Hebel:**")
        lines.extend(hebel_lines)
        lines.append("")

    directive = stage4.get("directive") or "Körper gut regenerieren lassen."
    next_slot = stage4.get("next_slot") or "Nächste reguläre Einheit"
    next_type = stage4.get("next_workout_type") or "Lockerer Dauerlauf"
    rec_h = stage4.get("recovery_window_hours")

    lines.append("📅 **Auswirkung auf deinen weiteren Plan:**")
    if rec_h:
        lines.append(f"• Erholungsfenster: ca. {rec_h} Stunden")
    lines.append(f"• Empfehlung: {directive}")
    lines.append(f"• Nächster empfohlener Slot: {next_slot} ({next_type})")

    return "\n".join(lines)


def format_daily_briefing_message(
    athlete_name: str,
    markers: Dict[str, Any],
    sync_result: Dict[str, Any],
    ref_date_iso: str,
) -> tuple[str, Optional[Dict[str, Any]]]:
    """Formats the German morning briefing text and extracts the active proposal dict."""
    readiness = markers.get("readiness", {})
    sleep_h = readiness.get("sleep_hours_last_night", 7.5)
    sleep_score = readiness.get("sleep_score_last_night", 80)
    sleep_is_today = readiness.get("sleep_is_today", True)
    tsb = readiness.get("tsb", 0.0)
    rhr = readiness.get("resting_hr_today", 48)

    target_date_iso = sync_result.get("target_date") or ref_date_iso
    is_rest_day = bool(sync_result.get("is_rest_day", False))

    scheduled_list = sync_result.get("scheduled") or []
    skipped_list = sync_result.get("skipped") or []
    active_workout = (
        scheduled_list[0]
        if scheduled_list
        else (skipped_list[0] if skipped_list else None)
    )

    if sleep_is_today:
        sleep_line = f"• Schlaf: {sleep_h:.1f}h (Score {sleep_score:.0f}) | Ruhepuls: {rhr} bpm"
    else:
        sleep_line = f"• Schlaf: Noch nicht synchronisiert (Uhr noch nicht in Garmin Connect aktualisiert) | Ruhepuls: {rhr} bpm"

    lines = [
        f"☀️ Guten Morgen {athlete_name}!",
        "",
        f"📊 Readiness ({ref_date_iso}):",
        sleep_line,
        f"• Form (TSB): {tsb:+.1f}",
        "",
    ]

    proposal_dict: Optional[Dict[str, Any]] = None

    if is_rest_day and target_date_iso != ref_date_iso:
        lines.append("🛋️ Heute steht ein wohlverdienter Ruhetag an (aktive Erholung).")
        if active_workout:
            w_name = active_workout.get("name", "Training")
            lines.append(f"📅 Vorschau für morgen ({target_date_iso}): {w_name} (bereits auf Intervals.icu vorgemerkt).")
            proposal_dict = {
                "target_date": target_date_iso,
                "name": w_name,
                "description": active_workout.get("description", ""),
                "event_id": active_workout.get("id"),
                "is_rest_day_today": True,
            }
        lines.append("")
        lines.append("Falls du heute trotzdem locker laufen oder den morgigen Plan anpassen möchtest, antworte mir einfach kurz!")
    elif active_workout:
        w_name = active_workout.get("name", "Training")
        w_desc = str(active_workout.get("description") or "").strip()
        date_label = "heute" if target_date_iso == ref_date_iso else f"morgen ({target_date_iso})"
        lines.append(f"🏃 Trainingsvorschlag für {date_label}:")
        lines.append(f"👉 {w_name}")
        if w_desc:
            lines.append("")
            lines.append(w_desc)
        lines.append("")
        lines.append("✅ Das Training ist bereits in Intervals.icu & für deine Garmin-Uhr eingeplant.")
        lines.append("Antworte mir einfach per Text (z.B. 'Habe heute nur 35 min Zeit' oder 'Fühle mich schlapp') oder nutze die Buttons zur Anpassung:")
        proposal_dict = {
            "target_date": target_date_iso,
            "name": w_name,
            "description": w_desc,
            "event_id": active_workout.get("id"),
            "is_rest_day_today": False,
        }
    else:
        lines.append("🏃 Für heute ist aktuell kein Lauf geplant.")
        lines.append("Schreib mir einfach (z.B. 'Plane heute 40 min locker'), falls ich dir ein Workout auf die Uhr schicken soll!")

    return "\n".join(lines), proposal_dict


def send_daily_briefing_for_athlete(
    ctx: AthleteContext,
    bot_client: TelegramBotClient,
    intervals_api_key: str,
    intervals_api_base: str = "https://intervals.icu/api/v1",
    ref_date: Optional[datetime.date] = None,
    dry_run: bool = False,
    project_root: Optional[Path] = None,
) -> Dict[str, Any]:
    """Runs Option B Auto-Sync for the athlete and sends the interactive Telegram morning briefing."""
    root = project_root or PROJECT_ROOT
    today = ref_date or datetime.date.today()
    today_iso = today.isoformat()

    if not ctx.telegram_chat_id:
        raise ValueError(
            f"Athlete '{ctx.slug}' does not have a linked telegram_chat_id. "
            f"Send '/start {ctx.slug}' to the bot in Telegram first."
        )

    # 0. Proactively sync fresh activities & wellness from Intervals.icu if credentials exist
    if intervals_api_key and not dry_run:
        try:
            fresh_acts = fetch_intervals_activities(
                api_key=intervals_api_key,
                athlete_id=ctx.intervals_id,
                api_base_url=intervals_api_base,
                raise_on_error=True,
            )
            if fresh_acts and isinstance(fresh_acts, list):
                ctx.activities_path.parent.mkdir(parents=True, exist_ok=True)
                ctx.activities_path.write_text(json.dumps(fresh_acts, indent=2, ensure_ascii=False), encoding="utf-8")
        except Exception:
            pass

        try:
            fresh_wellness = fetch_intervals_wellness(
                api_key=intervals_api_key,
                athlete_id=ctx.intervals_id,
                api_base_url=intervals_api_base,
                raise_on_error=True,
            )
            if fresh_wellness and isinstance(fresh_wellness, list):
                ctx.wellness_path.parent.mkdir(parents=True, exist_ok=True)
                ctx.wellness_path.write_text(json.dumps(fresh_wellness, indent=2, ensure_ascii=False), encoding="utf-8")
        except Exception:
            pass

    # 1. Auto-sync adaptive workout to Intervals.icu (Option B)
    profile_data = {}
    if ctx.athlete_config_path.is_file():
        try:
            profile_data = json.loads(ctx.athlete_config_path.read_text(encoding="utf-8"))
        except Exception:
            profile_data = {}

    sync_res = sync_adaptive_plan(
        api_key=intervals_api_key,
        athlete_id=ctx.intervals_id,
        athlete_profile=profile_data,
        ref_date=today,
        dry_run=dry_run,
        api_base_url=intervals_api_base,
        project_root=root,
        clean_past=True,
        activities_file=ctx.activities_path,
    )

    target_date_str = sync_res.get("target_date") or today_iso
    target_date_obj = datetime.date.fromisoformat(target_date_str)
    is_rest_day_today = bool(sync_res.get("is_rest_day", False))

    # 2. Extract readiness & guardrails markers
    markers = extract_planning_markers(
        activities_file=ctx.activities_path,
        wellness_file=ctx.wellness_path,
        profile_file=ctx.athlete_config_path,
        ref_date=today,
        target_date=target_date_obj,
        project_root=root,
    )

    # 3. Format message and inline buttons
    msg_text, proposal = format_daily_briefing_message(
        athlete_name=ctx.display_name,
        markers=markers,
        sync_result=sync_res,
        ref_date_iso=today_iso,
    )

    # For rest days, buttons target TODAY (today_iso), not tomorrow!
    keyboard_target_date = today_iso if is_rest_day_today else target_date_str
    sleep_is_today = bool(markers.get("readiness", {}).get("sleep_is_today", True))
    keyboard = build_briefing_keyboard(
        keyboard_target_date,
        is_rest_day=is_rest_day_today,
        sleep_is_today=sleep_is_today,
    )
    send_res = bot_client.send_message(
        chat_id=ctx.telegram_chat_id,
        text=msg_text,
        reply_markup=keyboard,
    )

    # 4. Save state in athlete's telegram_state.json
    state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
    state = load_telegram_state(state_path)
    state["last_briefing_date"] = today_iso
    state["current_proposal"] = proposal
    save_telegram_state(state_path, state)

    return {
        "athlete": ctx.slug,
        "chat_id": ctx.telegram_chat_id,
        "target_date": target_date_str,
        "proposal": proposal,
        "telegram_response": send_res,
    }


def send_evening_preview_for_athlete(
    ctx: AthleteContext,
    bot_client: TelegramBotClient,
    intervals_api_key: str,
    intervals_api_base: str = "https://intervals.icu/api/v1",
    ref_date: Optional[datetime.date] = None,
    dry_run: bool = False,
    project_root: Optional[Path] = None,
) -> Dict[str, Any]:
    """Sends the evening preview for tomorrow's planned session and logistics."""
    root = project_root or PROJECT_ROOT
    today = ref_date or datetime.date.today()
    today_iso = today.isoformat()
    tomorrow = today + datetime.timedelta(days=1)
    tomorrow_iso = tomorrow.isoformat()

    if not ctx.telegram_chat_id:
        raise ValueError(f"Athlete '{ctx.slug}' does not have a linked telegram_chat_id.")

    profile_data = {}
    if ctx.athlete_config_path.is_file():
        try:
            profile_data = json.loads(ctx.athlete_config_path.read_text(encoding="utf-8"))
        except Exception:
            profile_data = {}

    sync_res = sync_adaptive_plan(
        api_key=intervals_api_key,
        athlete_id=ctx.intervals_id,
        athlete_profile=profile_data,
        ref_date=tomorrow,
        dry_run=dry_run,
        api_base_url=intervals_api_base,
        project_root=root,
        clean_past=False,
        activities_file=ctx.activities_path,
    )

    is_rest_day = bool(sync_res.get("is_rest_day", False))
    scheduled = sync_res.get("scheduled") or []
    skipped = sync_res.get("skipped") or []
    tomorrow_workout = scheduled[0] if scheduled else (skipped[0] if skipped else None)

    msg_text = format_evening_preview_message(
        athlete_name=ctx.display_name,
        target_date_iso=tomorrow_iso,
        tomorrow_workout=tomorrow_workout,
        is_rest_day=is_rest_day,
    )
    keyboard = build_evening_keyboard(tomorrow_iso, is_rest_day=is_rest_day)

    send_res = bot_client.send_message(
        chat_id=ctx.telegram_chat_id,
        text=msg_text,
        reply_markup=keyboard,
    )

    state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
    state = load_telegram_state(state_path)
    state["last_evening_preview_date"] = today_iso
    if tomorrow_workout:
        state["current_proposal"] = {
            "target_date": tomorrow_iso,
            "name": tomorrow_workout.get("name"),
            "description": tomorrow_workout.get("description", ""),
            "event_id": tomorrow_workout.get("id"),
            "is_rest_day_today": is_rest_day,
        }
    save_telegram_state(state_path, state)

    return {
        "athlete": ctx.slug,
        "chat_id": ctx.telegram_chat_id,
        "target_date": tomorrow_iso,
        "telegram_response": send_res,
    }


def send_post_run_review_for_athlete(
    ctx: AthleteContext,
    bot_client: TelegramBotClient,
    activity: Optional[Dict[str, Any]] = None,
    activity_id: Optional[str] = None,
    project_root: Optional[Path] = None,
    dry_run: bool = False,
) -> Dict[str, Any]:
    """Generates and sends coach-centric post-run review for a completed activity."""
    root = project_root or PROJECT_ROOT
    if not ctx.telegram_chat_id:
        return {"status": "no_chat_id", "athlete": ctx.slug}

    act: Optional[Dict[str, Any]] = activity
    if act is None:
        if not ctx.activities_path.is_file():
            return {"status": "no_activities_file", "athlete": ctx.slug}
        all_acts = load_activities(ctx.activities_path)
        runs = filter_runs(all_acts)
        if not runs:
            return {"status": "no_runs", "athlete": ctx.slug}
        if activity_id:
            for r in runs:
                if str(r.get("id")) == str(activity_id):
                    act = r
                    break
        if act is None and runs:
            act = sorted(runs, key=lambda r: str(r.get("start_date_local") or ""), reverse=True)[0]

    if not act:
        return {"status": "activity_not_found", "athlete": ctx.slug}

    act_id = str(act.get("id") or "0")
    profile = load_athlete_profile(ctx.athlete_config_path)
    metrics = extract_deep_run_metrics(act, profile)
    history = load_coach_history(ctx.coach_history_path) if ctx.coach_history_path.is_file() else []
    intent = resolve_run_intent(act, metrics, profile, history)
    evaluation = evaluate_run_deep(metrics, profile, run_raw=act, coach_history=history, run_intent=intent)

    run_name = str(act.get("name") or metrics.get("name") or "Lauf")
    msg_text = format_post_run_review_message(
        run_name=run_name,
        metrics=metrics,
        evaluation=evaluation,
        athlete_name=ctx.display_name,
    )

    send_res = bot_client.send_message(
        chat_id=ctx.telegram_chat_id,
        text=msg_text,
    )

    state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
    state = load_telegram_state(state_path)
    notified = list(state.get("notified_activity_ids") or [])
    if act_id not in notified:
        notified.append(act_id)
    state["notified_activity_ids"] = notified
    save_telegram_state(state_path, state)

    return {
        "status": "sent",
        "athlete": ctx.slug,
        "activity_id": act_id,
        "telegram_response": send_res,
    }


def handle_telegram_update(
    update: Dict[str, Any],
    bot_client: TelegramBotClient,
    intervals_api_key: str,
    intervals_api_base: str = "https://intervals.icu/api/v1",
    llm_client: Optional[LLMClient] = None,
    allowed_user_ids: Optional[List[str]] = None,
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
    ref_date: Optional[datetime.date] = None,
    dry_run: bool = False,
) -> Dict[str, Any]:
    """Processes a single incoming Telegram update (text message or callback button click)."""
    root = project_root or PROJECT_ROOT
    today = ref_date or datetime.date.today()
    active_allowed_user_ids = (
        allowed_user_ids if allowed_user_ids is not None else parse_allowed_user_ids()
    )

    # Case 1: Inline Keyboard Button Click (callback_query)
    if "callback_query" in update:
        cb = update["callback_query"]
        cb_id = str(cb.get("id") or "")
        data = str(cb.get("data") or "")
        msg = cb.get("message") or {}
        chat_id = str((msg.get("chat") or {}).get("id") or "")
        from_user = cb.get("from") or {}
        user_id = str(from_user.get("id") or "")
        effective_user_id = user_id or (chat_id if not chat_id.startswith("-") else "")

        if active_allowed_user_ids and (not effective_user_id or effective_user_id not in active_allowed_user_ids):
            if cb_id:
                try:
                    bot_client.answer_callback_query(cb_id, text="⛔ Zugriff verweigert.", show_alert=True)
                except Exception:
                    pass
            return {"status": "unauthorized", "user_id": effective_user_id, "chat_id": chat_id}

        if cb_id:
            try:
                bot_client.answer_callback_query(cb_id)
            except Exception:
                pass

        ctx = resolve_athlete_by_telegram_chat_id(chat_id, project_root=root, registry_path=registry_path)
        if not ctx:
            if chat_id:
                bot_client.send_message(
                    chat_id=chat_id,
                    text="⚠️ Dein Chat ist noch keinem Athleten zugeordnet. Bitte sende `/start <dein-slug>`.",
                )
            return {"status": "unlinked", "chat_id": chat_id}

        action_key, _, date_part = data.partition(":")
        state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
        state = load_telegram_state(state_path)
        proposal = state.get("current_proposal") or {}
        target_date_iso = date_part or proposal.get("target_date") or today.isoformat()

        if action_key == "refresh_sleep":
            if intervals_api_key and not dry_run:
                try:
                    fresh_wellness = fetch_intervals_wellness(
                        api_key=intervals_api_key,
                        athlete_id=ctx.intervals_id,
                        api_base_url=intervals_api_base,
                        raise_on_error=True,
                    )
                    if fresh_wellness and isinstance(fresh_wellness, list):
                        ctx.wellness_path.parent.mkdir(parents=True, exist_ok=True)
                        ctx.wellness_path.write_text(json.dumps(fresh_wellness, indent=2, ensure_ascii=False), encoding="utf-8")
                except Exception:
                    pass

            target_date_obj = today
            try:
                target_date_obj = datetime.date.fromisoformat(target_date_iso)
            except Exception:
                pass

            markers = extract_planning_markers(
                activities_file=ctx.activities_path,
                wellness_file=ctx.wellness_path,
                profile_file=ctx.athlete_config_path,
                ref_date=today,
                target_date=target_date_obj,
                project_root=root,
            )
            readiness = markers.get("readiness", {})
            sleep_is_today = readiness.get("sleep_is_today", False)
            if sleep_is_today:
                sleep_h = readiness.get("sleep_hours_last_night", 7.5)
                sleep_score = readiness.get("sleep_score_last_night", 80)
                rhr = readiness.get("resting_hr_today", 48)
                reply = (
                    f"✅ Schlafdaten für heute erfolgreich aktualisiert:\n"
                    f"• Schlaf: {sleep_h:.1f}h (Score {sleep_score:.0f}) | Ruhepuls: {rhr} bpm"
                )
            else:
                reply = (
                    "ℹ️ Auf Intervals.icu liegen noch keine neuen Schlafdaten für heute vor.\n"
                    "Bitte öffne kurz die Garmin Connect App auf deinem Smartphone und tippe danach erneut auf '🔄 Schlaf aktualisieren'."
                )
            bot_client.send_message(chat_id=chat_id, text=reply)
            return {"status": "sleep_refreshed", "athlete": ctx.slug, "sleep_is_today": sleep_is_today}

        if action_key == "toggle":
            setting_name = date_part
            if setting_name == "morning":
                new_val = not ctx.briefing_enabled
                ctx = update_athlete_notification_settings(
                    athlete_slug=ctx.slug,
                    morning_enabled=new_val,
                    project_root=root,
                    registry_path=registry_path,
                )
                status_word = "aktiviert" if new_val else "deaktiviert"
                reply = f"🌅 Morgen-Check wurde {status_word}."
            elif setting_name == "evening":
                new_val = not ctx.evening_briefing_enabled
                ctx = update_athlete_notification_settings(
                    athlete_slug=ctx.slug,
                    evening_enabled=new_val,
                    project_root=root,
                    registry_path=registry_path,
                )
                status_word = "aktiviert" if new_val else "deaktiviert"
                reply = f"🌆 Abend-Vorschau wurde {status_word}."
            elif setting_name in ("postrun", "review"):
                new_val = not ctx.post_run_summary_enabled
                ctx = update_athlete_notification_settings(
                    athlete_slug=ctx.slug,
                    post_run_enabled=new_val,
                    project_root=root,
                    registry_path=registry_path,
                )
                status_word = "aktiviert" if new_val else "deaktiviert"
                reply = f"🏃 Post-Run Review wurde {status_word}."
            else:
                reply = "⚠️ Unbekannte Einstellung."

            kb = build_settings_keyboard(ctx)
            settings_text = format_settings_message(ctx)
            full_msg = f"{reply}\n\n{settings_text}"
            bot_client.send_message(chat_id=chat_id, text=full_msg, reply_markup=kb)
            return {"status": "setting_toggled", "athlete": ctx.slug, "setting": setting_name}

        if action_key == "accept":
            w_name = proposal.get("name") or "Dein Training"
            reply = f"Perfekt, {ctx.display_name}! '{w_name}' ({target_date_iso}) bleibt wie geplant auf Intervals.icu & deiner Garmin-Uhr. Viel Erfolg!"
            bot_client.send_message(chat_id=chat_id, text=reply)
            return {"status": "accepted", "athlete": ctx.slug, "target_date": target_date_iso}

        if action_key == "keep_rest":
            override_workout_on_date(
                target_date_iso=target_date_iso,
                new_payload=None,
                api_key=intervals_api_key,
                athlete_id=ctx.intervals_id,
                api_base_url=intervals_api_base,
                dry_run=dry_run,
                is_rest_day=True,
            )
            state["current_proposal"] = None
            save_telegram_state(state_path, state)
            reply = f"🛋️ Sehr gut, {ctx.display_name}! Der heutige Ruhetag ({target_date_iso}) bleibt fixiert. Gute Erholung!"
            bot_client.send_message(chat_id=chat_id, text=reply)
            return {"status": "keep_rest", "athlete": ctx.slug, "target_date": target_date_iso}

        if action_key == "run_today":
            synth_text = "Möchte heute doch locker 30 min in Zone 2 laufen"
            return _process_athlete_feedback_text(
                user_text=synth_text,
                ctx=ctx,
                chat_id=chat_id,
                bot_client=bot_client,
                intervals_api_key=intervals_api_key,
                intervals_api_base=intervals_api_base,
                llm_client=llm_client,
                root=root,
                today=today,
                dry_run=dry_run,
            )

        if action_key == "rest":
            override_workout_on_date(
                target_date_iso=target_date_iso,
                new_payload=None,
                api_key=intervals_api_key,
                athlete_id=ctx.intervals_id,
                api_base_url=intervals_api_base,
                dry_run=dry_run,
                is_rest_day=True,
            )
            state["current_proposal"] = None
            save_telegram_state(state_path, state)
            reply = f"🛋️ Verstanden, {ctx.display_name}! Ich habe das geplante Workout am {target_date_iso} aus Intervals.icu entfernt und als Ruhetag eingetragen. Genieße deinen Ruhetag!"
            bot_client.send_message(chat_id=chat_id, text=reply)
            return {"status": "rest_day", "athlete": ctx.slug, "target_date": target_date_iso}

        if action_key in ("easier", "shorter35"):
            synth_text = "Fühle mich schlapp, bitte locker in Z2" if action_key == "easier" else "Habe heute nur 35 min Zeit"
            return _process_athlete_feedback_text(
                user_text=synth_text,
                ctx=ctx,
                chat_id=chat_id,
                bot_client=bot_client,
                intervals_api_key=intervals_api_key,
                intervals_api_base=intervals_api_base,
                llm_client=llm_client,
                root=root,
                today=today,
                dry_run=dry_run,
            )

    # Case 2: Standard Message
    message = update.get("message") or {}
    chat_id = str((message.get("chat") or {}).get("id") or "")
    from_user = message.get("from") or {}
    user_id = str(from_user.get("id") or "")
    effective_user_id = user_id or (chat_id if not chat_id.startswith("-") else "")
    text = str(message.get("text") or "").strip()

    if not chat_id:
        return {"status": "ignored"}

    if active_allowed_user_ids and (not effective_user_id or effective_user_id not in active_allowed_user_ids):
        bot_client.send_message(
            chat_id=chat_id,
            text="⛔ Zugriff verweigert. Dieser Telegram-Bot ist privat.",
        )
        return {"status": "unauthorized", "user_id": effective_user_id, "chat_id": chat_id}

    # If user sends a voice note (not yet enabled)
    if "voice" in message and not text:
        bot_client.send_message(
            chat_id=chat_id,
            text="🎙️ Sprachnachrichten sind aktuell noch deaktiviert. Bitte schreib mir kurz per Text oder nutze die Auswahl-Buttons!",
        )
        return {"status": "voice_disabled", "chat_id": chat_id}

    if not text:
        return {"status": "ignored", "chat_id": chat_id}

    # Check commands: /start [slug]
    if text.lower().startswith("/start"):
        parts = text.split()
        reg = load_registry(registry_path=registry_path, project_root=root)
        athletes_map = reg.get("athletes", {})

        if len(parts) >= 2:
            target_slug = parts[1].strip().lower()
        else:
            # If only one athlete exists or default athlete exists, check if already linked
            existing_ctx = resolve_athlete_by_telegram_chat_id(chat_id, project_root=root, registry_path=registry_path)
            if existing_ctx:
                target_slug = existing_ctx.slug
            elif len(athletes_map) == 1:
                target_slug = next(iter(athletes_map.keys()))
            else:
                available = ", ".join(athletes_map.keys())
                bot_client.send_message(
                    chat_id=chat_id,
                    text=(
                        f"👋 Hallo! Deine Telegram Chat-ID lautet: `{chat_id}`.\n"
                        f"Bitte verknüpfe dein Profil mit: `/start <slug>` (Verfügbar: {available})"
                    ),
                )
                return {"status": "start_needs_slug", "chat_id": chat_id}

        if target_slug not in athletes_map:
            available = ", ".join(athletes_map.keys())
            bot_client.send_message(
                chat_id=chat_id,
                text=f"❌ Athlet '{target_slug}' wurde nicht in der Registry gefunden. Verfügbare Profile: {available}",
            )
            return {"status": "unknown_athlete", "chat_id": chat_id}

        linked_ctx = link_telegram_chat_id(
            athlete_slug=target_slug,
            chat_id=chat_id,
            project_root=root,
            registry_path=registry_path,
        )
        bot_client.send_message(
            chat_id=chat_id,
            text=(
                f"✅ Erfolgreich verknüpft mit **{linked_ctx.display_name}** (`{linked_ctx.slug}`)!\n"
                f"• Deine Chat-ID: `{chat_id}`\n"
                f"• Tägliches Briefing: {linked_ctx.briefing_time} Uhr\n\n"
                "Du kannst jederzeit `/heute` senden, um deinen aktuellen Trainingsvorschlag abzurufen, "
                "oder mir direkt schreiben (z.B. 'Habe heute nur 35 min Zeit')."
            ),
        )
        return {"status": "linked", "athlete": linked_ctx.slug, "chat_id": chat_id}

    # Resolve athlete by chat_id for all other commands/messages
    ctx = resolve_athlete_by_telegram_chat_id(chat_id, project_root=root, registry_path=registry_path)
    if not ctx:
        bot_client.send_message(
            chat_id=chat_id,
            text=(
                f"⚠️ Deine Chat-ID (`{chat_id}`) ist noch keinem Athleten zugeordnet.\n"
                "Bitte sende `/start <dein-slug>` (z.B. `/start berni`), um dein Profil zu verknüpfen."
            ),
        )
        return {"status": "unlinked", "chat_id": chat_id}

    # On-demand briefing trigger (/heute or /briefing)
    if text.lower() in ("/heute", "/today", "/briefing"):
        res = send_daily_briefing_for_athlete(
            ctx=ctx,
            bot_client=bot_client,
            intervals_api_key=intervals_api_key,
            intervals_api_base=intervals_api_base,
            ref_date=today,
            dry_run=dry_run,
            project_root=root,
        )
        return {"status": "briefing_sent", "athlete": ctx.slug, "result": res}

    # Settings menu (/settings, /einstellungen, /config)
    if text.lower() in ("/settings", "/einstellungen", "/config"):
        msg = format_settings_message(ctx)
        kb = build_settings_keyboard(ctx)
        bot_client.send_message(chat_id=chat_id, text=msg, reply_markup=kb)
        return {"status": "settings_shown", "athlete": ctx.slug}

    # Direct toggle commands: /morgen on|off
    if text.lower().startswith(("/morgen ", "/morning ")):
        val = text.lower().split(maxsplit=1)[1].strip()
        enabled = val in ("on", "an", "true", "1", "ja", "enable")
        ctx = update_athlete_notification_settings(
            athlete_slug=ctx.slug,
            morning_enabled=enabled,
            project_root=root,
            registry_path=registry_path,
        )
        status_txt = f"aktiviert ({ctx.briefing_time} Uhr)" if enabled else "deaktiviert"
        bot_client.send_message(chat_id=chat_id, text=f"🌅 Morgen-Check {status_txt}.")
        return {"status": "morning_toggled", "athlete": ctx.slug, "morning_enabled": enabled}

    # Direct toggle commands: /abend on|off
    if text.lower().startswith(("/abend ", "/evening ")):
        val = text.lower().split(maxsplit=1)[1].strip()
        enabled = val in ("on", "an", "true", "1", "ja", "enable")
        ctx = update_athlete_notification_settings(
            athlete_slug=ctx.slug,
            evening_enabled=enabled,
            project_root=root,
            registry_path=registry_path,
        )
        status_txt = f"aktiviert ({ctx.evening_briefing_time} Uhr)" if enabled else "deaktiviert"
        bot_client.send_message(chat_id=chat_id, text=f"🌆 Abend-Vorschau {status_txt}.")
        return {"status": "evening_toggled", "athlete": ctx.slug, "evening_enabled": enabled}

    # Direct toggle commands: /review on|off or /postrun on|off
    if text.lower().startswith(("/review ", "/postrun ")):
        val = text.lower().split(maxsplit=1)[1].strip()
        if val in ("on", "an", "true", "1", "ja", "enable"):
            enabled = True
        elif val in ("off", "aus", "false", "0", "nein", "disable"):
            enabled = False
        else:
            enabled = None
        if enabled is not None:
            ctx = update_athlete_notification_settings(
                athlete_slug=ctx.slug,
                post_run_enabled=enabled,
                project_root=root,
                registry_path=registry_path,
            )
            status_txt = "aktiviert" if enabled else "deaktiviert"
            bot_client.send_message(chat_id=chat_id, text=f"🏃 Post-Run Review {status_txt}.")
            return {"status": "review_toggled", "athlete": ctx.slug, "post_run_enabled": enabled}

    # On-demand post-run review: /review, /last, /analyse, /postrun
    if text.lower() in ("/review", "/last", "/analyse", "/postrun"):
        res = send_post_run_review_for_athlete(
            ctx=ctx,
            bot_client=bot_client,
            project_root=root,
            dry_run=dry_run,
        )
        return {"status": "review_sent", "athlete": ctx.slug, "result": res}

    # On-demand evening preview: /abend, /preview, /evening
    if text.lower() in ("/abend", "/preview", "/evening"):
        res = send_evening_preview_for_athlete(
            ctx=ctx,
            bot_client=bot_client,
            intervals_api_key=intervals_api_key,
            intervals_api_base=intervals_api_base,
            ref_date=today,
            dry_run=dry_run,
            project_root=root,
        )
        return {"status": "evening_preview_sent", "athlete": ctx.slug, "result": res}

    # Evening time command: /abendzeit 20:30
    if text.lower().startswith(("/abendzeit ", "/evening_time ")):
        new_time = text.split(maxsplit=1)[1].strip()
        if len(new_time) == 5 and new_time[2] == ":" and new_time[:2].isdigit() and new_time[3:].isdigit():
            update_athlete_notification_settings(
                athlete_slug=ctx.slug,
                evening_time=new_time,
                project_root=root,
                registry_path=registry_path,
            )
            bot_client.send_message(
                chat_id=chat_id,
                text=f"⏰ Deine Abend-Vorschau Uhrzeit wurde auf {new_time} Uhr geändert.",
            )
            return {"status": "evening_time_updated", "athlete": ctx.slug, "evening_time": new_time}

    # Configure briefing time: /zeit 06:30 or /time 07:00
    if text.lower().startswith(("/zeit ", "/time ")):
        new_time = text.split(maxsplit=1)[1].strip()
        if len(new_time) == 5 and new_time[2] == ":" and new_time[:2].isdigit() and new_time[3:].isdigit():
            link_telegram_chat_id(
                athlete_slug=ctx.slug,
                chat_id=chat_id,
                briefing_time=new_time,
                project_root=root,
                registry_path=registry_path,
            )
            bot_client.send_message(
                chat_id=chat_id,
                text=f"⏰ Deine tägliche Briefing-Uhrzeit wurde auf {new_time} Uhr geändert.",
            )
            return {"status": "time_updated", "athlete": ctx.slug, "briefing_time": new_time}

    # Natural language feedback -> Coach Intent evaluation & Intervals.icu override
    return _process_athlete_feedback_text(
        user_text=text,
        ctx=ctx,
        chat_id=chat_id,
        bot_client=bot_client,
        intervals_api_key=intervals_api_key,
        intervals_api_base=intervals_api_base,
        llm_client=llm_client,
        root=root,
        today=today,
        dry_run=dry_run,
    )


def _process_athlete_feedback_text(
    user_text: str,
    ctx: AthleteContext,
    chat_id: str,
    bot_client: TelegramBotClient,
    intervals_api_key: str,
    intervals_api_base: str,
    llm_client: Optional[LLMClient],
    root: Path,
    today: datetime.date,
    dry_run: bool,
) -> Dict[str, Any]:
    state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
    state = load_telegram_state(state_path)
    proposal = state.get("current_proposal") or {}
    target_date_iso = proposal.get("target_date") or today.isoformat()

    markers = extract_planning_markers(
        activities_file=ctx.activities_path,
        wellness_file=ctx.wellness_path,
        profile_file=ctx.athlete_config_path,
        ref_date=today,
        target_date=datetime.date.fromisoformat(target_date_iso),
        project_root=root,
    )

    intent = evaluate_athlete_feedback(
        user_text=user_text,
        athlete_name=ctx.display_name,
        current_proposal=proposal,
        markers=markers,
        llm_client=llm_client,
    )

    if intent.action == "modify":
        new_payload = build_payload_from_workout_params(
            workout_params=intent.workout_params,
            target_date_iso=target_date_iso,
            project_root=root,
        )
        override_res = override_workout_on_date(
            target_date_iso=target_date_iso,
            new_payload=new_payload,
            api_key=intervals_api_key,
            athlete_id=ctx.intervals_id,
            api_base_url=intervals_api_base,
            dry_run=dry_run,
        )
        scheduled_ev = override_res.get("scheduled") or new_payload
        state["current_proposal"] = {
            "target_date": target_date_iso,
            "name": scheduled_ev.get("name"),
            "description": scheduled_ev.get("description"),
            "event_id": scheduled_ev.get("id"),
            "is_rest_day_today": False,
        }
        save_telegram_state(state_path, state)

        reply_msg = (
            f"{intent.reply_text}\n\n"
            f"📋 **Neues Workout ({target_date_iso}):**\n"
            f"👉 {scheduled_ev.get('name')}\n"
            f"{str(scheduled_ev.get('description') or '').strip()}"
        )
        bot_client.send_message(chat_id=chat_id, text=reply_msg)
        return {
            "status": "modified",
            "athlete": ctx.slug,
            "target_date": target_date_iso,
            "scheduled": scheduled_ev,
        }

    elif intent.action == "rest_day":
        override_workout_on_date(
            target_date_iso=target_date_iso,
            new_payload=None,
            api_key=intervals_api_key,
            athlete_id=ctx.intervals_id,
            api_base_url=intervals_api_base,
            dry_run=dry_run,
        )
        state["current_proposal"] = None
        save_telegram_state(state_path, state)
        bot_client.send_message(chat_id=chat_id, text=intent.reply_text)
        return {
            "status": "rest_day",
            "athlete": ctx.slug,
            "target_date": target_date_iso,
        }

    elif intent.action == "accept":
        bot_client.send_message(chat_id=chat_id, text=intent.reply_text)
        return {
            "status": "accepted",
            "athlete": ctx.slug,
            "target_date": target_date_iso,
        }

    else:
        bot_client.send_message(chat_id=chat_id, text=intent.reply_text)
        return {
            "status": "question",
            "athlete": ctx.slug,
        }


def check_and_send_due_briefings(
    bot_client: TelegramBotClient,
    intervals_api_key: str,
    intervals_api_base: str = "https://intervals.icu/api/v1",
    now: Optional[datetime.datetime] = None,
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
    dry_run: bool = False,
) -> List[Dict[str, Any]]:
    """Checks all active athletes and sends their daily briefing if current time >= briefing_time and not yet sent today."""
    root = project_root or PROJECT_ROOT
    current_dt = now or datetime.datetime.now()
    today_iso = current_dt.date().isoformat()
    current_hm = current_dt.strftime("%H:%M")

    sent_results: List[Dict[str, Any]] = []
    athletes = list_registered_athletes(project_root=root, registry_path=registry_path, active_only=True)

    for ctx in athletes:
        if not ctx.briefing_enabled or not ctx.telegram_chat_id:
            continue
        state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
        state = load_telegram_state(state_path)
        if state.get("last_briefing_date") == today_iso:
            continue
        if current_hm >= ctx.briefing_time:
            try:
                res = send_daily_briefing_for_athlete(
                    ctx=ctx,
                    bot_client=bot_client,
                    intervals_api_key=intervals_api_key,
                    intervals_api_base=intervals_api_base,
                    ref_date=current_dt.date(),
                    dry_run=dry_run,
                    project_root=root,
                )
                sent_results.append(res)
            except Exception as e:
                print(f"[WARN] Briefing for athlete '{ctx.slug}' failed: {e}", file=sys.stderr)

    return sent_results


def check_and_send_due_evening_previews(
    bot_client: TelegramBotClient,
    intervals_api_key: str,
    intervals_api_base: str = "https://intervals.icu/api/v1",
    now: Optional[datetime.datetime] = None,
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
    dry_run: bool = False,
) -> List[Dict[str, Any]]:
    """Checks all active athletes and sends evening preview if time is reached and enabled."""
    root = project_root or PROJECT_ROOT
    current_dt = now or datetime.datetime.now()
    today_iso = current_dt.date().isoformat()
    current_hm = current_dt.strftime("%H:%M")

    sent_results: List[Dict[str, Any]] = []
    athletes = list_registered_athletes(project_root=root, registry_path=registry_path, active_only=True)

    for ctx in athletes:
        if not ctx.evening_briefing_enabled or not ctx.telegram_chat_id:
            continue
        state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
        state = load_telegram_state(state_path)
        if state.get("last_evening_preview_date") == today_iso:
            continue
        if current_hm >= ctx.evening_briefing_time:
            try:
                res = send_evening_preview_for_athlete(
                    ctx=ctx,
                    bot_client=bot_client,
                    intervals_api_key=intervals_api_key,
                    intervals_api_base=intervals_api_base,
                    ref_date=current_dt.date(),
                    dry_run=dry_run,
                    project_root=root,
                )
                sent_results.append(res)
            except Exception as e:
                print(f"[WARN] Evening preview for athlete '{ctx.slug}' failed: {e}", file=sys.stderr)

    return sent_results


def check_and_send_due_post_run_reviews(
    bot_client: TelegramBotClient,
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
    dry_run: bool = False,
) -> List[Dict[str, Any]]:
    """Checks all active athletes for un-notified completed runs and sends reviews."""
    root = project_root or PROJECT_ROOT
    sent_results: List[Dict[str, Any]] = []
    athletes = list_registered_athletes(project_root=root, registry_path=registry_path, active_only=True)

    for ctx in athletes:
        if not ctx.post_run_summary_enabled or not ctx.telegram_chat_id:
            continue
        if not ctx.activities_path.is_file():
            continue
        try:
            activities = load_activities(ctx.activities_path)
            runs = filter_runs(activities)
            if not runs:
                continue
            latest_run = sorted(runs, key=lambda r: str(r.get("start_date_local") or ""), reverse=True)[0]
            latest_id = str(latest_run.get("id") or "0")
            state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
            state = load_telegram_state(state_path)
            notified = set(state.get("notified_activity_ids") or [])
            if latest_id in notified:
                continue

            res = send_post_run_review_for_athlete(
                ctx=ctx,
                bot_client=bot_client,
                activity=latest_run,
                project_root=root,
                dry_run=dry_run,
            )
            sent_results.append(res)
        except Exception as e:
            print(f"[WARN] Post-run review for athlete '{ctx.slug}' failed: {e}", file=sys.stderr)

    return sent_results


def run_bot_daemon(
    bot_client: TelegramBotClient,
    intervals_api_key: str,
    intervals_api_base: str = "https://intervals.icu/api/v1",
    llm_client: Optional[LLMClient] = None,
    poll_timeout: int = 10,
    allowed_user_ids: Optional[List[str]] = None,
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
    dry_run: bool = False,
    max_loops: Optional[int] = None,
    enable_scheduler: bool = False,
) -> None:
    """Runs the long-polling Telegram bot daemon. Scheduled briefings can be enabled via enable_scheduler."""
    root = project_root or PROJECT_ROOT
    offset: Optional[int] = None
    loops = 0

    mode_label = "Long-Polling + Scheduler" if enable_scheduler else "Long-Polling Listener"
    print("======================================================================")
    print(f"  Laufen Telegram Coach Bot - Active Daemon ({mode_label})")
    print("======================================================================")

    while True:
        loops += 1
        try:
            if enable_scheduler:
                check_and_send_due_briefings(
                    bot_client=bot_client,
                    intervals_api_key=intervals_api_key,
                    intervals_api_base=intervals_api_base,
                    project_root=root,
                    registry_path=registry_path,
                    dry_run=dry_run,
                )
                check_and_send_due_evening_previews(
                    bot_client=bot_client,
                    intervals_api_key=intervals_api_key,
                    intervals_api_base=intervals_api_base,
                    project_root=root,
                    registry_path=registry_path,
                    dry_run=dry_run,
                )
                check_and_send_due_post_run_reviews(
                    bot_client=bot_client,
                    project_root=root,
                    registry_path=registry_path,
                    dry_run=dry_run,
                )

            updates = bot_client.get_updates(offset=offset, timeout=poll_timeout)
            for upd in updates:
                upd_id = upd.get("update_id")
                if isinstance(upd_id, int):
                    offset = upd_id + 1
                handle_telegram_update(
                    update=upd,
                    bot_client=bot_client,
                    intervals_api_key=intervals_api_key,
                    intervals_api_base=intervals_api_base,
                    llm_client=llm_client,
                    allowed_user_ids=allowed_user_ids,
                    project_root=root,
                    registry_path=registry_path,
                    dry_run=dry_run,
                )
        except KeyboardInterrupt:
            print("\n[INFO] Shutting down Telegram Bot daemon.")
            break
        except Exception as e:
            print(f"[ERROR] Telegram polling cycle error: {e}", file=sys.stderr)
            if max_loops is None:
                time.sleep(5)

        if max_loops is not None and loops >= max_loops:
            break


def main() -> None:
    dotenv_path = os.getenv("DOTENV_PATH")
    load_env_file(Path(dotenv_path) if dotenv_path else PROJECT_ROOT / ".env")

    parser = argparse.ArgumentParser(
        description="Telegram Bot Coach & Proactive Briefing Runner for Laufen Hub."
    )
    parser.add_argument(
        "--briefing",
        action="store_true",
        help="Immediately sync and send the daily workout briefing to the target athlete",
    )
    parser.add_argument(
        "--evening",
        action="store_true",
        help="Immediately send the evening preview for tomorrow to the target athlete",
    )
    parser.add_argument(
        "--review",
        action="store_true",
        help="Immediately send the post-run coach review for the latest run to the target athlete",
    )
    parser.add_argument(
        "--once",
        action="store_true",
        help="Process pending Telegram updates and due briefings once, then exit",
    )
    parser.add_argument(
        "--link-chat",
        metavar="CHAT_ID",
        help="Manually link a Telegram chat_id to the target athlete in registry.json",
    )
    parser.add_argument(
        "--briefing-time",
        metavar="HH:MM",
        help="Set the daily briefing time (e.g. '07:00') for the target athlete",
    )
    parser.add_argument(
        "--athlete",
        "-a",
        default=os.getenv("ATHLETE_SLUG"),
        help="Target athlete slug (defaults to ATHLETE_SLUG or default_athlete)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Simulate Intervals.icu workout modifications without writing to remote calendar",
    )
    parser.add_argument(
        "--telegram-api-base",
        default=os.getenv("TELEGRAM_API_BASE", "https://api.telegram.org"),
        help="Telegram Bot API base URL",
    )
    parser.add_argument(
        "--intervals-api-base",
        default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"),
        help="Intervals.icu API base URL",
    )
    parser.add_argument(
        "--scheduler",
        action="store_true",
        help="Enable automatic scheduled briefing timer in daemon loop (default: disabled, briefings triggered externally)",
    )
    parser.add_argument(
        "--llm-provider",
        default=os.getenv("LLM_PROVIDER"),
        help="Optional LLM provider ('agy', 'gemini', 'mock', or 'none' for heuristic only)",
    )
    parser.add_argument(
        "--allowed-user-ids",
        default=os.getenv("TELEGRAM_ALLOWED_USER_IDS", ""),
        help="Comma-separated Telegram user IDs allowed to interact with the bot (e.g. '12345678,87654321')",
    )

    args = parser.parse_args()

    if args.link_chat or args.briefing_time:
        ctx = resolve_athlete_context(args.athlete, project_root=PROJECT_ROOT)
        chat_to_set = args.link_chat or ctx.telegram_chat_id
        if not chat_to_set:
            sys.exit("Error: Please provide --link-chat <CHAT_ID>.")
        updated_ctx = link_telegram_chat_id(
            athlete_slug=ctx.slug,
            chat_id=chat_to_set,
            briefing_time=args.briefing_time,
            project_root=PROJECT_ROOT,
        )
        print(
            f"✅ Linked athlete '{updated_ctx.slug}' ({updated_ctx.display_name}) "
            f"-> Telegram Chat ID: {updated_ctx.telegram_chat_id} | Briefing Time: {updated_ctx.briefing_time}"
        )
        if not args.briefing and not args.once:
            return

    bot_token = os.getenv("TELEGRAM_BOT_TOKEN", "")
    if not bot_token:
        sys.exit(
            "Error: TELEGRAM_BOT_TOKEN is not set in environment or .env.\n"
            "Please add TELEGRAM_BOT_TOKEN=<your_token> to .env."
        )

    intervals_key = os.getenv("INTERVALS_API_KEY", "")
    bot_client = TelegramBotClient(
        bot_token=bot_token,
        api_base_url=args.telegram_api_base,
    )

    llm: Optional[LLMClient] = None
    if args.llm_provider and args.llm_provider.lower() != "none":
        try:
            llm = get_llm_client(provider=args.llm_provider)
        except Exception:
            llm = None

    if args.briefing:
        ctx = resolve_athlete_context(args.athlete, project_root=PROJECT_ROOT)
        res = send_daily_briefing_for_athlete(
            ctx=ctx,
            bot_client=bot_client,
            intervals_api_key=intervals_key,
            intervals_api_base=args.intervals_api_base,
            dry_run=args.dry_run,
            project_root=PROJECT_ROOT,
        )
        print(
            f"✅ Daily briefing sent to {ctx.display_name} (Chat ID: {ctx.telegram_chat_id}) "
            f"for target date {res['target_date']}."
        )
        return

    if args.evening:
        ctx = resolve_athlete_context(args.athlete, project_root=PROJECT_ROOT)
        res = send_evening_preview_for_athlete(
            ctx=ctx,
            bot_client=bot_client,
            intervals_api_key=intervals_key,
            intervals_api_base=args.intervals_api_base,
            dry_run=args.dry_run,
            project_root=PROJECT_ROOT,
        )
        print(
            f"✅ Evening preview sent to {ctx.display_name} (Chat ID: {ctx.telegram_chat_id}) "
            f"for tomorrow ({res['target_date']})."
        )
        return

    if args.review:
        ctx = resolve_athlete_context(args.athlete, project_root=PROJECT_ROOT)
        res = send_post_run_review_for_athlete(
            ctx=ctx,
            bot_client=bot_client,
            dry_run=args.dry_run,
            project_root=PROJECT_ROOT,
        )
        print(
            f"✅ Post-run review sent to {ctx.display_name} (Chat ID: {ctx.telegram_chat_id}) "
            f"for activity {res.get('activity_id', 'latest')}."
        )
        return

    allowed_user_ids = parse_allowed_user_ids(args.allowed_user_ids)

    if args.once:
        run_bot_daemon(
            bot_client=bot_client,
            intervals_api_key=intervals_key,
            intervals_api_base=args.intervals_api_base,
            llm_client=llm,
            poll_timeout=1,
            allowed_user_ids=allowed_user_ids,
            project_root=PROJECT_ROOT,
            dry_run=args.dry_run,
            max_loops=1,
            enable_scheduler=args.scheduler,
        )
        return

    run_bot_daemon(
        bot_client=bot_client,
        intervals_api_key=intervals_key,
        intervals_api_base=args.intervals_api_base,
        llm_client=llm,
        poll_timeout=15,
        allowed_user_ids=allowed_user_ids,
        project_root=PROJECT_ROOT,
        dry_run=args.dry_run,
        enable_scheduler=args.scheduler,
    )


if __name__ == "__main__":
    main()
