import http.server
import json
import os
import subprocess
import sys
import threading
from pathlib import Path

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Bind feature scenarios
scenarios("download_wellness.feature")


# 2. Mock HTTP Server for Intervals.icu Wellness API
class MockWellnessHandler(http.server.BaseHTTPRequestHandler):
    wellness_data = [
        {
            "id": "2026-10-13",
            "date": "2026-10-13",
            "sleepSecs": 27900,  # 7h 45m
            "sleepScore": 82,
            "restingHR": 49,
            "hrv": 62.5,
            "bodyBattery": 85,
            "comments": "Felt well rested",
        },
        {
            "id": "2026-10-14",
            "date": "2026-10-14",
            "sleepSecs": 28800,  # 8h 00m
            "sleepScore": 88,
            "restingHR": 47,
            "hrv": 65.0,
            "bodyBattery": 90,
            "comments": "Good recovery",
        },
    ]

    def do_GET(self):
        auth_header = self.headers.get("Authorization", "")
        if not auth_header.startswith("Basic "):
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error": "Unauthorized"}')
            return

        if "/wellness" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(self.wellness_data).encode("utf-8"))
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, format, *args):
        pass


# 3. Test Driver
class WellnessDownloadDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.env: dict[str, str] = os.environ.copy()
        self.process_result: subprocess.CompletedProcess | None = None
        self.mock_server: http.server.HTTPServer | None = None
        self.mock_server_thread: threading.Thread | None = None
        self.mock_server_port: int | None = None
        self.output_file = tmp_path / "intervals_wellness.json"
        self.env["DOTENV_PATH"] = str(tmp_path / ".env")

    def unset_api_key(self) -> None:
        self.env.pop("INTERVALS_API_KEY", None)

    def set_api_key(self, key: str = "mock_secret_key") -> None:
        self.env["INTERVALS_API_KEY"] = key

    def start_mock_api(self) -> None:
        self.mock_server = http.server.HTTPServer(("127.0.0.1", 0), MockWellnessHandler)
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
            str(self.workspace_root / "src" / "integrations" / "download_wellness.py"),
            "--output",
            str(self.output_file),
        ]
        if use_mock_api and self.mock_server_port:
            cmd.extend(["--api-base", f"http://127.0.0.1:{self.mock_server_port}/api/v1"])

        self.process_result = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=self.env,
            cwd=str(self.workspace_root),
        )

    def assert_exit_code(self, expected_code: int) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode == expected_code, (
            f"Expected exit code {expected_code}, got {self.process_result.returncode}.\n"
            f"Stdout: {self.process_result.stdout}\nStderr: {self.process_result.stderr}"
        )

    def assert_error_contains(self, text: str) -> None:
        assert self.process_result is not None
        combined = (self.process_result.stdout + self.process_result.stderr).lower()
        assert text.lower() in combined, (
            f"Expected '{text}' in output, but got:\n"
            f"Stdout: {self.process_result.stdout}\nStderr: {self.process_result.stderr}"
        )

    def assert_json_count(self, expected_count: int) -> None:
        assert self.output_file.exists(), f"File {self.output_file} was not created."
        with open(self.output_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        assert isinstance(data, list)
        assert len(data) == expected_count, f"Expected {expected_count} records, found {len(data)}."

    def assert_stdout_contains(self, text: str) -> None:
        assert self.process_result is not None
        assert text.lower() in self.process_result.stdout.lower()


# 4. Pytest Fixture
@pytest.fixture
def driver(tmp_path: Path):
    workspace = Path(__file__).resolve().parent.parent
    d = WellnessDownloadDriver(workspace, tmp_path)
    yield d
    d.stop_mock_api()


# 5. Step Definitions
@given("INTERVALS_API_KEY is not configured")
def step_unset_api_key(driver: WellnessDownloadDriver):
    driver.unset_api_key()


@given("INTERVALS_API_KEY is configured")
def step_set_api_key(driver: WellnessDownloadDriver):
    driver.set_api_key("valid_test_api_key")


@given("a mock Intervals.icu API server is running with 2 wellness records")
def step_start_mock_api(driver: WellnessDownloadDriver):
    driver.start_mock_api()


@when("the wellness download script is executed")
def step_run_download_no_mock(driver: WellnessDownloadDriver):
    driver.run_download(use_mock_api=False)


@when("the wellness download script is executed against the mock API")
def step_run_download_with_mock(driver: WellnessDownloadDriver):
    driver.run_download(use_mock_api=True)


@then("the process exits with an error")
def step_verify_error_exit(driver: WellnessDownloadDriver):
    assert driver.process_result is not None
    assert driver.process_result.returncode != 0


@then(parsers.parse('the error output mentions "{expected_text}"'))
def step_verify_error_output(driver: WellnessDownloadDriver, expected_text: str):
    driver.assert_error_contains(expected_text)


@then(parsers.parse("the process exits with code {code:d}"))
def step_verify_exit_code(driver: WellnessDownloadDriver, code: int):
    driver.assert_exit_code(code)


@then(parsers.parse("the output JSON file contains {count:d} wellness records"))
def step_verify_json_count(driver: WellnessDownloadDriver, count: int):
    driver.assert_json_count(count)


@then("the stdout report displays the wellness and sleep summary table")
def step_verify_table(driver: WellnessDownloadDriver):
    driver.assert_stdout_contains("Intervals.icu Wellness & Sleep Summary")
    driver.assert_stdout_contains("7h 45m")
    driver.assert_stdout_contains("8h 00m")
