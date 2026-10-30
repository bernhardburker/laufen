import datetime
import http.server
import json
import os
import subprocess
import sys
import threading
from pathlib import Path

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

scenarios("schedule_workout.feature")


class MockIntervalsEventHandler(http.server.BaseHTTPRequestHandler):
    received_requests: list[dict] = []
    events_db: list[dict] = []

    def do_POST(self):
        auth_header = self.headers.get("Authorization", "")
        if not auth_header.startswith("Basic "):
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error": "Unauthorized"}')
            return

        content_len = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(content_len).decode("utf-8") if content_len > 0 else "{}"
        parsed_body = json.loads(body)

        event_id = f"mock_event_{len(MockIntervalsEventHandler.received_requests) + 1}"
        new_event = {**parsed_body, "id": event_id}
        MockIntervalsEventHandler.events_db.append(new_event)

        MockIntervalsEventHandler.received_requests.append({
            "method": "POST",
            "path": self.path,
            "headers": dict(self.headers),
            "payload": parsed_body,
        })

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        response_data = {"id": event_id, "status": "SUCCESS"}
        self.wfile.write(json.dumps(response_data).encode("utf-8"))

    def do_GET(self):
        auth_header = self.headers.get("Authorization", "")
        if not auth_header.startswith("Basic "):
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error": "Unauthorized"}')
            return

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps(MockIntervalsEventHandler.events_db).encode("utf-8"))

    def do_DELETE(self):
        auth_header = self.headers.get("Authorization", "")
        if not auth_header.startswith("Basic "):
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error": "Unauthorized"}')
            return

        MockIntervalsEventHandler.received_requests.append({
            "method": "DELETE",
            "path": self.path,
            "headers": dict(self.headers),
        })

        ev_id_str = self.path.split("/")[-1]
        MockIntervalsEventHandler.events_db = [
            e for e in MockIntervalsEventHandler.events_db
            if str(e.get("id")) != ev_id_str
        ]

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"status": "DELETED"}')

    def log_message(self, format, *args):
        pass


class WorkoutScheduleDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.env: dict[str, str] = os.environ.copy()
        self.process_result: subprocess.CompletedProcess | None = None
        self.mock_server: http.server.HTTPServer | None = None
        self.mock_server_thread: threading.Thread | None = None
        self.mock_server_port: int | None = None
        self.env["DOTENV_PATH"] = str(tmp_path / ".env")
        self.events_file = tmp_path / "calendar_events.json"
        self.test_ref_date = datetime.date(2026, 10, 22)
        MockIntervalsEventHandler.received_requests.clear()
        MockIntervalsEventHandler.events_db.clear()

    def unset_api_key(self) -> None:
        self.env.pop("INTERVALS_API_KEY", None)

    def set_api_key(self, key: str = "mock_secret_key") -> None:
        self.env["INTERVALS_API_KEY"] = key

    def start_mock_api(self) -> None:
        MockIntervalsEventHandler.received_requests.clear()
        MockIntervalsEventHandler.events_db.clear()
        self.mock_server = http.server.HTTPServer(("127.0.0.1", 0), MockIntervalsEventHandler)
        self.mock_server_port = self.mock_server.server_address[1]
        self.mock_server_thread = threading.Thread(target=self.mock_server.serve_forever, daemon=True)
        self.mock_server_thread.start()

    def stop_mock_api(self) -> None:
        if self.mock_server:
            self.mock_server.shutdown()
            self.mock_server.server_close()

    def run_schedule(self, extra_args: list[str], use_mock_api: bool = False) -> None:
        cmd = [
            sys.executable,
            str(self.workspace_root / "src" / "planner" / "schedule_workout.py"),
        ]
        if use_mock_api and self.mock_server_port:
            cmd.extend(["--api-base", f"http://127.0.0.1:{self.mock_server_port}/api/v1"])

        cmd.extend(extra_args)

        self.process_result = subprocess.run(
            cmd,
            cwd=str(self.workspace_root),
            env=self.env,
            capture_output=True,
            text=True,
        )

    def get_last_received_payload(self) -> dict:
        posts = [r for r in MockIntervalsEventHandler.received_requests if "payload" in r]
        assert len(posts) > 0, "No payload requests received by mock server"
        return posts[-1]["payload"]

    def setup_mock_uncompleted_yesterday(self) -> None:
        yesterday = (self.test_ref_date - datetime.timedelta(days=1)).isoformat()
        MockIntervalsEventHandler.events_db.append({
            "id": 998877,
            "category": "WORKOUT",
            "type": "Run",
            "name": "Slot 1: Tempo Run",
            "start_date_local": f"{yesterday}T07:00:00",
            "paired_activity_id": None,
        })

    def setup_activities_no_run_yesterday(self) -> None:
        old_date = (self.test_ref_date - datetime.timedelta(days=4)).isoformat()
        activities = [
            {
                "id": "run_old",
                "type": "Run",
                "start_date_local": f"{old_date}T07:00:00",
                "name": "Easy Run",
            }
        ]
        act_path = self.tmp_path / "activities.json"
        act_path.write_text(json.dumps(activities), encoding="utf-8")

    def setup_activities_run_yesterday(self) -> None:
        yesterday = (self.test_ref_date - datetime.timedelta(days=1)).isoformat()
        activities = [
            {
                "id": "run_yesterday",
                "type": "Run",
                "start_date_local": f"{yesterday}T07:00:00",
                "name": "Tempo Run",
            }
        ]
        act_path = self.tmp_path / "activities.json"
        act_path.write_text(json.dumps(activities), encoding="utf-8")

    def run_adaptive_plan(self, with_events_file: bool = False) -> None:
        act_path = self.tmp_path / "activities.json"
        args = [
            "--adaptive-plan",
            "--ref-date", self.test_ref_date.isoformat(),
        ]
        if act_path.is_file():
            args.extend(["--activities-file", str(act_path)])
        if with_events_file:
            args.extend(["--events-file", str(self.events_file)])
        self.run_schedule(args, use_mock_api=True)

    def get_last_received_payload(self) -> dict:
        assert len(MockIntervalsEventHandler.received_requests) > 0, "No requests received by mock server"
        return MockIntervalsEventHandler.received_requests[-1]["payload"]


@pytest.fixture
def driver(tmp_path: Path):
    workspace = Path(__file__).resolve().parent.parent
    d = WorkoutScheduleDriver(workspace, tmp_path)
    yield d
    d.stop_mock_api()


# Gherkin Steps

@given("INTERVALS_API_KEY is not set")
def step_unset_api_key(driver: WorkoutScheduleDriver):
    driver.unset_api_key()


@given("INTERVALS_API_KEY is configured")
def step_set_api_key(driver: WorkoutScheduleDriver):
    driver.set_api_key("valid_test_api_key")


@given("a mock Intervals.icu event server is running")
def step_start_mock_server(driver: WorkoutScheduleDriver):
    driver.start_mock_api()


@when(parsers.parse('the workout schedule script is executed with archetype "{archetype}"'))
def step_run_archetype(driver: WorkoutScheduleDriver, archetype: str):
    driver.run_schedule(["--type", archetype, "--date", "tomorrow"])


@when(parsers.parse('the workout schedule script is executed in dry-run mode for "{archetype}"'))
def step_run_dry_run(driver: WorkoutScheduleDriver, archetype: str):
    driver.run_schedule(["--type", archetype, "--dry-run", "--date", "tomorrow"])


@when(parsers.parse('the workout schedule script is executed for "{archetype}" with duration "{duration}"'))
def step_run_archetype_duration(driver: WorkoutScheduleDriver, archetype: str, duration: str):
    driver.run_schedule(
        ["--type", archetype, "--duration", duration, "--date", "tomorrow"],
        use_mock_api=True,
    )


@when(parsers.parse('the workout schedule script is executed for "{archetype}" with {reps:d} reps of "{work}" in "{target}"'))
def step_run_intervals_custom(driver: WorkoutScheduleDriver, archetype: str, reps: int, work: str, target: str):
    driver.run_schedule(
        ["--type", archetype, "--reps", str(reps), "--work", work, "--target", target, "--date", "tomorrow"],
        use_mock_api=True,
    )


@when(parsers.parse('the workout schedule script is executed with raw text "{raw_text}"'))
def step_run_raw_text(driver: WorkoutScheduleDriver, raw_text: str):
    # Unescape literal \n if present
    formatted_text = raw_text.replace("\\n", "\n")
    driver.run_schedule(
        ["--raw-text", formatted_text, "--date", "tomorrow"],
        use_mock_api=True,
    )


@then("the process exits with an error")
def step_check_error_exit(driver: WorkoutScheduleDriver):
    assert driver.process_result is not None
    assert driver.process_result.returncode != 0


@then(parsers.parse('the process exits with code {expected_code:d}'))
def step_check_exit_code(driver: WorkoutScheduleDriver, expected_code: int):
    assert driver.process_result is not None
    assert driver.process_result.returncode == expected_code, f"Stderr: {driver.process_result.stderr}"


@then(parsers.parse('the error output mentions "{keyword}"'))
def step_check_stderr_mentions(driver: WorkoutScheduleDriver, keyword: str):
    assert driver.process_result is not None
    output = driver.process_result.stdout + driver.process_result.stderr
    assert keyword in output


@then(parsers.parse('the stdout contains "{keyword}"'))
def step_check_stdout_contains(driver: WorkoutScheduleDriver, keyword: str):
    assert driver.process_result is not None
    assert keyword in driver.process_result.stdout


@then(parsers.parse('the mock server received a POST request to "{expected_path}"'))
def step_check_mock_path(expected_path: str):
    assert len(MockIntervalsEventHandler.received_requests) > 0
    last_req = MockIntervalsEventHandler.received_requests[-1]
    assert last_req["path"] == expected_path


@then(parsers.parse('the received event has type "{expected_type}" and category "{expected_category}"'))
def step_check_event_type_cat(driver: WorkoutScheduleDriver, expected_type: str, expected_category: str):
    payload = driver.get_last_received_payload()
    assert payload.get("type") == expected_type
    assert payload.get("category") == expected_category


@then(parsers.parse('the received event description contains "{snippet}"'))
def step_check_event_snippet(driver: WorkoutScheduleDriver, snippet: str):
    payload = driver.get_last_received_payload()
    description = payload.get("description", "")
    assert snippet in description, f"Expected '{snippet}' in description:\n{description}"


@when(parsers.parse('the workout schedule script is executed with extra argument "{arg}"'))
def step_run_with_arg(driver: WorkoutScheduleDriver, arg: str):
    driver.run_schedule([arg], use_mock_api=True)


@then(parsers.parse('the mock server received at least {min_count:d} POST requests to "{expected_path}"'))
def step_check_min_post_requests(min_count: int, expected_path: str):
    matching = [
        r for r in MockIntervalsEventHandler.received_requests
        if r["path"] == expected_path
    ]
    assert len(matching) >= min_count, f"Expected >= {min_count} POST requests to {expected_path}, got {len(matching)}"


@then(parsers.parse('one scheduled event name contains "{snippet}"'))
def step_check_event_name_contains(snippet: str):
    names = [
        r["payload"].get("name", "")
        for r in MockIntervalsEventHandler.received_requests
        if "payload" in r
    ]
    assert any(snippet in n for n in names), f"Expected '{snippet}' in one of names: {names}"


@when("the workout schedule script is executed with adaptive planning arguments")
def step_run_adaptive(driver: WorkoutScheduleDriver):
    driver.run_adaptive_plan()


@given("a mock uncompleted workout exists for yesterday")
def step_mock_uncompleted_yesterday(driver: WorkoutScheduleDriver):
    driver.setup_mock_uncompleted_yesterday()


@given("no run occurred yesterday")
def step_no_run_yesterday(driver: WorkoutScheduleDriver):
    driver.setup_activities_no_run_yesterday()


@given("an activity was recorded for yesterday")
def step_activity_yesterday(driver: WorkoutScheduleDriver):
    driver.setup_activities_run_yesterday()


@then("the mock server received a DELETE request for the uncompleted workout")
def step_check_delete_request(driver: WorkoutScheduleDriver):
    delete_reqs = [r for r in MockIntervalsEventHandler.received_requests if r.get("method") == "DELETE"]
    assert len(delete_reqs) > 0, f"No DELETE requests received. All reqs: {MockIntervalsEventHandler.received_requests}"
    assert any("998877" in r["path"] for r in delete_reqs)


@then("one scheduled event has date for today")
def step_check_scheduled_today(driver: WorkoutScheduleDriver):
    today_iso = driver.test_ref_date.isoformat()
    scheduled = [
        r["payload"] for r in MockIntervalsEventHandler.received_requests
        if r.get("method") == "POST" and str(r.get("payload", {}).get("start_date_local", "")).startswith(today_iso)
    ]
    assert len(scheduled) > 0, f"Expected scheduled event for today ({today_iso}). Received: {MockIntervalsEventHandler.received_requests}"


@then("one scheduled event has date for tomorrow")
def step_check_scheduled_tomorrow(driver: WorkoutScheduleDriver):
    tomorrow_iso = (driver.test_ref_date + datetime.timedelta(days=1)).isoformat()
    scheduled = [
        r["payload"] for r in MockIntervalsEventHandler.received_requests
        if r.get("method") == "POST" and str(r.get("payload", {}).get("start_date_local", "")).startswith(tomorrow_iso)
    ]
    assert len(scheduled) > 0, f"Expected scheduled event for tomorrow ({tomorrow_iso}). Received: {MockIntervalsEventHandler.received_requests}"


@given("a rest day note exists in calendar for today")
def step_rest_day_note_today(driver: WorkoutScheduleDriver):
    today_iso = driver.test_ref_date.isoformat()
    MockIntervalsEventHandler.events_db.append({
        "id": 112233,
        "category": "NOTE",
        "name": "🛋️ Ruhetag (Coach)",
        "start_date_local": f"{today_iso}T07:00:00",
    })


@then("exactly 1 workout event is scheduled for today")
def step_exactly_one_workout_today(driver: WorkoutScheduleDriver):
    today_iso = driver.test_ref_date.isoformat()
    scheduled = [
        r["payload"] for r in MockIntervalsEventHandler.received_requests
        if r.get("method") == "POST"
        and r.get("payload", {}).get("category") == "WORKOUT"
        and str(r.get("payload", {}).get("start_date_local", "")).startswith(today_iso)
    ]
    assert len(scheduled) == 1, f"Expected exactly 1 workout scheduled for today, found {len(scheduled)}"


@then("no workout event is scheduled for today")
def step_no_workout_today(driver: WorkoutScheduleDriver):
    today_iso = driver.test_ref_date.isoformat()
    scheduled = [
        r["payload"] for r in MockIntervalsEventHandler.received_requests
        if r.get("method") == "POST"
        and r.get("payload", {}).get("category") == "WORKOUT"
        and str(r.get("payload", {}).get("start_date_local", "")).startswith(today_iso)
    ]
    assert len(scheduled) == 0, f"Expected no workout scheduled for today, found {len(scheduled)}"


@given("a paired completed workout exists in calendar for today")
def step_paired_workout_today(driver: WorkoutScheduleDriver):
    today_iso = driver.test_ref_date.isoformat()
    MockIntervalsEventHandler.events_db.append({
        "id": 149850038,
        "category": "WORKOUT",
        "name": "Kontrollierter Schwellenlauf (3x 8m Z4)",
        "start_date_local": f"{today_iso}T07:00:00",
        "paired_activity_id": "i203449631",
    })


@when("the workout schedule script is executed with adaptive planning arguments and events-file")
def step_run_adaptive_with_events_file(driver: WorkoutScheduleDriver):
    driver.run_adaptive_plan(with_events_file=True)


@then("the calendar events file contains the today paired workout")
def step_events_file_has_paired_workout(driver: WorkoutScheduleDriver):
    assert driver.events_file.is_file()
    with open(driver.events_file, "r", encoding="utf-8") as f:
        data = json.load(f)
    assert any(e.get("paired_activity_id") == "i203449631" for e in data)


