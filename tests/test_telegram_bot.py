import datetime
import http.server
import json
from pathlib import Path
import sys
import threading
from typing import Any, Dict, List, Optional
import pytest
from pytest_bdd import given, parsers, scenarios, then, when

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.athletes import (
    link_telegram_chat_id,
    load_registry,
    resolve_athlete_context,
    save_registry,
)
from src.integrations.telegram_bot import (
    TelegramBotClient,
    check_and_send_due_briefings,
    check_and_send_due_evening_previews,
    check_and_send_due_post_run_reviews,
    handle_telegram_update,
    load_telegram_state,
    save_telegram_state,
    send_daily_briefing_for_athlete,
    send_evening_preview_for_athlete,
    send_post_run_review_for_athlete,
)

# 1. Bind feature scenarios
scenarios("telegram_bot.feature")


# 2. Mock HTTP Handler for both Telegram Bot API and Intervals.icu API
class MockCombinedAPIHandler(http.server.BaseHTTPRequestHandler):
    sent_telegram_messages: List[Dict[str, Any]] = []
    intervals_events: List[Dict[str, Any]] = []
    intervals_activities: List[Dict[str, Any]] = []
    deleted_event_ids: List[str] = []
    updated_activities: Dict[str, Dict[str, Any]] = {}
    next_event_id: int = 100

    def do_GET(self):
        if "/athlete/" in self.path and "/events" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(self.intervals_events).encode("utf-8"))
        elif "/athlete/" in self.path and "/activities" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(self.intervals_activities).encode("utf-8"))
        else:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b"[]")

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8") if length > 0 else "{}"
        payload = json.loads(body) if body else {}

        # Telegram API endpoints
        if "/sendMessage" in self.path:
            self.sent_telegram_messages.append(payload)
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"ok": True, "result": {"message_id": len(self.sent_telegram_messages)}}).encode("utf-8"))
            return

        if "/answerCallbackQuery" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"ok": true, "result": true}')
            return

        if "/getUpdates" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"ok": true, "result": []}')
            return

        # Intervals.icu POST event
        if "/athlete/" in self.path and "/events" in self.path:
            MockCombinedAPIHandler.next_event_id += 1
            ev = dict(payload)
            ev["id"] = f"ev_{MockCombinedAPIHandler.next_event_id}"
            self.intervals_events.append(ev)
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(ev).encode("utf-8"))
            return

        self.send_response(404)
        self.end_headers()

    def do_PUT(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8") if length > 0 else "{}"
        payload = json.loads(body) if body else {}

        if "/activity/" in self.path:
            act_id = self.path.rstrip("/").split("/")[-1]
            MockCombinedAPIHandler.updated_activities[act_id] = payload
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"id": act_id, **payload}).encode("utf-8"))
            return

        self.send_response(404)
        self.end_headers()

    def do_DELETE(self):
        if "/athlete/" in self.path and "/events/" in self.path:
            ev_id = self.path.rstrip("/").split("/")[-1]
            self.deleted_event_ids.append(ev_id)
            MockCombinedAPIHandler.intervals_events = [
                e for e in MockCombinedAPIHandler.intervals_events if str(e.get("id")) != ev_id
            ]
            self.send_response(200)
            self.end_headers()
            return
        self.send_response(404)
        self.end_headers()

    def log_message(self, format, *args):
        pass


# 3. Driver Class
class TelegramBotDriver:
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.registry_path = tmp_path / "athletes" / "registry.json"

        # Copy workout templates into tmp_path so schedule_workout can render them
        src_templates = PROJECT_ROOT / "workouts" / "templates"
        dst_templates = tmp_path / "workouts" / "templates"
        dst_templates.mkdir(parents=True, exist_ok=True)
        for tmpl in src_templates.glob("*.txt"):
            (dst_templates / tmpl.name).write_text(tmpl.read_text(encoding="utf-8"), encoding="utf-8")

        MockCombinedAPIHandler.sent_telegram_messages = []
        MockCombinedAPIHandler.intervals_events = []
        MockCombinedAPIHandler.intervals_activities = []
        MockCombinedAPIHandler.deleted_event_ids = []
        MockCombinedAPIHandler.updated_activities = {}
        MockCombinedAPIHandler.next_event_id = 100

        self.server = http.server.HTTPServer(("127.0.0.1", 0), MockCombinedAPIHandler)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

        self.base_url = f"http://127.0.0.1:{self.port}"
        self.bot_client = TelegramBotClient(
            bot_token="123456:TEST_TOKEN",
            api_base_url=self.base_url,
        )
        self.allowed_user_ids: Optional[List[str]] = None

    def set_allowed_user_ids(self, ids_str: str) -> None:
        self.allowed_user_ids = [i.strip() for i in ids_str.split(",") if i.strip()]

    def stop(self) -> None:
        self.server.shutdown()
        self.server.server_close()

    def setup_athlete(
        self,
        slug: str,
        intervals_id: str = "i697752",
        chat_id: str = "",
        briefing_time: str = "07:00",
    ) -> None:
        save_registry(
            {
                "default_athlete": slug,
                "athletes": {
                    slug: {
                        "intervals_id": intervals_id,
                        "display_name": slug.capitalize(),
                        "active": True,
                        "dashboard_slug": slug,
                        "telegram_chat_id": chat_id,
                        "briefing_time": briefing_time,
                        "briefing_enabled": True,
                    }
                },
            },
            registry_path=self.registry_path,
            project_root=self.tmp_path,
        )
        ctx = resolve_athlete_context(slug, project_root=self.tmp_path, registry_path=self.registry_path)
        ctx.ensure_dirs()
        if not ctx.athlete_config_path.is_file():
            ctx.athlete_config_path.write_text(
                json.dumps({
                    "athlete": {
                        "id": intervals_id,
                        "name": slug.capitalize(),
                        "heart_rate": {
                            "lthr": 178,
                            "max": 195,
                            "zones": {
                                "z1_recovery": {"max": 142},
                                "z2_aerobic": {"max": 158},
                                "z3_tempo": {"max": 168},
                                "z4_threshold": {"max": 178},
                                "z5_vo2max": {"max": 195},
                            },
                        },
                    }
                }),
                encoding="utf-8",
            )
        if not ctx.activities_path.is_file():
            ctx.activities_path.write_text("[]", encoding="utf-8")
        if not ctx.wellness_path.is_file():
            ctx.wellness_path.write_text("[]", encoding="utf-8")

    def set_evening_briefing(self, slug: str, enabled: bool, time_str: str = "20:30") -> None:
        reg = load_registry(self.registry_path, self.tmp_path)
        reg["athletes"][slug]["evening_briefing_enabled"] = enabled
        reg["athletes"][slug]["evening_briefing_time"] = time_str
        save_registry(reg, registry_path=self.registry_path, project_root=self.tmp_path)

    def set_post_run_reviews(self, slug: str, enabled: bool) -> None:
        reg = load_registry(self.registry_path, self.tmp_path)
        reg["athletes"][slug]["post_run_summary_enabled"] = enabled
        save_registry(reg, registry_path=self.registry_path, project_root=self.tmp_path)

    def add_completed_run(self, slug: str, act_id: str, name: str, date_str: str) -> None:
        ctx = resolve_athlete_context(slug, project_root=self.tmp_path, registry_path=self.registry_path)
        activities = []
        if ctx.activities_path.is_file():
            try:
                activities = json.loads(ctx.activities_path.read_text(encoding="utf-8"))
            except Exception:
                activities = []
        act = {
            "id": act_id,
            "type": "Run",
            "name": name,
            "start_date_local": f"{date_str}T18:00:00",
            "distance": 8000.0,
            "moving_time": 2880,
            "elapsed_time": 2900,
            "average_heartrate": 150,
            "max_heartrate": 162,
            "average_speed": 2.78,
            "icu_training_load": 45,
            "icu_ctl": 25.0,
            "icu_atl": 24.0,
            "icu_hr_zones": [300, 2200, 380, 0, 0, 0, 0],
        }
        activities.append(act)
        ctx.activities_path.write_text(json.dumps(activities), encoding="utf-8")
        MockCombinedAPIHandler.intervals_activities = list(activities)

    def set_last_run_days_ago(self, slug: str, days_ago: int, ref_date_str: str) -> None:
        ctx = resolve_athlete_context(slug, project_root=self.tmp_path, registry_path=self.registry_path)
        ref_date = datetime.date.fromisoformat(ref_date_str)
        run_date = (ref_date - datetime.timedelta(days=days_ago)).isoformat()
        activities = [
            {
                "id": "act_1",
                "type": "Run",
                "name": "Easy Run",
                "start_date_local": f"{run_date}T08:00:00",
                "distance": 6000.0,
                "moving_time": 2400,
                "average_heartrate": 148,
                "icu_training_load": 35,
                "icu_ctl": 22.0,
                "icu_atl": 20.0,
            }
        ]
        ctx.activities_path.write_text(json.dumps(activities), encoding="utf-8")
        MockCombinedAPIHandler.intervals_activities = list(activities)

    def seed_active_proposal(self, slug: str, date_str: str) -> None:
        ctx = resolve_athlete_context(slug, project_root=self.tmp_path, registry_path=self.registry_path)
        ev = {
            "id": "ev_initial",
            "category": "WORKOUT",
            "start_date_local": f"{date_str}T07:00:00",
            "type": "Run",
            "name": "Intervall- & Kadenztraining (5x 1m Speed)",
            "description": "- 10m Z2 HR\n- 5x 1m 05:15/km Pace",
        }
        MockCombinedAPIHandler.intervals_events.append(ev)
        state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
        save_telegram_state(
            state_path,
            {
                "last_briefing_date": date_str,
                "current_proposal": {
                    "target_date": date_str,
                    "name": ev["name"],
                    "description": ev["description"],
                    "event_id": ev["id"],
                    "is_rest_day_today": False,
                },
            },
        )

    def setup_pending_run_note(self, slug: str, act_id: str, note_text: str) -> None:
        ctx = resolve_athlete_context(slug, project_root=self.tmp_path, registry_path=self.registry_path)
        state_path = ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")
        state = load_telegram_state(state_path)
        state["pending_run_note"] = {
            "activity_id": act_id,
            "activity_name": "3x8m Schwellenintervalle",
            "date": "2026-10-30 08:00",
            "note_text": note_text,
        }
        save_telegram_state(state_path, state)

    def send_incoming_text(
        self,
        text: str,
        chat_id: str,
        user_id: Optional[str] = None,
        ref_date_str: str = "2026-10-26",
    ) -> None:
        effective_from_id = user_id or chat_id
        update = {
            "update_id": 1,
            "message": {
                "message_id": 10,
                "chat": {"id": chat_id},
                "from": {"id": int(effective_from_id) if effective_from_id.isdigit() else effective_from_id},
                "text": text,
            },
        }
        handle_telegram_update(
            update=update,
            bot_client=self.bot_client,
            intervals_api_key="test_intervals_key",
            intervals_api_base=self.base_url,
            allowed_user_ids=self.allowed_user_ids,
            project_root=self.tmp_path,
            registry_path=self.registry_path,
            ref_date=datetime.date.fromisoformat(ref_date_str),
        )

    def send_callback_query(
        self,
        data: str,
        chat_id: str,
        user_id: Optional[str] = None,
        ref_date_str: str = "2026-10-26",
    ) -> None:
        effective_from_id = user_id or chat_id
        update = {
            "update_id": 2,
            "callback_query": {
                "id": "cb_99",
                "data": data,
                "from": {"id": int(effective_from_id) if effective_from_id.isdigit() else effective_from_id},
                "message": {"chat": {"id": chat_id}},
            },
        }
        handle_telegram_update(
            update=update,
            bot_client=self.bot_client,
            intervals_api_key="test_intervals_key",
            intervals_api_base=self.base_url,
            allowed_user_ids=self.allowed_user_ids,
            project_root=self.tmp_path,
            registry_path=self.registry_path,
            ref_date=datetime.date.fromisoformat(ref_date_str),
        )

    def trigger_briefing(self, slug: str, date_str: str) -> None:
        ctx = resolve_athlete_context(slug, project_root=self.tmp_path, registry_path=self.registry_path)
        send_daily_briefing_for_athlete(
            ctx=ctx,
            bot_client=self.bot_client,
            intervals_api_key="test_intervals_key",
            intervals_api_base=self.base_url,
            ref_date=datetime.date.fromisoformat(date_str),
            project_root=self.tmp_path,
        )

    def run_due_briefing_check(self, iso_dt_str: str) -> None:
        dt = datetime.datetime.fromisoformat(iso_dt_str)
        check_and_send_due_briefings(
            bot_client=self.bot_client,
            intervals_api_key="test_intervals_key",
            intervals_api_base=self.base_url,
            now=dt,
            project_root=self.tmp_path,
            registry_path=self.registry_path,
        )

    def run_due_evening_check(self, iso_dt_str: str) -> None:
        dt = datetime.datetime.fromisoformat(iso_dt_str)
        check_and_send_due_evening_previews(
            bot_client=self.bot_client,
            intervals_api_key="test_intervals_key",
            intervals_api_base=self.base_url,
            now=dt,
            project_root=self.tmp_path,
            registry_path=self.registry_path,
        )

    def run_post_run_review_check(self, slug: str) -> None:
        check_and_send_due_post_run_reviews(
            bot_client=self.bot_client,
            project_root=self.tmp_path,
            registry_path=self.registry_path,
        )


@pytest.fixture
def driver(tmp_path: Path):
    d = TelegramBotDriver(tmp_path)
    yield d
    d.stop()


# 4. Step Definitions
@given(parsers.parse('the athlete registry contains athlete "{slug}" with intervals id "{intervals_id}"'))
def given_athlete_in_registry(driver: TelegramBotDriver, slug: str, intervals_id: str):
    driver.setup_athlete(slug=slug, intervals_id=intervals_id)


@given(parsers.parse('the athlete "{slug}" is linked to Telegram chat id "{chat_id}" with briefing time "{b_time}"'))
def given_linked_athlete(driver: TelegramBotDriver, slug: str, chat_id: str, b_time: str):
    driver.setup_athlete(slug=slug, chat_id=chat_id, briefing_time=b_time)


@given(parsers.parse('the athlete "{slug}" last ran {days:d} days ago before "{ref_date}"'))
def given_last_run_days_ago(driver: TelegramBotDriver, slug: str, days: int, ref_date: str):
    driver.set_last_run_days_ago(slug=slug, days_ago=days, ref_date_str=ref_date)


@given(parsers.parse('the athlete "{slug}" has an active briefing proposal on "{date_str}"'))
def given_active_proposal(driver: TelegramBotDriver, slug: str, date_str: str):
    driver.seed_active_proposal(slug=slug, date_str=date_str)


@when(parsers.parse('the Telegram bot receives message "{text}" from chat id "{chat_id}"'))
def when_bot_receives_message(driver: TelegramBotDriver, text: str, chat_id: str):
    driver.send_incoming_text(text=text, chat_id=chat_id)


@when(parsers.parse('the Telegram bot receives callback query "{cb_data}" from chat id "{chat_id}"'))
def when_bot_receives_callback(driver: TelegramBotDriver, cb_data: str, chat_id: str):
    driver.send_callback_query(data=cb_data, chat_id=chat_id)


@when(parsers.parse('the daily briefing is triggered for athlete "{slug}" on "{date_str}"'))
def when_briefing_triggered(driver: TelegramBotDriver, slug: str, date_str: str):
    driver.trigger_briefing(slug=slug, date_str=date_str)


@when(parsers.parse('the scheduled briefing check runs at "{iso_dt}"'))
@when(parsers.parse('the scheduled briefing check runs again at "{iso_dt}"'))
def when_scheduled_check_runs(driver: TelegramBotDriver, iso_dt: str):
    driver.run_due_briefing_check(iso_dt_str=iso_dt)


@then(parsers.parse('the athlete "{slug}" has linked Telegram chat id "{expected_chat_id}" in the registry'))
def then_verify_linked_chat_id(driver: TelegramBotDriver, slug: str, expected_chat_id: str):
    reg = load_registry(registry_path=driver.registry_path, project_root=driver.tmp_path)
    assert reg["athletes"][slug]["telegram_chat_id"] == expected_chat_id


@then(parsers.parse('the last Telegram message sent to "{chat_id}" contains "{snippet}"'))
def then_verify_last_telegram_message(driver: TelegramBotDriver, chat_id: str, snippet: str):
    msgs = [m for m in MockCombinedAPIHandler.sent_telegram_messages if str(m.get("chat_id")) == chat_id]
    assert len(msgs) > 0, f"No Telegram messages sent to {chat_id}"
    assert snippet in msgs[-1]["text"]


@then(parsers.parse('the last Telegram message sent to "{chat_id}" includes inline keyboard buttons'))
def then_verify_inline_keyboard(driver: TelegramBotDriver, chat_id: str):
    msgs = [m for m in MockCombinedAPIHandler.sent_telegram_messages if str(m.get("chat_id")) == chat_id]
    assert len(msgs) > 0
    markup = msgs[-1].get("reply_markup") or {}
    assert "inline_keyboard" in markup
    assert len(markup["inline_keyboard"]) >= 2


@then(parsers.parse('a workout is scheduled on Intervals.icu for "{date_str}"'))
def then_verify_workout_scheduled(driver: TelegramBotDriver, date_str: str):
    matching = [
        e for e in MockCombinedAPIHandler.intervals_events
        if str(e.get("start_date_local") or "").startswith(date_str)
    ]
    assert len(matching) == 1


@then(parsers.parse('the previous workout on "{date_str}" is replaced on Intervals.icu with a workout containing "{snippet}"'))
def then_verify_workout_replaced(driver: TelegramBotDriver, date_str: str, snippet: str):
    assert "ev_initial" in MockCombinedAPIHandler.deleted_event_ids
    matching = [
        e for e in MockCombinedAPIHandler.intervals_events
        if str(e.get("start_date_local") or "").startswith(date_str)
    ]
    assert len(matching) == 1
    assert snippet in matching[0]["name"] or snippet in matching[0]["description"]


@then(parsers.parse('the planned workout on "{date_str}" is deleted from Intervals.icu'))
def then_verify_workout_deleted(driver: TelegramBotDriver, date_str: str):
    assert "ev_initial" in MockCombinedAPIHandler.deleted_event_ids
    workouts = [
        e for e in MockCombinedAPIHandler.intervals_events
        if str(e.get("start_date_local") or "").startswith(date_str)
        and e.get("category") == "WORKOUT"
    ]
    assert len(workouts) == 0


@then(parsers.parse('exactly {count:d} Telegram message was sent to "{chat_id}"'))
def then_verify_message_count(driver: TelegramBotDriver, count: int, chat_id: str):
    msgs = [m for m in MockCombinedAPIHandler.sent_telegram_messages if str(m.get("chat_id")) == chat_id]
    assert len(msgs) == count


@then(parsers.parse('a rest day note is created on Intervals.icu for "{date_str}"'))
def then_verify_rest_note_created(driver: TelegramBotDriver, date_str: str):
    matching = [
        e for e in MockCombinedAPIHandler.intervals_events
        if str(e.get("start_date_local") or "").startswith(date_str)
        and e.get("category") == "NOTE"
        and "ruhetag" in str(e.get("name") or "").lower()
    ]
    assert len(matching) >= 1, f"Expected rest day note for {date_str}, found {MockCombinedAPIHandler.intervals_events}"


@then(parsers.parse('the last Telegram message sent to "{chat_id}" includes rest day keyboard buttons'))
def then_verify_rest_day_keyboard(driver: TelegramBotDriver, chat_id: str):
    msgs = [m for m in MockCombinedAPIHandler.sent_telegram_messages if str(m.get("chat_id")) == chat_id]
    assert len(msgs) > 0
    markup = msgs[-1].get("reply_markup") or {}
    assert "inline_keyboard" in markup
    first_row = markup["inline_keyboard"][0]
    callbacks = [btn.get("callback_data", "") for btn in first_row]
    assert any("keep_rest" in cb for cb in callbacks), f"Expected keep_rest in callbacks: {callbacks}"


@given(parsers.parse('the athlete "{slug}" has evening briefing enabled with time "{e_time}"'))
def given_athlete_evening_briefing(driver: TelegramBotDriver, slug: str, e_time: str):
    driver.set_evening_briefing(slug=slug, enabled=True, time_str=e_time)


@given(parsers.parse('the athlete "{slug}" has completed a run "{act_id}" with name "{act_name}" on "{run_date}"'))
def given_athlete_completed_run(driver: TelegramBotDriver, slug: str, act_id: str, act_name: str, run_date: str):
    driver.add_completed_run(slug=slug, act_id=act_id, name=act_name, date_str=run_date)


@given(parsers.parse('the athlete "{slug}" has post run reviews disabled'))
def given_athlete_post_run_disabled(driver: TelegramBotDriver, slug: str):
    driver.set_post_run_reviews(slug=slug, enabled=False)


@when(parsers.parse('the scheduled evening preview check runs at "{iso_dt}"'))
def when_evening_check_runs(driver: TelegramBotDriver, iso_dt: str):
    driver.run_due_evening_check(iso_dt_str=iso_dt)


@when(parsers.parse('the post-run review check is triggered for athlete "{slug}"'))
def when_post_run_check_triggered(driver: TelegramBotDriver, slug: str):
    driver.run_post_run_review_check(slug=slug)


@then(parsers.parse('the athlete "{slug}" has morning briefing disabled in the registry'))
def then_morning_disabled(driver: TelegramBotDriver, slug: str):
    reg = load_registry(registry_path=driver.registry_path, project_root=driver.tmp_path)
    ath = reg["athletes"][slug]
    assert ath.get("morning_briefing_enabled") is False or ath.get("briefing_enabled") is False


@then(parsers.parse('the athlete "{slug}" has evening briefing enabled in the registry'))
def then_evening_enabled(driver: TelegramBotDriver, slug: str):
    reg = load_registry(registry_path=driver.registry_path, project_root=driver.tmp_path)
    assert reg["athletes"][slug].get("evening_briefing_enabled") is True


@then(parsers.parse('the last Telegram message sent to "{chat_id}" includes settings keyboard buttons'))
def then_settings_keyboard(driver: TelegramBotDriver, chat_id: str):
    msgs = [m for m in MockCombinedAPIHandler.sent_telegram_messages if str(m.get("chat_id")) == chat_id]
    assert len(msgs) > 0
    markup = msgs[-1].get("reply_markup") or {}
    assert "inline_keyboard" in markup
    assert len(markup["inline_keyboard"]) >= 2
    callbacks = [btn.get("callback_data", "") for row in markup["inline_keyboard"] for btn in row]
    assert any("toggle:morning" in cb for cb in callbacks)


@given(parsers.parse('the Telegram bot has allowed user ids "{ids_str}"'))
def given_allowed_user_ids(driver: TelegramBotDriver, ids_str: str):
    driver.set_allowed_user_ids(ids_str)


@when(parsers.parse('the Telegram bot receives message "{text}" from user id "{user_id}" in chat id "{chat_id}"'))
def when_bot_receives_message_with_user_id(driver: TelegramBotDriver, text: str, user_id: str, chat_id: str):
    driver.send_incoming_text(text=text, chat_id=chat_id, user_id=user_id)


@when(parsers.parse('the Telegram bot receives callback query "{cb_data}" from user id "{user_id}" in chat id "{chat_id}"'))
def when_bot_receives_callback_with_user_id(driver: TelegramBotDriver, cb_data: str, user_id: str, chat_id: str):
    driver.send_callback_query(data=cb_data, chat_id=chat_id, user_id=user_id)


@then(parsers.parse('the athlete "{slug}" does not have linked Telegram chat id "{chat_id}" in the registry'))
def then_verify_not_linked_chat_id(driver: TelegramBotDriver, slug: str, chat_id: str):
    reg = load_registry(registry_path=driver.registry_path, project_root=driver.tmp_path)
    assert reg.get("athletes", {}).get(slug, {}).get("telegram_chat_id") != chat_id


@then(parsers.parse('the planned workout on "{date_str}" is not modified on Intervals.icu'))
def then_verify_workout_not_modified(driver: TelegramBotDriver, date_str: str):
    assert "ev_initial" not in MockCombinedAPIHandler.deleted_event_ids


@given(parsers.parse('the athlete "{slug}" has a pending run note for "{act_id}" with text "{note_text}"'))
def given_athlete_pending_run_note(driver: TelegramBotDriver, slug: str, act_id: str, note_text: str):
    driver.setup_pending_run_note(slug=slug, act_id=act_id, note_text=note_text)


@then(parsers.parse('the last Telegram message sent to "{chat_id}" includes note confirmation keyboard buttons'))
def then_note_confirmation_keyboard(driver: TelegramBotDriver, chat_id: str):
    msgs = [m for m in MockCombinedAPIHandler.sent_telegram_messages if str(m.get("chat_id")) == chat_id]
    assert len(msgs) > 0
    markup = msgs[-1].get("reply_markup") or {}
    assert "inline_keyboard" in markup
    callbacks = [btn.get("callback_data", "") for row in markup["inline_keyboard"] for btn in row]
    assert any("note_confirm:" in cb for cb in callbacks)
    assert any("note_pick_other" in cb for cb in callbacks)
    assert any("note_cancel" in cb for cb in callbacks)


@then(parsers.parse('the activity "{act_id}" is updated on Intervals.icu with description "{expected_desc}"'))
def then_activity_updated_on_intervals(driver: TelegramBotDriver, act_id: str, expected_desc: str):
    assert act_id in MockCombinedAPIHandler.updated_activities
    assert MockCombinedAPIHandler.updated_activities[act_id].get("description") == expected_desc



