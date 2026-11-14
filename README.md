# 🏃‍♂️ Laufen — Multi-Athlete Running Analytics, Adaptive Planner & AI Coach

A zero-dependency (Python 3 standard library) endurance training platform connecting **Intervals.icu** (syncing Garmin Connect, Coros, Wahoo, Suunto, and Strava), **Antigravity CLI (`agy`) / LLM providers**, an interactive **Telegram Coach Bot**, and a self-contained **HTML5 Analytics Dashboard**.

---

## 📚 Documentation & Architecture

Detailed system architecture, coaching philosophy, data contracts, and operational workflows live in [`docs/`](docs/SYSTEM_SPEC.md):

- **[`docs/SYSTEM_SPEC.md`](docs/SYSTEM_SPEC.md)**: Authoritative **SOLL Architecture & Domain Ground Truth** (core invariants, component architecture, HR vs. Pace coaching DNA, physiological formulas, data contracts, 24-hour workflow-dispatch lifecycle, and workout DSL templates).
- **[`docs/ARCHITECTURE_REVIEW_AND_FINDINGS.md`](docs/ARCHITECTURE_REVIEW_AND_FINDINGS.md)**: **IST Convergence Status & Roadmap** (completed architectural phases, open multi-day handoff findings, and next implementation steps).
- **[`docs/WORKFLOW_DISPATCH_ARCHITECTURE_PLAN.md`](docs/WORKFLOW_DISPATCH_ARCHITECTURE_PLAN.md)**: Event-driven GitHub Actions workflow dispatch & thin Telegram event-router architecture.
- **[`docs/FORESIGHT_ENGINE_PLAN.md`](docs/FORESIGHT_ENGINE_PLAN.md)** & **[`docs/AGENTIC_SIMULATION_PLAN.md`](docs/AGENTIC_SIMULATION_PLAN.md)**: Planned 14–28 day load/deload projection engine and multi-day scenario simulator.
- **[`src/core/contracts.py`](src/core/contracts.py)**: Executable `TypedDict` data contracts and runtime schema validators.
- **[`tests/README.md`](tests/README.md)**: Two-tier BDD testing architecture (`pytest-bdd` + Test Driver Pattern) and offline/staging isolation.
- **[`docs/archive/`](docs/archive/README.md)**: Historical design plans and superseded specifications.

---

## 🚀 Quick Start & Configuration

### 1. Initialize Environment
```bash
./init.sh
source .paths.env
```
`./init.sh` locates Python 3 and `.venv/bin/pytest`, writing environment exports to `.paths.env` and `.paths.json`.

### 2. Configure `.env`
Copy [`.env.example`](.env.example) to `.env` and configure your credentials:
```dotenv
# Required: Intervals.icu API Access (Settings -> Developer Settings)
INTERVALS_API_KEY=your_api_key_here
INTERVALS_ATHLETE_ID=i697752

# Optional: Telegram Coach Bot
TELEGRAM_BOT_TOKEN=123456:ABC-DEF...
TELEGRAM_ALLOWED_USER_IDS=157754568

# Optional: LLM Provider ("agy", "gemini", "openai", "mock")
LLM_PROVIDER=agy
```

---

## 🛠️ Central CLI Runner (`./run`)

All commands support `--athlete <slug>` (targets a specific tenant) or `--all` (iterates over all active athletes in [`athletes/registry.json`](athletes/registry.json) for batch commands):

| Command | Description |
| :--- | :--- |
| `./run sync [--athlete <slug> \| --all]` | Sync athlete profile, wellness, activities, calendar events, and adaptive workout plan |
| `./run sync-profile [--athlete <slug>]` | Fetch calibrated HR zones, LTHR, and resting HR into `athletes/<slug>/athlete.json` |
| `./run wellness [--athlete <slug> \| --all]` | Fetch daily sleep, HRV, resting HR, and CTL/ATL/TSB metrics |
| `./run events [--athlete <slug> \| --all]` | Fetch scheduled calendar events and workouts (`-7d` to `+14d`) |
| `./run upload [ARGS]` | Bulk-upload historical activity export files to Intervals.icu |
| `./run note "<text>" [--date YYYY-MM-DD]` | Attach subjective athlete feedback to a run and re-evaluate its 4-stage review |
| `./run today [--athlete <slug>]` | Print today's adaptive training recommendation in the terminal |
| `./run review [--athlete <slug> \| --all]` | Run incremental 4-stage physiological & compliance reviews for unanalyzed runs |
| `./run coach [--athlete <slug> \| --all]` | Execute the full Macro AI Coach pipeline ([`run-ai-coach.sh`](.github/scripts/run-ai-coach.sh)) |
| `./run report [--athlete <slug>]` | Display terminal summary tables of recent runs and weekly trends |
| `./run runs [--athlete <slug>]` | Display recent running activities table |
| `./run trends [--athlete <slug>]` | Display weekly trends, volume, and HR zone discipline |
| `./run history [--athlete <slug>]` | Display Coach cycle memory and compliance feedback loop |
| `./run trace [--athlete <slug>] [ARGS]` | Inspect Coach thinking, evaluated decision criteria, rejected options, and `decision_trace.jsonl` |
| `./run dashboard [--athlete <slug> \| --all]` | Generate static HTML5 dashboard (`dist/<dashboard_slug>/index.html`) |
| `./run preview [PORT]` | Build dashboard and serve locally on `http://localhost:8080` |
| `./run plan [ARGS]` | Schedule workouts or inspect markers (`--markers`, `--adaptive-plan`, `--sync-plan`, `--type ...`) |
| `./run bot [--scheduler \| --once]` | Start the interactive Telegram Coach Bot daemon |
| `./run briefing [--athlete <slug> \| --all]` | Send proactive morning briefing via Telegram |
| `./run evening [--athlete <slug> \| --all]` | Send evening workout logistics preview via Telegram |
| `./run post-run-review [--athlete <slug> \| --all]` | Send 4-stage post-run review for the latest completed run via Telegram |
| `./run athletes` | List all registered athletes in `athletes/registry.json` |
| `./run add-athlete <slug> --intervals-id <id>` | Onboard a new athlete and bootstrap their historical data |
| `./run seed --athlete berni-test [ARGS]` | Inject or purge (`--clean`) synthetic test data on the staging sandbox |
| `./run dispatch [--sync-only]` | Trigger the remote GitHub Actions pipeline via `gh` CLI or REST API |
| `./run test` | Execute the offline BDD test suite (`pytest -m "not e2e"`) |
| `./run test:e2e` | Execute live Intervals.icu API contract tests against staging tenant `berni-test` |
| `./run init` | Inspect system and update `.paths.env` / `.paths.json` |
| `./run help` | Display runner command overview |

---

## 🧪 Testing & CI/CD

- **Tests**: See [`tests/README.md`](tests/README.md) for the offline mocked BDD suite (`./run test`), live staging E2E tests (`./run test:e2e`), and automated documentation/contract drift checks ([`tests/documentation_integrity.feature`](tests/documentation_integrity.feature)).
- **CI/CD Workflows**: All deployments and scheduled coaching/briefing pipelines are automated via [`.github/workflows/`](.github/workflows/deploy-burkerserver.yml) (`coach-plan.yml`, `send-telegram.yml`, `post-run-review.yml`, `deploy-pages.yml`, `deploy-burkerserver.yml`, `deploy-agent.yml`). See [`docs/SYSTEM_SPEC.md`](docs/SYSTEM_SPEC.md) §2 & §5 for details.
- **Agent Bundle & Skill**: Standalone personal assistant definition in [`agent/manifest.yaml`](agent/manifest.yaml) and coding-agent skill in [`.agents/skills/workout-scheduler/SKILL.md`](.agents/skills/workout-scheduler/SKILL.md).
