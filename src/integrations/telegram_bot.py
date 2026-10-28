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

from src.analysis.telegram_coach import (
    build_payload_from_workout_params,
    evaluate_athlete_feedback,
)
from src.athletes import (
    AthleteContext,
    link_telegram_chat_id,
    list_registered_athletes,
    load_registry,
    resolve_athlete_by_telegram_chat_id,
    resolve_athlete_context,
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
    ) -> Dict[str, Any]:
        payload: Dict[str, Any] = {"callback_query_id": callback_query_id}
        if text:
            payload["text"] = text
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


def handle_telegram_update(
    update: Dict[str, Any],
    bot_client: TelegramBotClient,
    intervals_api_key: str,
    intervals_api_base: str = "https://intervals.icu/api/v1",
    llm_client: Optional[LLMClient] = None,
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
    ref_date: Optional[datetime.date] = None,
    dry_run: bool = False,
) -> Dict[str, Any]:
    """Processes a single incoming Telegram update (text message or callback button click)."""
    root = project_root or PROJECT_ROOT
    today = ref_date or datetime.date.today()

    # Case 1: Inline Keyboard Button Click (callback_query)
    if "callback_query" in update:
        cb = update["callback_query"]
        cb_id = str(cb.get("id") or "")
        data = str(cb.get("data") or "")
        msg = cb.get("message") or {}
        chat_id = str((msg.get("chat") or {}).get("id") or "")

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
    text = str(message.get("text") or "").strip()

    if not chat_id:
        return {"status": "ignored"}

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


def run_bot_daemon(
    bot_client: TelegramBotClient,
    intervals_api_key: str,
    intervals_api_base: str = "https://intervals.icu/api/v1",
    llm_client: Optional[LLMClient] = None,
    poll_timeout: int = 10,
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

    if args.once:
        run_bot_daemon(
            bot_client=bot_client,
            intervals_api_key=intervals_key,
            intervals_api_base=args.intervals_api_base,
            llm_client=llm,
            poll_timeout=1,
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
        project_root=PROJECT_ROOT,
        dry_run=args.dry_run,
        enable_scheduler=args.scheduler,
    )


if __name__ == "__main__":
    main()
