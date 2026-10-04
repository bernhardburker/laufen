import http.server
import json
import os
import subprocess
import sys
import threading
from pathlib import Path

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Scenarios aus der Feature-Datei binden
scenarios("download_intervals.feature")


# 2. Mock HTTP Server für Intervals.icu API
class MockIntervalsHandler(http.server.BaseHTTPRequestHandler):
    activities_data = [
        {
            "id": "i1001",
            "name": "Morgenlauf Z2",
            "type": "Run",
            "start_date_local": "2026-10-02T07:00:00",
            "distance": 8000.0,
            "moving_time": 2880,
            "elapsed_time": 2900,
            "average_speed": 2.778,  # ~6:00 min/km
            "average_heartrate": 142,
            "max_heartrate": 155,
        },
        {
            "id": "i1002",
            "name": "Intervalltraining 4x800",
            "type": "Run",
            "start_date_local": "2026-10-01T18:00:00",
            "distance": 6500.0,
            "moving_time": 2100,
            "elapsed_time": 2200,
            "average_speed": 3.095,  # ~5:23 min/km
            "average_heartrate": 162,
            "max_heartrate": 178,
        },
    ]

    def do_GET(self):
        auth_header = self.headers.get("Authorization", "")
        if not auth_header.startswith("Basic "):
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error": "Unauthorized"}')
            return

        if "/activities" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(self.activities_data).encode("utf-8"))
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, format, *args):
        # Stille im Testlauf
        pass


# 3. Test Driver: Kapselt Prozessausführung und Assertions
class IntervalsDownloadDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.env: dict[str, str] = os.environ.copy()
        self.process_result: subprocess.CompletedProcess | None = None
        self.mock_server: http.server.HTTPServer | None = None
        self.mock_server_thread: threading.Thread | None = None
        self.mock_server_port: int | None = None
        self.output_file = tmp_path / "intervals_activities.json"
        self.env["DOTENV_PATH"] = str(tmp_path / ".env")

    def unset_api_key(self) -> None:
        self.env.pop("INTERVALS_API_KEY", None)

    def set_api_key(self, key: str = "mock_secret_key") -> None:
        self.env["INTERVALS_API_KEY"] = key

    def start_mock_api(self) -> None:
        self.mock_server = http.server.HTTPServer(("127.0.0.1", 0), MockIntervalsHandler)
        self.mock_server_port = self.mock_server.server_address[1]
        self.mock_server_thread = threading.Thread(target=self.mock_server.serve_forever, daemon=True)
        self.mock_server_thread.start()

    def stop_mock_api(self) -> None:
        if self.mock_server:
            self.mock_server.shutdown()
            self.mock_server.server_close()

    def run_download(self, use_mock_api: bool = False) -> None:
        cmd = [
            sys.executable,
            str(self.workspace_root / "src" / "integrations" / "download_intervals.py"),
            "--output",
            str(self.output_file),
        ]
        if use_mock_api and self.mock_server_port:
            cmd.extend(["--api-base", f"http://127.0.0.1:{self.mock_server_port}"])

        self.process_result = subprocess.run(
            cmd,
            cwd=self.workspace_root,
            env=self.env,
            capture_output=True,
            text=True,
        )

    def assert_exit_code(self, expected_code: int) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode == expected_code, (
            f"Expected exit code {expected_code}, got {self.process_result.returncode}.\n"
            f"STDOUT:\n{self.process_result.stdout}\n"
            f"STDERR:\n{self.process_result.stderr}"
        )

    def assert_exit_code_non_zero(self) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode != 0, (
            f"Expected non-zero exit code, got 0.\nSTDOUT:\n{self.process_result.stdout}"
        )

    def assert_output_contains(self, text: str) -> None:
        assert self.process_result is not None
        combined = (self.process_result.stdout or "") + (self.process_result.stderr or "")
        assert text in combined, f"Expected '{text}' in output:\n{combined}"

    def assert_saved_activities_count(self, expected_count: int) -> None:
        assert self.output_file.exists(), f"Output file does not exist: {self.output_file}"
        with open(self.output_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        assert len(data) == expected_count, f"Expected {expected_count} activities, got {len(data)}"


# 4. Pytest Fixture
@pytest.fixture
def driver(tmp_path: Path) -> IntervalsDownloadDriver:
    workspace_root = Path(__file__).resolve().parent.parent
    drv = IntervalsDownloadDriver(workspace_root, tmp_path)
    yield drv
    drv.stop_mock_api()


# 5. Step Definitions
@given("INTERVALS_API_KEY is not configured")
def given_no_api_key(driver: IntervalsDownloadDriver):
    driver.unset_api_key()


@given("INTERVALS_API_KEY is configured")
def given_api_key_configured(driver: IntervalsDownloadDriver):
    driver.set_api_key("test_api_key")


@given("a mock Intervals.icu API server is running with 2 activities")
def given_mock_server_running(driver: IntervalsDownloadDriver):
    driver.start_mock_api()


@when("the download script is executed")
def when_script_executed(driver: IntervalsDownloadDriver):
    driver.run_download(use_mock_api=False)


@when("the download script is executed against the mock API")
def when_script_executed_against_mock(driver: IntervalsDownloadDriver):
    driver.run_download(use_mock_api=True)


@then("the process exits with an error")
def then_process_exits_with_error(driver: IntervalsDownloadDriver):
    driver.assert_exit_code_non_zero()


@then(parsers.parse('the error output mentions "{keyword}"'))
def then_error_mentions_keyword(driver: IntervalsDownloadDriver, keyword: str):
    driver.assert_output_contains(keyword)


@then(parsers.parse("the process exits with code {code:d}"))
def then_process_exits_with_code(driver: IntervalsDownloadDriver, code: int):
    driver.assert_exit_code(code)


@then(parsers.parse("the output JSON file contains {count:d} activities"))
def then_json_contains_activities(driver: IntervalsDownloadDriver, count: int):
    driver.assert_saved_activities_count(count)


@then("the stdout report displays the activities summary table")
def then_stdout_displays_summary_table(driver: IntervalsDownloadDriver):
    driver.assert_output_contains("Übersicht der letzten")
    driver.assert_output_contains("Ø Pace")
