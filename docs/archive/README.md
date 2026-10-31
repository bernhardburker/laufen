# Archived Design Plans & Historical Specifications

> [!WARNING]
> **HISTORICAL ARCHIVE — DO NOT USE AS CURRENT IMPLEMENTATION REFERENCE**
>
> The documents in this directory (`docs/archive/`) are historical design drafts, early feature proposals, and superseded specification versions from previous development iterations. They are preserved strictly for historical context.

## Authoritative Sources of Truth (Active)

If you are an AI coding agent or developer working on this repository, consult the active documentation instead:

1. **[`GEMINI.md`](../../GEMINI.md)** — Repository rules, agent context router, testing policies, and CLI workflows.
2. **[`docs/SYSTEM_SPEC.md`](../SYSTEM_SPEC.md)** — Authoritative domain rules (Coaching DNA, HR vs. Pace separation, physiological formulas), 24-hour operational lifecycle, and target vs. current architecture (SOLL vs. IST).
3. **[`src/core/contracts.py`](../../src/core/contracts.py)** — Executable `TypedDict` schemas and runtime validators for `run_reviews.json` (`schema_version: 2`), `coach_history.json`, `athlete.json`, `calendar_events.json`, and planning markers.
4. **[`docs/ARCHITECTURE_REVIEW_AND_FINDINGS.md`](../ARCHITECTURE_REVIEW_AND_FINDINGS.md)** — Active bug backlog, test coverage matrix, and agreed refactoring roadmap.
5. **[`README.md`](../../README.md)** — System overview, service catalog, CLI runner reference, and CI/CD pipelines.
