"""Core package for shared configurations, path constants, and loaders."""
from src.core.config import (
    DEFAULT_ACTIVITIES_PATH,
    DEFAULT_ATHLETE_PATH,
    DEFAULT_HISTORY_PATH,
    DEFAULT_REVIEWS_PATH,
    DEFAULT_WELLNESS_PATH,
    PROJECT_ROOT,
    load_athlete_profile,
    load_env_file,
)

__all__ = [
    "PROJECT_ROOT",
    "DEFAULT_ATHLETE_PATH",
    "DEFAULT_ACTIVITIES_PATH",
    "DEFAULT_WELLNESS_PATH",
    "DEFAULT_REVIEWS_PATH",
    "DEFAULT_HISTORY_PATH",
    "load_athlete_profile",
    "load_env_file",
]
