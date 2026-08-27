"""Validate the canonical public chat-artifact contract and fixture."""

import json
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


CONTRACT_DIR = Path(__file__).resolve().parents[3] / "contracts" / "chat-artifacts"


def test_procurement_fixture_matches_checked_in_schema():
    schema = json.loads((CONTRACT_DIR / "procurement-opportunities.v1.schema.json").read_text())
    fixture = json.loads((CONTRACT_DIR / "fixtures" / "procurement-opportunities.v1.json").read_text())

    Draft202012Validator.check_schema(schema)
    Draft202012Validator(schema, format_checker=FormatChecker()).validate(fixture)
