import http.server
import json
import os
import subprocess
import sys
import threading
from pathlib import Path

import pytest
from pytest_bdd import given, scenarios, then, when

scenarios("download_events.feature")


class MockEventsHandler(http.server.BaseHTTPRequestHandler):
    events_data = [
        {
            "id": 101,
            "start_date_local": "2026-10-30T07:00:00",
            "category": "WORKOUT",
            "name": "Kontrollierter Schwellenlauf (3x 8m Z4)",
            "paired_activity_id": "i203449631",
        },
        {
            "id": 102,
            "start_date_local": "2026-10-31T08:00:00",
            "category": "WORKOUT",
            "name": "Aerober Anker & Grundlagenlauf (45m Z2)",
            "paired_activity_id": None,
        },
        {
            "id": 103,
            "start_date_local": "2026-10-29T00:00:00",
            "category": "NOTE",
            "name": "🛋️ Ruhetag (Coach)",
            "paired_activity_id": None,
        },
    ]

    def do_GET(self):
        auth_header = self.headers.get("Authorization", "")
        if not auth_header.startswith("Basic "):
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error": "Unauthorized"}')
            return

        if "/events" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(self.events_data).encode("utf-8"))
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, format, *args):
        pass


class EventsDownloadDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.env: dict[str, str] = os.environ.copy()
        self.process_result: subprocess.CompletedProcess | None = None
        self.mock_server: http.server.HTTPServer | None = None
        self.mock_server_thread: threading.Thread | None = None
        self.mock_server_port: int | None = None
        self.output_file = tmp_path / "calendar_events.json"
        self.env["DOTENV_PATH"] = str(tmp_path / ".env")

    def unset_api_key(self) -> None:
        self.env.pop("INTERVALS_API_KEY", None)

    def set_api_key(self, key: str = "dummy_key") -> None:
        self.env["INTERVALS_API_KEY"] = key

    def start_mock_server(self) -> None:
        self.mock_server = http.server.HTTPServer(("127.0.0.1", 0), MockEventsHandler)
        self.mock_server_port = self.mock_server.server_port
        self.mock_server_thread = threading.Thread(target=self.mock_server.serve_forever, daemon=True)
        self.mock_server_thread.start()

    def stop_mock_server(self) -> None:
        if self.mock_server:
            self.mock_server.shutdown()
            self.mock_server.server_close()

    def run_download_script(self, use_mock_api: bool = False) -> subprocess.CompletedProcess:
        cmd = [
            sys.executable,
            str(self.workspace_root / "src" / "integrations" / "download_events.py"),
            "--output",
            str(self.output_file),
        ]
        if use_mock_api and self.mock_server_port:
            cmd.extend(["--api-base", f"http://127.0.0.1:{self.mock_server_port}/api/v1"])

        self.process_result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            env=self.env,
            cwd=str(self.workspace_root),
        )
        return self.process_result


@pytest.fixture
def driver(tmp_path: Path) -> EventsDownloadDriver:
    workspace = Path(__file__).resolve().parent.parent
    d = EventsDownloadDriver(workspace_root=workspace, tmp_path=tmp_path)
    yield d
    d.stop_mock_server()


@given("INTERVALS_API_KEY is not configured for events")
def given_no_api_key(driver: EventsDownloadDriver):
    driver.unset_api_key()


@given("INTERVALS_API_KEY is configured for events")
def given_api_key(driver: EventsDownloadDriver):
    driver.set_api_key("valid_test_key")


@given("a mock Intervals.icu event API server is running with 3 calendar events")
def given_mock_server(driver: EventsDownloadDriver):
    driver.start_mock_server()


@when("the download events script is executed")
def when_run_no_mock(driver: EventsDownloadDriver):
    driver.run_download_script(use_mock_api=False)


@when("the download events script is executed against the mock API")
def when_run_mock(driver: EventsDownloadDriver):
    driver.run_download_script(use_mock_api=True)


@then("the process exits with an error")
def then_exit_error(driver: EventsDownloadDriver):
    assert driver.process_result is not None
    assert driver.process_result.returncode != 0


@then('the error output mentions "INTERVALS_API_KEY"')
def then_error_mentions_key(driver: EventsDownloadDriver):
    assert driver.process_result is not None
    err = driver.process_result.stderr + driver.process_result.stdout
    assert "INTERVALS_API_KEY" in err


@then("the process exits with code 0")
def then_exit_zero(driver: EventsDownloadDriver):
    assert driver.process_result is not None
    assert driver.process_result.returncode == 0, f"Error: {driver.process_result.stderr}"


@then("the output JSON file contains 3 calendar events")
def then_output_contains_events(driver: EventsDownloadDriver):
    assert driver.output_file.is_file()
    with open(driver.output_file, "r", encoding="utf-8") as f:
        data = json.load(f)
    assert len(data) == 3


@then("the stdout report displays the calendar events summary")
def then_stdout_summary(driver: EventsDownloadDriver):
    assert driver.process_result is not None
    out = driver.process_result.stdout
    assert "Intervals.icu Kalender-Events Synchronisation" in out
    assert "Gesamtanzahl Events: 3" in out
    assert "Workouts geplant:" in out
    assert "Workouts absolviert:" in out
