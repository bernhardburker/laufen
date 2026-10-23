#!/usr/bin/env python3
"""BDD Step definitions and test driver for LLM Client Abstraction Layer."""

import json
import os
from pathlib import Path
import stat
import sys
from typing import Any, Dict, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

from src.llm import (
    AgyLLMClient,
    GeminiAPIClient,
    LLMClient,
    LLMResult,
    MockLLMClient,
    get_llm_client,
)

# 1. Bind feature scenarios
scenarios("llm_client.feature")


# 2. Test Driver
class LLMClientDriver:
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.client: Optional[LLMClient] = None
        self.result: Optional[LLMResult] = None
        self.expected_response_text: str = ""
        self.mock_script: Optional[Path] = None
        self.artifact_file: Optional[Path] = None
        self.artifact_content: str = ""

    def setup_mock_client(self, response_text: str) -> None:
        self.expected_response_text = response_text
        self.client = MockLLMClient(responses=response_text)

    def setup_failing_mock_client(self, error_message: str) -> None:
        self.client = MockLLMClient(should_fail=True, error_message=error_message)

    def setup_timeout_mock_client(self) -> None:
        self.client = MockLLMClient(simulate_timeout=True)

    def create_mock_agy_script(self, output_type: str = "json") -> Path:
        script_path = self.tmp_path / "mock_agy.sh"
        if output_type == "json":
            content = """#!/usr/bin/env bash
cat << 'EOF'
{
  "status_title": "Solider Grundlagenaufbau",
  "status_badge": "Basis stabil",
  "status_level": "info",
  "coach_commentary": "Gutes Training diese Woche.",
  "insights": [],
  "recommendations": []
}
EOF
"""
        elif output_type == "artifact":
            self.artifact_file = self.tmp_path / "generated_coach_brief.md"
            self.artifact_content = "### Wochenanalyse\nSolide Ausdauerleistung in Zone 2."
            self.artifact_file.write_text(self.artifact_content, encoding="utf-8")
            content = f"""#!/usr/bin/env bash
echo "Artifact generated at file://{self.artifact_file}"
"""
        else:
            content = """#!/usr/bin/env bash
echo "Unknown output"
"""
        script_path.write_text(content, encoding="utf-8")
        script_path.chmod(script_path.stat().st_mode | stat.S_IEXEC)
        self.mock_script = script_path
        return script_path

    def setup_agy_client(self, binary_path: Optional[Path] = None) -> None:
        bin_path = binary_path or self.mock_script
        self.client = AgyLLMClient(agy_bin=bin_path, timeout="10s")

    def invoke_client(self, prompt: str) -> None:
        assert self.client is not None, "Client must be initialized"
        self.result = self.client.generate(prompt=prompt)

    def ask_factory_for_provider(self, provider_name: str) -> None:
        self.client = get_llm_client(provider=provider_name)

    def assert_success(self, expected_success: bool) -> None:
        assert self.result is not None
        assert self.result.success == expected_success, (
            f"Expected success={expected_success}, got {self.result.success}. Error: {self.result.error}"
        )

    def assert_text_matches(self, expected_text: str) -> None:
        assert self.result is not None
        assert self.result.text.strip() == expected_text.strip()

    def assert_mock_recorded_prompt(self, prompt_substring: str) -> None:
        assert isinstance(self.client, MockLLMClient)
        assert len(self.client.calls) >= 1
        recorded = self.client.last_prompt or ""
        assert prompt_substring in recorded

    def assert_error_contains(self, text: str) -> None:
        assert self.result is not None
        err = self.result.error or ""
        assert text.lower() in err.lower(), f"Expected '{text}' in error message: '{err}'"

    def assert_response_is_valid_json(self) -> None:
        assert self.result is not None
        try:
            data = json.loads(self.result.text)
            assert isinstance(data, dict)
            assert "status_title" in data
        except Exception as e:
            pytest.fail(f"Result text was not valid JSON: {self.result.text}. Error: {e}")

    def assert_response_matches_artifact(self) -> None:
        assert self.result is not None
        assert self.result.text.strip() == self.artifact_content.strip()

    def assert_client_type(self, expected_type_name: str) -> None:
        assert self.client is not None
        assert self.client.__class__.__name__ == expected_type_name


# 3. Fixture
@pytest.fixture
def driver(tmp_path: Path) -> LLMClientDriver:
    return LLMClientDriver(tmp_path)


# 4. Step Definitions
@given("a mock LLM client configured with a structured response")
def given_mock_client_structured(driver: LLMClientDriver):
    driver.setup_mock_client('{"status": "ok", "message": "all good"}')


@given(parsers.parse('a mock LLM client configured to fail with error "{error_message}"'))
def given_mock_client_failing(driver: LLMClientDriver, error_message: str):
    driver.setup_failing_mock_client(error_message)


@given("a mock LLM client configured to simulate timeout")
def given_mock_client_timeout(driver: LLMClientDriver):
    driver.setup_timeout_mock_client()


@given("a mock agy executable that echoes structured json")
def given_mock_agy_json(driver: LLMClientDriver):
    driver.create_mock_agy_script(output_type="json")


@given("a mock agy executable that outputs a file artifact link")
def given_mock_agy_artifact(driver: LLMClientDriver):
    driver.create_mock_agy_script(output_type="artifact")


@given("an agy LLM client pointing to the mock executable")
def given_agy_client_mock_exe(driver: LLMClientDriver):
    driver.setup_agy_client()


@given(parsers.parse('an agy LLM client pointing to a non-existent binary "{bin_path}"'))
def given_agy_client_non_existent(driver: LLMClientDriver, bin_path: str):
    driver.setup_agy_client(binary_path=Path(bin_path))


@when("the mock LLM client is invoked with a coaching prompt")
def when_mock_client_invoked(driver: LLMClientDriver):
    driver.invoke_client("Analyze recent run: 7km in Zone 2")


@when("the agy client generates a response for a prompt")
def when_agy_client_generates(driver: LLMClientDriver):
    driver.invoke_client("Generate weekly coach review")


@when(parsers.parse('the LLM client factory is asked for provider "{provider_name}"'))
def when_factory_asked_provider(driver: LLMClientDriver, provider_name: str):
    driver.ask_factory_for_provider(provider_name)


@then(parsers.parse("the generation succeeds with status {expected_status}"))
def then_generation_succeeds(driver: LLMClientDriver, expected_status: str):
    driver.assert_success(expected_status.lower() == "true")


@then(parsers.parse("the generation fails with status {expected_status}"))
def then_generation_fails(driver: LLMClientDriver, expected_status: str):
    driver.assert_success(expected_status.lower() == "true")


@then("the generated text matches the configured response")
def then_text_matches(driver: LLMClientDriver):
    driver.assert_text_matches(driver.expected_response_text)


@then("the mock client recorded the prompt")
def then_mock_recorded_prompt(driver: LLMClientDriver):
    driver.assert_mock_recorded_prompt("Zone 2")


@then(parsers.parse('the error message contains "{text}"'))
def then_error_contains(driver: LLMClientDriver, text: str):
    driver.assert_error_contains(text)


@then("the response contains valid json")
def then_response_is_valid_json(driver: LLMClientDriver):
    driver.assert_response_is_valid_json()


@then("the response text matches the file artifact content")
def then_response_matches_artifact(driver: LLMClientDriver):
    driver.assert_response_matches_artifact()


@then(parsers.re(r"the resulting client is an? (?P<type_name>\w+)"))
def then_resulting_client_type(driver: LLMClientDriver, type_name: str):
    driver.assert_client_type(type_name)
