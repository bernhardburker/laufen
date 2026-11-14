# Development & Quality Guidelines

## Language & Coding Conventions
- **English Only for Code & Tests**: All source code, scripts, variable/function/class names, code comments, docstrings, git commit messages, Gherkin feature files, step definitions, test drivers, and technical documentation must be written in **English**, even if the chat conversation is conducted in German or another language.
- **No Absolute Filepaths in Repository Files**: Never use machine-specific absolute paths (`/home/...`, `/mnt/...`, or `file:///...`) inside repository Markdown files, configurations, or tests. Always use clean relative paths.

## Agent Context Router & Authoritative Ground Truth
Before implementing features, modifying pipelines, or evaluating domain questions, consult the authoritative specifications and compare user statements against these system invariants:
- **[`docs/SYSTEM_SPEC.md`](docs/SYSTEM_SPEC.md)**: **Authoritative Domain & Architecture Spec.** Contains Coaching DNA (strict HR vs. Pace separation, Run-Walk biomechanics, de-dogmatized 80/20 macro compass, athlete-configurable periodization slots via `athlete.json`), physiological formulas (AEF, Banister CTL/ATL/TSB, Riegel exponent), 24-hour operational lifecycle, and SOLL vs. IST architecture. If a user instruction conflicts with an invariant in `docs/SYSTEM_SPEC.md`, explicitly point out the discrepancy before proceeding.
- **[`src/core/contracts.py`](src/core/contracts.py)**: **Executable Data Contracts.** `TypedDict` definitions and runtime validators for `run_reviews.json` (`schema_version: 2`), `coach_history.json`, `athlete.json`, and planning markers.
- **[`docs/ARCHITECTURE_REVIEW_AND_FINDINGS.md`](docs/ARCHITECTURE_REVIEW_AND_FINDINGS.md)**: **Active Bug Backlog & Refactoring Roadmap.** Tracks confirmed defects, test gaps, and agreed architectural transitions.
- **[`tests/README.md`](tests/README.md)**: **BDD & Test Driver Architecture.** Reference examples for `pytest-bdd` and Driver classes.
- **Ignore [`docs/archive/`](docs/archive/README.md)**: Historical plans and superseded specs in `docs/archive/` must not be used as current implementation references.

## Testing Policy & Scope
- **Scope**: Mandatory automated tests for all scripts, CLI tools, background automatisms, data pipelines, and platform integrations (Garmin, Intervals.icu, Strava).
- **Existing Code**: Whenever existing automatisms or scripts are touched, modified, or refactored, corresponding tests must be added or updated.
- **External API Isolation**: Standard automated tests (`./run test`) must never perform real HTTP requests to external APIs (Intervals.icu, Garmin, Strava). External dependencies must be mocked.
- **Live E2E & Staging Sandbox**: Live interactions with external APIs for testing purposes are strictly restricted to designated test tenants (e.g. `berni-test`) to protect real training histories and preserve rate limits.
- **Operational Exemption**:
  - **No tests required for operational tasks**: Running existing scripts, fetching or evaluating training data, generating reports, scheduling workouts, or adjusting local configuration files.
  - Tests are strictly tied to **code creation and code modifications**.

## Architecture & BDD Pattern (Gherkin + Driver Pattern)
- **Syntax**: Tests must be written in **Gherkin syntax** (`Given` / `When` / `Then`) inside `.feature` files.
- **Framework**: `pytest-bdd` (with `pytest`).
- **Strict Driver Pattern**:
  - **Feature Files (`tests/*.feature`)**: Purely declarative business logic and user scenarios. No technical implementation details.
  - **Step Definitions (`tests/test_*.py`)**: Thin glue code mapping Gherkin steps to Driver method calls. No raw `subprocess`, file system I/O, or direct HTTP/API calls inside steps.
  - **Test Drivers**: Encapsulates execution mechanics (invoking CLI commands, process lifecycle, environment variables, mock servers, exit codes, output parsing, and assertions). Defined directly within the `test_*.py` file or shared via [`tests/drivers.py`](tests/drivers.py).

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
- **Central Runner**: Use `./run <command> [--athlete <slug> | --all]` for all operations (resolves tenant paths via [`src/athletes.py`](src/athletes.py) and [`athletes/registry.json`](athletes/registry.json)):
  - `./run sync`: Sync profile, wellness, activities, calendar events & adaptive workout plan
  - `./run sync-profile`: Fetch athlete profile & calibrated HR zones to `athletes/<slug>/athlete.json`
  - `./run report`: Show terminal summary of recent runs & weekly trends
  - `./run review`: Run incremental 4-stage physiological & compliance reviews (`athletes/<slug>/run_reviews.json`)
  - `./run coach`: Execute Macro AI Coach pipeline (`athletes/<slug>/ai_coach_summary.json` & `coach_history.json`)
  - `./run trace`: Inspect Coach thinking, evaluated decision criteria, rejected alternatives & `decision_trace.jsonl`
  - `./run dashboard`: Generate multi-tenant HTML dashboard (`dist/<dashboard_slug>/index.html`)
  - `./run preview [PORT]`: Build dashboard and launch local webserver (default port 8080)
  - `./run plan`: Schedule workouts or inspect readiness markers (`--markers`, `--adaptive-plan`)
  - `./run test`: Run the offline BDD test suite (including documentation & contract drift checks)
  - `./run test:e2e`: Run live Intervals.icu staging tests against `berni-test`
  - `./run init`: Run system initialization

## Deployment & CI/CD Policy
- **Strictly No Manual Deployments**: Never perform ad-hoc manual deployments (e.g., manual `rsync` to `~/dockerContainer/laufen`, manual copying of application bundles, or manual `systemctl --user restart` on server services).
- **Exclusively Automated via CI/CD**: All deployments to Burkerserver, staging environments, and production services must be executed exclusively through the automated GitHub Actions CI/CD pipeline ([`.github/workflows/deploy-burkerserver.yml`](.github/workflows/deploy-burkerserver.yml)).
- **Pipeline Integrity**: Code changes must be committed and pushed to `main` (or triggered via `workflow_dispatch`). The pipeline guarantees pre-flight CI testing, staging sandbox validation (`test:e2e`), atomic bundle synchronization, and post-deployment health verification.

## Documentation Co-Update & Drift Prevention Policy
- **Mandatory Co-Update**: Whenever you add or modify CLI subcommands in [`./run`](run), JSON payload keys in analysis/planning modules, workout templates in [`workouts/templates/`](workouts/templates/), or domain rules, you **must** update [`src/core/contracts.py`](src/core/contracts.py), [`docs/SYSTEM_SPEC.md`](docs/SYSTEM_SPEC.md), and [`README.md`](README.md) in the same task.
- **Automated Drift Verification**: [`tests/documentation_integrity.feature`](tests/documentation_integrity.feature) runs automatically during `./run test` and fails the build if any markdown file link is broken or uses an absolute path, if any `./run` command is missing from `./run help` or `README.md`, if any workout template is undocumented, or if core module outputs violate [`src/core/contracts.py`](src/core/contracts.py).

## Verification
- Whenever scripts, tests, or contracts are created or edited, execute and pass the test suite before completing the task:
  ```bash
  ./run test
  # or: .venv/bin/pytest tests/
  ```
- For pure documentation-only edits (`*.md` files), do **not** trigger full test/verify runs; run only the fast documentation drift check when links, CLI commands, or template references changed:
  ```bash
  .venv/bin/pytest tests/test_documentation_integrity.py
  ```
