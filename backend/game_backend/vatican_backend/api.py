"""Compatibility ASGI factory; implementation lives in presentation."""
from .presentation.app import create_app

__all__ = ["create_app"]
