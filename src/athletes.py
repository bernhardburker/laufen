#!/usr/bin/env python3
"""athletes.py

Central athlete context and multi-tenant registry resolution.
Provides path resolution, registry management, and backward compatibility
for the Laufen running management hub.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
import os
from pathlib import Path
from typing import Any, Dict, List, Optional


@dataclass(frozen=True)
class AthleteContext:
    """Encapsulates all path and profile identifiers for a specific athlete."""
    slug: str
    intervals_id: str
    display_name: str
    active: bool
    dashboard_slug: str
    base_dir: Path
    athlete_config_path: Path
    activities_path: Path
    wellness_path: Path
    coach_history_path: Path
    run_reviews_path: Path
    ai_summary_path: Path
    ai_commentary_path: Path
    dashboard_output_dir: Path
    telegram_chat_id: str = ""
    briefing_time: str = "07:00"
    briefing_enabled: bool = True
    telegram_state_path: Optional[Path] = None

    def ensure_dirs(self) -> None:
        """Creates necessary athlete directory trees if they do not exist."""
        self.base_dir.mkdir(parents=True, exist_ok=True)
        self.dashboard_output_dir.mkdir(parents=True, exist_ok=True)


def get_project_root() -> Path:
    """Resolves the root directory of the repository."""
    return Path(__file__).resolve().parent.parent


def get_default_registry_path(project_root: Optional[Path] = None) -> Path:
    """Returns the standard path to athletes/registry.json."""
    root = project_root or get_project_root()
    return root / "athletes" / "registry.json"


def load_registry(
    registry_path: Optional[Path] = None,
    project_root: Optional[Path] = None,
) -> Dict[str, Any]:
    """Loads the athlete registry or returns a default empty structure."""
    path = registry_path or get_default_registry_path(project_root)
    if not path.is_file():
        return {"default_athlete": "berni", "athletes": {}}
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {"default_athlete": "berni", "athletes": {}}


def save_registry(
    registry_data: Dict[str, Any],
    registry_path: Optional[Path] = None,
    project_root: Optional[Path] = None,
) -> None:
    """Persists the athlete registry dictionary to disk."""
    path = registry_path or get_default_registry_path(project_root)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(registry_data, f, indent=2, ensure_ascii=False)
        f.write("\n")


def resolve_athlete_context(
    athlete_slug: Optional[str] = None,
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
    allow_legacy_fallback: bool = True,
) -> AthleteContext:
    """Resolves an AthleteContext following precedence:

    1. Explicit athlete_slug argument
    2. Environment variable ATHLETE_SLUG
    3. default_athlete from athletes/registry.json
    4. Legacy single-tenant directory structure (data/ and config/)
    """
    root = project_root or get_project_root()
    registry = load_registry(registry_path, root)
    athletes_table = registry.get("athletes", {})

    target_slug = (
        athlete_slug
        or os.getenv("ATHLETE_SLUG")
        or registry.get("default_athlete")
        or "berni"
    ).strip()

    telegram_chat_id = ""
    briefing_time = "07:00"
    briefing_enabled = True

    # If athlete is found in registry
    if target_slug in athletes_table:
        info = athletes_table[target_slug]
        intervals_id = str(info.get("intervals_id") or "0")
        display_name = str(info.get("display_name") or target_slug.capitalize())
        active = bool(info.get("active", True))
        dash_slug = str(info.get("dashboard_slug") or target_slug)
        telegram_chat_id = str(info.get("telegram_chat_id") or "")
        briefing_time = str(info.get("briefing_time") or "07:00")
        briefing_enabled = bool(info.get("briefing_enabled", True))
        base_dir = root / "athletes" / target_slug
    else:
        # Check if athlete directory exists even if missing from registry
        base_dir = root / "athletes" / target_slug
        if base_dir.is_dir():
            intervals_id = "0"
            display_name = target_slug.capitalize()
            # Try to read athlete.json if available
            athlete_cfg = base_dir / "athlete.json"
            if athlete_cfg.is_file():
                try:
                    with open(athlete_cfg, "r", encoding="utf-8") as f:
                        cfg_data = json.load(f).get("athlete", {})
                        intervals_id = str(cfg_data.get("id") or "0")
                        display_name = str(cfg_data.get("display_name") or cfg_data.get("name") or display_name)
                except Exception:
                    pass
            active = True
            dash_slug = target_slug
        elif allow_legacy_fallback:
            # Fall back to root config/athlete.json and data/ directory
            legacy_config = root / "config" / "athlete.json"
            intervals_id = "0"
            display_name = "Berni"
            if legacy_config.is_file():
                try:
                    with open(legacy_config, "r", encoding="utf-8") as f:
                        cfg_data = json.load(f).get("athlete", {})
                        intervals_id = str(cfg_data.get("id") or "0")
                        display_name = str(cfg_data.get("display_name") or cfg_data.get("name") or display_name)
                except Exception:
                    pass
            return AthleteContext(
                slug=target_slug,
                intervals_id=intervals_id,
                display_name=display_name,
                active=True,
                dashboard_slug=target_slug,
                base_dir=root / "data",
                athlete_config_path=legacy_config,
                activities_path=root / "data" / "intervals_activities.json",
                wellness_path=root / "data" / "intervals_wellness.json",
                coach_history_path=root / "data" / "coach_history.json",
                run_reviews_path=root / "data" / "run_reviews.json",
                ai_summary_path=root / "data" / "ai_coach_summary.json",
                ai_commentary_path=root / "data" / "ai_coach_commentary.md",
                dashboard_output_dir=root / "dist",
                telegram_chat_id="",
                briefing_time="07:00",
                briefing_enabled=True,
                telegram_state_path=root / "data" / "telegram_state.json",
            )
        else:
            raise KeyError(f"Athlete '{target_slug}' not found in registry and fallback disabled.")

    return AthleteContext(
        slug=target_slug,
        intervals_id=intervals_id,
        display_name=display_name,
        active=active,
        dashboard_slug=dash_slug,
        base_dir=base_dir,
        athlete_config_path=base_dir / "athlete.json",
        activities_path=base_dir / "intervals_activities.json",
        wellness_path=base_dir / "intervals_wellness.json",
        coach_history_path=base_dir / "coach_history.json",
        run_reviews_path=base_dir / "run_reviews.json",
        ai_summary_path=base_dir / "ai_coach_summary.json",
        ai_commentary_path=base_dir / "ai_coach_commentary.md",
        dashboard_output_dir=root / "dist" / dash_slug,
        telegram_chat_id=telegram_chat_id,
        briefing_time=briefing_time,
        briefing_enabled=briefing_enabled,
        telegram_state_path=base_dir / "telegram_state.json",
    )


def resolve_athlete_by_telegram_chat_id(
    chat_id: str | int,
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
) -> Optional[AthleteContext]:
    """Finds an active AthleteContext matching a given Telegram chat_id."""
    target_id = str(chat_id).strip()
    if not target_id:
        return None
    root = project_root or get_project_root()
    registry = load_registry(registry_path, root)
    athletes = registry.get("athletes", {})
    for slug, info in athletes.items():
        if not info.get("active", True):
            continue
        if str(info.get("telegram_chat_id") or "").strip() == target_id:
            return resolve_athlete_context(slug, project_root=root, registry_path=registry_path)
    return None


def link_telegram_chat_id(
    athlete_slug: str,
    chat_id: str | int,
    briefing_time: Optional[str] = None,
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
) -> AthleteContext:
    """Links a Telegram chat_id (and optional briefing_time) to an athlete in registry.json."""
    root = project_root or get_project_root()
    registry = load_registry(registry_path, root)
    athletes = registry.setdefault("athletes", {})
    clean_slug = athlete_slug.strip().lower()
    if clean_slug not in athletes:
        raise KeyError(f"Athlete '{clean_slug}' is not registered in athletes/registry.json.")
    athletes[clean_slug]["telegram_chat_id"] = str(chat_id).strip()
    if briefing_time:
        athletes[clean_slug]["briefing_time"] = briefing_time.strip()
    athletes[clean_slug].setdefault("briefing_enabled", True)
    save_registry(registry, registry_path=registry_path, project_root=root)
    return resolve_athlete_context(clean_slug, project_root=root, registry_path=registry_path)


def list_registered_athletes(
    project_root: Optional[Path] = None,
    registry_path: Optional[Path] = None,
    active_only: bool = True,
) -> List[AthleteContext]:
    """Returns a list of resolved AthleteContext instances from the registry."""
    root = project_root or get_project_root()
    registry = load_registry(registry_path, root)
    athletes = registry.get("athletes", {})

    contexts: List[AthleteContext] = []
    for slug, info in athletes.items():
        if active_only and not info.get("active", True):
            continue
        ctx = resolve_athlete_context(slug, project_root=root, registry_path=registry_path)
        contexts.append(ctx)
    return contexts


def print_shell_env(ctx: AthleteContext, is_default: bool = False) -> None:
    """Prints shell environment variable assignments for sourcing in bash scripts."""
    print(f'export ATHLETE_SLUG="{ctx.slug}"')
    print(f'export ATHLETE_DISPLAY_NAME="{ctx.display_name}"')
    print(f'export INTERVALS_ATHLETE_ID="{ctx.intervals_id}"')
    print(f'export ATHLETE_DASHBOARD_SLUG="{ctx.dashboard_slug}"')
    print(f'export ATHLETE_IS_DEFAULT="{"true" if is_default else "false"}"')
    print(f'export ATHLETE_BASE_DIR="{ctx.base_dir}"')
    print(f'export ATHLETE_CONFIG_PATH="{ctx.athlete_config_path}"')
    print(f'export ACTIVITIES_PATH="{ctx.activities_path}"')
    print(f'export WELLNESS_PATH="{ctx.wellness_path}"')
    print(f'export COACH_HISTORY_PATH="{ctx.coach_history_path}"')
    print(f'export RUN_REVIEWS_PATH="{ctx.run_reviews_path}"')
    print(f'export AI_SUMMARY_PATH="{ctx.ai_summary_path}"')
    print(f'export AI_COMMENTARY_PATH="{ctx.ai_commentary_path}"')
    print(f'export DASHBOARD_OUTPUT_DIR="{ctx.dashboard_output_dir}"')
    print(f'export TELEGRAM_CHAT_ID="{ctx.telegram_chat_id}"')
    print(f'export BRIEFING_TIME="{ctx.briefing_time}"')
    print(f'export BRIEFING_ENABLED="{"true" if ctx.briefing_enabled else "false"}"')
    print(f'export TELEGRAM_STATE_PATH="{ctx.telegram_state_path or (ctx.base_dir / "telegram_state.json")}"')


def main() -> None:
    import argparse

    parser = argparse.ArgumentParser(description="Multi-tenant athlete registry & context CLI.")
    parser.add_argument("--env", metavar="SLUG", nargs="?", const="", help="Output bash export statements for given or default athlete")
    parser.add_argument("--list-slugs", action="store_true", help="Print whitespace-delimited active athlete slugs")
    parser.add_argument("--json", action="store_true", help="Output context as JSON")
    parser.add_argument("--athlete", "-a", help="Target athlete slug")

    args = parser.parse_args()

    root = get_project_root()
    registry = load_registry(project_root=root)
    default_slug = registry.get("default_athlete", "berni")

    if args.list_slugs:
        athletes = list_registered_athletes(project_root=root, active_only=True)
        print(" ".join(a.slug for a in athletes))
        return

    target = args.env if args.env is not None else args.athlete
    if not target:
        target = default_slug

    ctx = resolve_athlete_context(target, project_root=root)
    is_default = (ctx.slug == default_slug)

    if args.env is not None:
        print_shell_env(ctx, is_default=is_default)
        return

    if args.json:
        payload = {
            "slug": ctx.slug,
            "display_name": ctx.display_name,
            "intervals_id": ctx.intervals_id,
            "active": ctx.active,
            "dashboard_slug": ctx.dashboard_slug,
            "is_default": is_default,
            "paths": {
                "base_dir": str(ctx.base_dir),
                "athlete_config": str(ctx.athlete_config_path),
                "activities": str(ctx.activities_path),
                "wellness": str(ctx.wellness_path),
                "coach_history": str(ctx.coach_history_path),
                "run_reviews": str(ctx.run_reviews_path),
                "ai_summary": str(ctx.ai_summary_path),
                "ai_commentary": str(ctx.ai_commentary_path),
                "dashboard_output_dir": str(ctx.dashboard_output_dir),
            },
        }
        print(json.dumps(payload, indent=2))
        return

    print_shell_env(ctx, is_default=is_default)


if __name__ == "__main__":
    main()

