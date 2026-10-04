import json
import os
from pathlib import Path
import subprocess
import sys
from typing import List, Optional

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Bind feature file scenarios
scenarios("init_system.feature")


# 2. Test Driver
class InitSystemDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.test_workspace = tmp_path / "sandbox_workspace"
        self.test_workspace.mkdir(parents=True, exist_ok=True)
        self.process_result: Optional[subprocess.CompletedProcess] = None
        self.custom_python: Optional[str] = None

    def setup_existing_venv(self) -> None:
        venv_bin = self.test_workspace / ".venv" / "bin"
        venv_bin.mkdir(parents=True, exist_ok=True)

        current_python = sys.executable
        target_python = venv_bin / "python"
        if not target_python.exists():
            os.symlink(current_python, target_python)

        # Symlink pytest and pip if available in the running environment
        current_pytest = Path(sys.executable).parent / "pytest"
        target_pytest = venv_bin / "pytest"
        if current_pytest.exists() and not target_pytest.exists():
            os.symlink(current_pytest, target_pytest)
        elif not target_pytest.exists():
            target_pytest.write_text("#!/bin/sh\necho pytest stub\n")
            target_pytest.chmod(0o755)

        current_pip = Path(sys.executable).parent / "pip"
        target_pip = venv_bin / "pip"
        if current_pip.exists() and not target_pip.exists():
            os.symlink(current_pip, target_pip)
        elif not target_pip.exists():
            target_pip.write_text("#!/bin/sh\necho pip stub\n")
            target_pip.chmod(0o755)

    def setup_empty_workspace(self) -> None:
        venv_dir = self.test_workspace / ".venv"
        if venv_dir.exists():
            import shutil
            shutil.rmtree(venv_dir)

    def set_invalid_python(self, invalid_path: str = "/nonexistent/invalid_python_binary") -> None:
        self.custom_python = invalid_path

    def run_init(self, extra_args: Optional[List[str]] = None) -> None:
        cmd = [
            "bash",
            str(self.workspace_root / "init.sh"),
            "--workspace",
            str(self.test_workspace),
        ]
        if self.custom_python:
            cmd.extend(["--python", self.custom_python])
        if extra_args:
            cmd.extend(extra_args)

        self.process_result = subprocess.run(
            cmd,
            cwd=self.test_workspace,
            capture_output=True,
            text=True,
        )

    def run_sourced_verification(self) -> None:
        env_file = self.test_workspace / ".paths.env"
        assert env_file.exists(), f"Environment file not found: {env_file}"
        check_cmd = f"source '{env_file}' && which python && which pytest"
        result = subprocess.run(
            ["bash", "-c", check_cmd],
            cwd=self.test_workspace,
            capture_output=True,
            text=True,
        )
        assert result.returncode == 0, (
            f"Sourced verification failed (exit code {result.returncode}):\n"
            f"STDOUT: {result.stdout}\nSTDERR: {result.stderr}"
        )
        expected_venv_bin = str(self.test_workspace / ".venv" / "bin")
        assert expected_venv_bin in result.stdout, (
            f"Expected {expected_venv_bin} in which output:\n{result.stdout}"
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

    def assert_shell_paths_file_exists(self, exists: bool = True) -> None:
        target = self.test_workspace / ".paths.env"
        if exists:
            assert target.is_file(), f"Expected shell paths file at {target}"
        else:
            assert not target.exists(), f"Did not expect shell paths file at {target}"

    def assert_shell_paths_exports(self) -> None:
        target = self.test_workspace / ".paths.env"
        assert target.is_file()
        content = target.read_text(encoding="utf-8")
        assert "export WORKSPACE_ROOT=" in content
        assert "export PYTHON_EXEC=" in content
        assert "export PYTEST_EXEC=" in content
        assert "export PATH=" in content

    def assert_json_paths_file_exists(self, exists: bool = True) -> None:
        target = self.test_workspace / ".paths.json"
        if exists:
            assert target.is_file(), f"Expected JSON paths file at {target}"
        else:
            assert not target.exists(), f"Did not expect JSON paths file at {target}"

    def assert_json_paths_valid(self) -> None:
        target = self.test_workspace / ".paths.json"
        assert target.is_file()
        data = json.loads(target.read_text(encoding="utf-8"))
        assert "workspace_root" in data
        assert "python" in data and len(data["python"]) > 0
        assert "pytest" in data and len(data["pytest"]) > 0
        assert "virtual_env" in data

    def assert_venv_created(self) -> None:
        python_bin = self.test_workspace / ".venv" / "bin" / "python"
        assert python_bin.exists(), f"Virtual environment Python not found at {python_bin}"


# 3. Fixture injecting Driver
@pytest.fixture
def driver(tmp_path: Path) -> InitSystemDriver:
    workspace_root = Path(__file__).resolve().parent.parent
    return InitSystemDriver(workspace_root, tmp_path)


# 4. Step Definitions
@given("a workspace directory")
def given_workspace(driver: InitSystemDriver):
    pass  # Initialized in driver constructor


@given("a workspace directory without a virtual environment")
def given_workspace_without_venv(driver: InitSystemDriver):
    driver.setup_empty_workspace()


@given("an existing virtual environment with Python and Pytest")
def given_existing_venv(driver: InitSystemDriver):
    driver.setup_existing_venv()


@given("an invalid custom Python interpreter path")
def given_invalid_python(driver: InitSystemDriver):
    driver.set_invalid_python()


@when("the initialization script is executed")
def when_script_executed(driver: InitSystemDriver):
    driver.run_init()


@when("the initialization script is executed with check-only mode")
def when_script_executed_check_only(driver: InitSystemDriver):
    driver.run_init(extra_args=["--check-only"])


@when("the initialization script is executed with the invalid Python path")
def when_script_executed_with_invalid_python(driver: InitSystemDriver):
    driver.run_init()


@then(parsers.parse("the process exits with code {code:d}"))
def then_process_exits_with_code(driver: InitSystemDriver, code: int):
    driver.assert_exit_code(code)


@then("the process exits with an error")
def then_process_exits_with_error(driver: InitSystemDriver):
    driver.assert_exit_code_non_zero()


@then("the shell paths file is created")
def then_shell_paths_file_created(driver: InitSystemDriver):
    driver.assert_shell_paths_file_exists(True)


@then("the shell paths file is not created")
def then_shell_paths_file_not_created(driver: InitSystemDriver):
    driver.assert_shell_paths_file_exists(False)


@then("the shell paths file exports the Python and Pytest executables")
def then_shell_paths_exports(driver: InitSystemDriver):
    driver.assert_shell_paths_exports()


@then("the json paths file is created")
def then_json_paths_file_created(driver: InitSystemDriver):
    driver.assert_json_paths_file_exists(True)


@then("the json paths file is not created")
def then_json_paths_file_not_created(driver: InitSystemDriver):
    driver.assert_json_paths_file_exists(False)


@then("the json paths file contains the absolute paths to Python and Pytest")
def then_json_paths_valid(driver: InitSystemDriver):
    driver.assert_json_paths_valid()


@then("a new virtual environment is created")
def then_venv_created(driver: InitSystemDriver):
    driver.assert_venv_created()


@then("the output displays the system inspection summary")
def then_summary_displayed(driver: InitSystemDriver):
    driver.assert_output_contains("System & Environment Initialization")
    driver.assert_output_contains("Check-only mode")


@then(parsers.parse('the error output mentions "{keyword}"'))
def then_error_mentions_keyword(driver: InitSystemDriver, keyword: str):
    driver.assert_output_contains(keyword)


@then("sourcing the shell paths file in bash makes Python and Pytest accessible in PATH")
def then_sourced_accessible(driver: InitSystemDriver):
    driver.run_sourced_verification()
