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
    handle_telegram_update,
    save_telegram_state,
    send_daily_briefing_for_athlete,
)

# 1. Bind feature scenarios
scenarios("telegram_bot.feature")


# 2. Mock HTTP Handler for both Telegram Bot API and Intervals.icu API
class MockCombinedAPIHandler(http.server.BaseHTTPRequestHandler):
    sent_telegram_messages: List[Dict[str, Any]] = []
    intervals_events: List[Dict[str, Any]] = []
    deleted_event_ids: List[str] = []
    next_event_id: int = 100

    def do_GET(self):
        if "/athlete/" in self.path and "/events" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(self.intervals_events).encode("utf-8"))
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
        MockCombinedAPIHandler.deleted_event_ids = []
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
                json.dumps({"athlete": {"id": intervals_id, "name": slug.capitalize()}}),
                encoding="utf-8",
            )
        if not ctx.activities_path.is_file():
            ctx.activities_path.write_text("[]", encoding="utf-8")
        if not ctx.wellness_path.is_file():
            ctx.wellness_path.write_text("[]", encoding="utf-8")

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

    def send_incoming_text(self, text: str, chat_id: str, ref_date_str: str = "2026-10-26") -> None:
        update = {
            "update_id": 1,
            "message": {
                "message_id": 10,
                "chat": {"id": chat_id},
                "text": text,
            },
        }
        handle_telegram_update(
            update=update,
            bot_client=self.bot_client,
            intervals_api_key="test_intervals_key",
            intervals_api_base=self.base_url,
            project_root=self.tmp_path,
            registry_path=self.registry_path,
            ref_date=datetime.date.fromisoformat(ref_date_str),
        )

    def send_callback_query(self, data: str, chat_id: str, ref_date_str: str = "2026-10-26") -> None:
        update = {
            "update_id": 2,
            "callback_query": {
                "id": "cb_99",
                "data": data,
                "message": {"chat": {"id": chat_id}},
            },
        }
        handle_telegram_update(
            update=update,
            bot_client=self.bot_client,
            intervals_api_key="test_intervals_key",
            intervals_api_base=self.base_url,
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
    matching = [
        e for e in MockCombinedAPIHandler.intervals_events
        if str(e.get("start_date_local") or "").startswith(date_str)
    ]
    assert len(matching) == 0


@then(parsers.parse('exactly {count:d} Telegram message was sent to "{chat_id}"'))
def then_verify_message_count(driver: TelegramBotDriver, count: int, chat_id: str):
    msgs = [m for m in MockCombinedAPIHandler.sent_telegram_messages if str(m.get("chat_id")) == chat_id]
    assert len(msgs) == count
