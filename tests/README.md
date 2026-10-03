# Test Suite Architecture & BDD Driver Pattern

This directory contains the automated test suite for all scripts, data pipelines, and automatisms. It uses **Behavior-Driven Development (BDD)** with **Gherkin syntax** combined with the **Test Driver Pattern** in a **flat directory layout**.

---

## Directory Layout (Flat & Pragmatic)

Tests and scenarios live directly in `tests/` without deep nesting:

```text
tests/
├── sync_workouts.feature       # Pure Gherkin scenarios (declarative business logic)
├── test_sync_workouts.py       # Step definitions + local Driver class
├── drivers.py                  # Optional: Shared Driver classes across multiple tests
├── conftest.py                 # Optional: Pytest fixtures & driver injection
└── README.md                   # Architecture documentation & reference examples
```

---

## The Driver Pattern Architecture

```text
[ Feature File (sync_workouts.feature) ]
                     │
                     ▼
[ Step Definitions in test_sync_workouts.py ]
                     │  (Calls Driver methods only; no raw subprocess/IO)
                     ▼
[ Driver Class (in test file or drivers.py) ]
                     │  (Handles CLI execution, subprocess.run, mocks, exit codes)
                     ▼
[ Target Automatism (e.g. src/planner/sync.py) ]
```

### Separation of Concerns:
1. **Feature File (`.feature`)**: Pure declarative Gherkin (`Given`, `When`, `Then`). Human-readable, 0% code.
2. **Step Definitions (`test_*.py`)**: Glue layer. Binds Gherkin phrases directly to Driver methods. Never contains raw `subprocess.run`, file I/O, or API calls.
3. **Driver Class (`test_*.py` or `drivers.py`)**: The machine room. Launches scripts, manages environment/fixtures, inspects stdout/stderr, and asserts exit codes.

---

## Reference Implementations

### 1. Feature Specification (`tests/sync_workouts.feature`)
```gherkin
Feature: Sync workouts to athlete calendar
  Scenario: Successfully upload planned workouts
    Given athlete configuration is available
    When the workout sync runs for date "2026-10-05"
    Then the process exits with code 0
    And the sync report contains "1 workout uploaded"
```

---

### 2. Python Reference (`tests/test_sync_workouts.py` using `pytest-bdd`)

In Python, the Driver and the Step Definitions can live in the **same test file** (or share a driver via `tests/drivers.py`):

```python
import json
import subprocess
from pathlib import Path
import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Bind the Gherkin feature file
scenarios("sync_workouts.feature")


# 2. Driver Class: Encapsulates CLI execution and assertions
class SyncDriver:

  def __init__(self, workspace_root: Path):
    self.workspace_root = workspace_root
    self.process_result: subprocess.CompletedProcess | None = None

  def set_athlete_config(self, athlete_data: dict) -> None:
    config_path = self.workspace_root / "config" / "athlete.json"
    config_path.parent.mkdir(parents=True, exist_ok=True)
    config_path.write_text(json.dumps(athlete_data))

  def run_sync(self, target_date: str) -> None:
    cmd = ["python", "-m", "src.planner.sync", "--date", target_date]
    self.process_result = subprocess.run(
        cmd, cwd=self.workspace_root, capture_output=True, text=True
    )

  def assert_exit_code(self, expected_code: int) -> None:
    assert self.process_result is not None
    assert self.process_result.returncode == expected_code, (
        f"Expected exit code {expected_code}, got"
        f" {self.process_result.returncode}. Stderr: {self.process_result.stderr}"
    )

  def assert_stdout_contains(self, text: str) -> None:
    assert self.process_result is not None
    assert (
        text in self.process_result.stdout
    ), f"Expected '{text}' in stdout: {self.process_result.stdout}"


# 3. Fixture injecting the Driver
@pytest.fixture
def sync_driver(tmp_path: Path) -> SyncDriver:
  return SyncDriver(tmp_path)


# 4. Step Definitions: Thin delegation to the Driver
@given("athlete configuration is available")
def given_athlete_config(sync_driver: SyncDriver):
  sync_driver.set_athlete_config({"athlete_id": "test_123"})


@when(parsers.parse('the workout sync runs for date "{target_date}"'))
def when_sync_runs(sync_driver: SyncDriver, target_date: str):
  sync_driver.run_sync(target_date)


@then(parsers.parse("the process exits with code {exit_code:d}"))
def then_exit_code(sync_driver: SyncDriver, exit_code: int):
  sync_driver.assert_exit_code(exit_code)


@then(parsers.parse('the sync report contains "{text}"'))
def then_report_contains(sync_driver: SyncDriver, text: str):
  sync_driver.assert_stdout_contains(text)
```

---

### 3. TypeScript Reference (`tests/sync_workouts.test.ts`)

```typescript
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import * as assert from 'node:assert';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { Given, When, Then } from '@cucumber/cucumber';

const execFileAsync = promisify(execFile);

// 1. Driver Class
export class SyncDriver {
  private exitCode: number = 0;
  private stdout: string = '';
  private stderr: string = '';

  constructor(private readonly workspaceRoot: string) {}

  async setAthleteConfig(data: object): Promise<void> {
    const filePath = path.join(this.workspaceRoot, 'config', 'athlete.json');
    await fs.mkdir(path.dirname(filePath), { recursive: true });
    await fs.writeFile(filePath, JSON.stringify(data));
  }

  async runSync(date: string): Promise<void> {
    try {
      const res = await execFileAsync('node', ['dist/sync.js', '--date', date], {
        cwd: this.workspaceRoot,
      });
      this.stdout = res.stdout;
      this.stderr = res.stderr;
      this.exitCode = 0;
    } catch (err: any) {
      this.exitCode = err.code ?? 1;
      this.stdout = err.stdout ?? '';
      this.stderr = err.stderr ?? '';
    }
  }

  assertExitCode(expected: number): void {
    assert.strictEqual(this.exitCode, expected, `Stderr: ${this.stderr}`);
  }

  assertOutputContains(text: string): void {
    assert.ok(this.stdout.includes(text), `Expected output to contain "${text}", got: ${this.stdout}`);
  }
}

// 2. Step Definitions (referencing Driver)
const driver = new SyncDriver(process.cwd());

Given('athlete configuration is available', async function () {
  await driver.setAthleteConfig({ athlete_id: 'test_123' });
});

When('the workout sync runs for date {string}', async function (targetDate: string) {
  await driver.runSync(targetDate);
});

Then('the process exits with code {int}', function (code: number) {
  driver.assertExitCode(code);
});

Then('the sync report contains {string}', function (text: string) {
  driver.assertOutputContains(text);
});
```
