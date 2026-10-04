# Development & Quality Guidelines

## Language & Coding Conventions
- **English Only for Code & Tests**: All source code, scripts, variable/function/class names, code comments, docstrings, git commit messages, Gherkin feature files, step definitions, test drivers, and technical documentation must be written in **English**, even if the chat conversation is conducted in German or another language.

## Testing Policy & Scope
- **Scope**: Mandatory automated tests for all scripts, CLI tools, background automatisms, data pipelines, and platform integrations (Garmin, Intervals.icu, Strava).
- **Existing Code**: Whenever existing automatisms or scripts are touched, modified, or refactored, corresponding tests must be added or updated.
- **Operational Exemption**:
  - **No tests required for operational tasks**: Running existing scripts, fetching or evaluating training data, generating reports, scheduling workouts, or adjusting local configuration files.
  - Tests are strictly tied to **code creation and code modifications**.

## Architecture & BDD Pattern (Gherkin + Driver Pattern)
- **Syntax**: Tests must be written in **Gherkin syntax** (`Given` / `When` / `Then`) inside `.feature` files.
- **Framework**: `pytest-bdd` (with `pytest`).
- **Strict Driver Pattern**:
  - **Feature Files (`tests/*.feature`)**: Purely declarative business logic and user scenarios. No technical implementation details.
  - **Step Definitions (`tests/test_*.py`)**: Thin glue code mapping Gherkin steps to Driver method calls. No raw `subprocess`, file system I/O, or direct HTTP/API calls inside steps.
  - **Test Drivers**: Encapsulates execution mechanics (invoking CLI commands, process lifecycle, environment variables, mock servers, exit codes, output parsing, and assertions). Defined directly within the `test_*.py` file or shared via `tests/drivers.py`.

## Directory Structure (Flat Layout)
```text
tests/
├── <name>.feature        # Declarative Gherkin scenarios
├── test_<name>.py        # Step definitions + local Driver class
├── drivers.py            # Optional: Shared Driver classes across multiple tests
└── README.md             # Architecture documentation and implementation examples
```

## Environment, Python & Executable Paths
- **Initialization**: Run `./init.sh` to inspect the system and export paths to `.paths.env` and `.paths.json`.
- **Python & Pytest Executables**:
  - Direct execution: `.venv/bin/python` and `.venv/bin/pytest`
  - Shell activation: `source .paths.env` (prepends `.venv/bin` to `$PATH`)
  - Exported variables in `.paths.env`: `$PYTHON_EXEC`, `$PYTEST_EXEC`, `$PIP_EXEC`, `$WORKSPACE_ROOT`, `$VIRTUAL_ENV`
  - Machine-readable paths: `.paths.json`
- **Never invoke bare `python` or `pytest`** without sourcing `.paths.env` or using `.venv/bin/` because system `$PATH` does not include `.venv/bin` by default.

## Task Runner & CLI Interface
- **Central Runner**: Use `./run <command>` for common tasks:
  - `./run sync`: Fetch activities from Intervals.icu
  - `./run sync-profile`: Fetch athlete profile & calibrated HR zones to `config/athlete.json`
  - `./run report`: Show terminal summary of recent runs & weekly trends
  - `./run dashboard`: Generate HTML dashboard (`dist/index.html`)
  - `./run preview [PORT]`: Build dashboard and launch local webserver (default port 8080)
  - `./run plan`: Schedule workouts to Intervals.icu
  - `./run test`: Run the BDD test suite
  - `./run init`: Run system initialization

## Verification
- Whenever scripts or automatisms are created or edited, execute and pass the test suite before completing the task:
  ```bash
  ./run test
  # or: .venv/bin/pytest tests/
  ```

---

## Documentation & Reference Examples
- Detailed code examples for Python (`pytest-bdd`) and TypeScript (`@cucumber/cucumber`) as well as architectural details are documented in [tests/README.md](file:///mnt/data/work/laufen/tests/README.md).


