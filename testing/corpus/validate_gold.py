#!/usr/bin/env python3
"""Validate a parser response against independently retained gold values."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def get(value: object, *path: str) -> object:
    current = value
    for part in path:
        if not isinstance(current, dict):
            return None
        current = current.get(part)
    return current


def section(response: dict, key: str) -> dict:
    paths = {
        "indications": ("sections", "indications"),
        "pediatricsPrimary": ("sections", "pediatrics", "primary"),
        "geriatricsPrimary": ("sections", "geriatrics", "primary"),
        "contraindications": ("sections", "contraindications"),
    }
    value = get(response, *paths[key])
    return value if isinstance(value, dict) else {}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--annotation", type=Path, required=True)
    parser.add_argument("--response", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    expected = json.loads(args.annotation.read_text(encoding="utf-8"))
    actual = json.loads(args.response.read_text(encoding="utf-8"))
    checks: dict[str, bool] = {
        "language": get(actual, "language", "value") == expected["language"],
        "templateFamily": get(actual, "template", "family") == expected["templateFamily"],
        "controlNumber": get(actual, "controlNumber", "value") == expected["controlNumber"],
        "initialAuthorization": get(actual, "dates", "initialAuthorization", "isoValue") == expected["initialAuthorization"]["isoValue"],
        "revision": get(actual, "dates", "revision", "isoValue") == expected["revision"]["isoValue"],
    }
    for name, annotation in expected["sections"].items():
        parsed = section(actual, name)
        markdown = str(parsed.get("rawMarkdown") or "")
        prefix = "section:" + name
        checks[prefix + ":startPage"] = parsed.get("startPage") == annotation.get("startPage")
        if annotation.get("matchedHeading") is not None:
            checks[prefix + ":matchedHeading"] = parsed.get("matchedHeading") == annotation["matchedHeading"]
        checks[prefix + ":sha256"] = hashlib.sha256(markdown.encode("utf-8")).hexdigest() == annotation["sha256Utf8RawMarkdown"]

    report = {
        "documentId": expected["documentId"],
        "passed": all(checks.values()),
        "checks": checks,
        "passedCount": sum(checks.values()),
        "checkCount": len(checks),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
