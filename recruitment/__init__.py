"""Recruitment features ported from the unified notebook.

This package contains CV parsing, ATS analysis, scraping,
vector matching, and scheduler helpers.
"""

from .config import get_recruitment_settings

__all__ = ["get_recruitment_settings"]
