#!/usr/bin/env python3
"""onboard_athlete.py

Onboards a new runner into the multi-tenant Laufen Hub.
Connects to Intervals.icu via coach or personal API key, synchronizes
the athlete profile & HR zones, initializes the tenant directory,
bootstraps activity/wellness history, and updates athletes/registry.json.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
from pathlib import Path
import secrets
import sys
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.athletes import (
    AthleteContext,
    get_default_registry_path,
    list_registered_athletes,
    load_registry,
    resolve_athlete_context,
    save_registry,
)
from src.integrations.download_intervals import fetch_intervals_activities
from src.integrations.download_wellness import fetch_intervals_wellness
from src.integrations.sync_athlete import build_athlete_config, fetch_athlete_profile


def load_env_file(filepath: Path) -> None:
    """Loads key-value pairs from .env into os.environ if not already present."""
    if not filepath.is_file():
        return
    with open(filepath, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = line.split("=", 1)
            k, v = k.strip(), v.strip().strip("'\"")
            if k not in os.environ and v:
                os.environ[k] = v


def generate_dashboard_slug(slug: str) -> str:
    """Generates an unguessable privacy dashboard slug with random hex suffix."""
    token = secrets.token_hex(3)
    return f"{slug.strip().lower()}-{token}"


def onboard_athlete(
    slug: str,
    intervals_id: str,
    display_name: Optional[str] = None,
    api_key: Optional[str] = None,
    api_base_url: str = "https://intervals.icu/api/v1",
    fetch_history: bool = True,
    project_root: Optional[Path] = None,
    dry_run: bool = False,
) -> AthleteContext:
    """Performs the full onboarding sequence for a new runner."""
    root = project_root or PROJECT_ROOT
    clean_slug = slug.strip().lower()
    clean_id = intervals_id.strip()

    resolved_api_key = api_key or os.getenv("INTERVALS_API_KEY", "")
    if not resolved_api_key and not dry_run:
        raise ValueError(
            "INTERVALS_API_KEY is required to onboard an athlete from Intervals.icu."
        )

    print(f"==> Validating athlete '{clean_slug}' (ID: {clean_id}) via Intervals.icu API...")
    if dry_run:
        profile_data = {
            "id": clean_id,
            "name": display_name or clean_slug.capitalize(),
            "icu_resting_hr": 50,
            "sportSettings": [
                {
                    "types": ["Run"],
                    "lthr": 168,
                    "max_hr": 185,
                    "hr_zones": [140, 150, 160, 170, 178, 182, 185],
                    "hr_zone_names": ["Recovery", "Aerobic", "Tempo", "SubThreshold", "SuperThreshold", "Aerobic Capacity", "Anaerobic"],
                }
            ],
        }
    else:
        profile_data = fetch_athlete_profile(
            api_key=resolved_api_key,
            athlete_id=clean_id,
            api_base_url=api_base_url,
        )

    resolved_name = display_name or profile_data.get("name") or clean_slug.capitalize()
    athlete_cfg = build_athlete_config(profile_data)
    athlete_cfg["athlete"]["display_name"] = resolved_name
    athlete_cfg["athlete"]["id"] = clean_id

    # Create tenant directory
    tenant_dir = root / "athletes" / clean_slug
    dash_slug = generate_dashboard_slug(clean_slug)

    print(f"==> Setting up tenant directory: {tenant_dir}")
    if not dry_run:
        tenant_dir.mkdir(parents=True, exist_ok=True)
        # Write athlete.json
        with open(tenant_dir / "athlete.json", "w", encoding="utf-8") as f:
            json.dump(athlete_cfg, f, indent=2, ensure_ascii=False)
            f.write("\n")

    # Fetch initial history (activities & wellness)
    if fetch_history and not dry_run:
        print("==> Bootstrapping activity history (last 90 days)...")
        oldest_act = (datetime.date.today() - datetime.timedelta(days=90)).isoformat()
        try:
            activities = fetch_intervals_activities(
                api_key=resolved_api_key,
                athlete_id=clean_id,
                oldest=oldest_act,
                api_base_url=api_base_url,
            )
            with open(tenant_dir / "intervals_activities.json", "w", encoding="utf-8") as f:
                json.dump(activities, f, indent=2, ensure_ascii=False)
                f.write("\n")
            print(f"    Fetched {len(activities)} activities.")
        except Exception as e:
            print(f"    Warning: Activity bootstrap failed: {e}")
            with open(tenant_dir / "intervals_activities.json", "w", encoding="utf-8") as f:
                f.write("[]\n")

        print("==> Bootstrapping wellness & sleep records (last 30 days)...")
        oldest_well = (datetime.date.today() - datetime.timedelta(days=30)).isoformat()
        try:
            wellness = fetch_intervals_wellness(
                api_key=resolved_api_key,
                athlete_id=clean_id,
                oldest=oldest_well,
                api_base_url=api_base_url,
            )
            with open(tenant_dir / "intervals_wellness.json", "w", encoding="utf-8") as f:
                json.dump(wellness, f, indent=2, ensure_ascii=False)
                f.write("\n")
            print(f"    Fetched {len(wellness)} wellness records.")
        except Exception as e:
            print(f"    Warning: Wellness bootstrap failed: {e}")
            with open(tenant_dir / "intervals_wellness.json", "w", encoding="utf-8") as f:
                f.write("[]\n")

    # Update registry
    registry = load_registry(project_root=root)
    registry.setdefault("athletes", {})[clean_slug] = {
        "intervals_id": clean_id,
        "display_name": resolved_name,
        "active": True,
        "dashboard_slug": dash_slug,
    }
    if not registry.get("default_athlete"):
        registry["default_athlete"] = clean_slug

    if not dry_run:
        save_registry(registry, project_root=root)
        print(f"==> Updated athletes/registry.json (Slug: '{clean_slug}', Dashboard: '{dash_slug}')")

    ctx = resolve_athlete_context(clean_slug, project_root=root)
    return ctx


def print_athletes_summary(project_root: Optional[Path] = None) -> None:
    """Displays a formatted overview of all registered athletes."""
    root = project_root or PROJECT_ROOT
    registry = load_registry(project_root=root)
    athletes = registry.get("athletes", {})
    default_slug = registry.get("default_athlete")

    print("\n" + "=" * 78)
    print("  Laufen Management Hub - Registered Athletes")
    print("=" * 78)
    if not athletes:
        print("  No athletes registered in athletes/registry.json.")
        print("  Use './run add-athlete <slug> --intervals-id <id>' to onboard an athlete.")
        print("=" * 78 + "\n")
        return

    header = f"{'Slug':<12} | {'Name':<14} | {'Intervals ID':<12} | {'Telegram ID':<12} | {'Time':<5} | {'Status':<8} | {'Dashboard Slug'}"
    print(header)
    print("-" * 92)
    for slug, info in athletes.items():
        status = "Active" if info.get("active", True) else "Inactive"
        name = str(info.get("display_name") or slug)[:14]
        iid = str(info.get("intervals_id") or "-")[:12]
        tg_id = str(info.get("telegram_chat_id") or "-")[:12]
        b_time = str(info.get("briefing_time") or "07:00")[:5]
        d_slug = str(info.get("dashboard_slug") or slug)
        print(f"{slug:<12} | {name:<14} | {iid:<12} | {tg_id:<12} | {b_time:<5} | {status:<8} | {d_slug}")
    print("=" * 92 + "\n")


def main() -> None:
    load_env_file(PROJECT_ROOT / ".env")

    parser = argparse.ArgumentParser(description="Onboard a new runner into Laufen Hub.")
    parser.add_argument("slug", nargs="?", help="Unique alphanumeric slug for the athlete (e.g. 'anna')")
    parser.add_argument("--intervals-id", "-i", help="Athlete ID in Intervals.icu (e.g. 'i771119')")
    parser.add_argument("--name", "-n", help="Display name for the runner (e.g. 'Anna')")
    parser.add_argument("--list", "-l", action="store_true", help="List all registered athletes")
    parser.add_argument("--no-history", action="store_true", help="Skip initial 90-day history sync")
    parser.add_argument("--dry-run", action="store_true", help="Simulate onboarding without writing to disk")
    parser.add_argument("--api-base", default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"), help="API Base URL")

    args = parser.parse_args()

    if args.list or not args.slug:
        print_athletes_summary()
        if not args.list and not args.slug:
            sys.exit(0)
        return

    if not args.intervals_id:
        # Prompt or error
        sys.exit("Error: --intervals-id is required when onboarding an athlete.")

    try:
        ctx = onboard_athlete(
            slug=args.slug,
            intervals_id=args.intervals_id,
            display_name=args.name,
            api_base_url=args.api_base,
            fetch_history=not args.no_history,
            dry_run=args.dry_run,
        )
        print("\n🎉 Onboarding completed successfully!")
        print(f"Athlete slug      : {ctx.slug}")
        print(f"Display name      : {ctx.display_name}")
        print(f"Intervals.icu ID  : {ctx.intervals_id}")
        print(f"Config path       : {ctx.athlete_config_path}")
        print(f"Dashboard folder  : {ctx.dashboard_output_dir}")
        print(f"Private URL slug  : {ctx.dashboard_slug}")
    except Exception as e:
        sys.exit(f"\n❌ Onboarding failed: {e}")


if __name__ == "__main__":
    main()
