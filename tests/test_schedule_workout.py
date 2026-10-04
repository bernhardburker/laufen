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

        MockIntervalsEventHandler.received_requests.append({
            "path": self.path,
            "headers": dict(self.headers),
            "payload": parsed_body,
        })

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        response_data = {"id": "mock_event_12345", "status": "SUCCESS"}
        self.wfile.write(json.dumps(response_data).encode("utf-8"))

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
        MockIntervalsEventHandler.received_requests.clear()

    def unset_api_key(self) -> None:
        self.env.pop("INTERVALS_API_KEY", None)

    def set_api_key(self, key: str = "mock_secret_key") -> None:
        self.env["INTERVALS_API_KEY"] = key

    def start_mock_api(self) -> None:
        MockIntervalsEventHandler.received_requests.clear()
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
