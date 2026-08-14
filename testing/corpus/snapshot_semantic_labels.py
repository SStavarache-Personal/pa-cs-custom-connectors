#!/usr/bin/env python3
"""Snapshot EN/FR monograph-link labels through the Market Access DAX endpoint skill."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import unquote, urlparse


DAX_QUERY = r'''
EVALUATE
VAR Links =
    DISTINCT(
        UNION(
            SELECTCOLUMNS(
                FILTER(
                    'Canadian Product Database',
                    LEN(TRIM(COALESCE('Canadian Product Database'[Product Monograph Link (EN)], ""))) > 0
                ),
                "language", "en",
                "url", 'Canadian Product Database'[Product Monograph Link (EN)]
            ),
            SELECTCOLUMNS(
                FILTER(
                    'Canadian Product Database',
                    LEN(TRIM(COALESCE('Canadian Product Database'[Product Monograph Link (FR)], ""))) > 0
                ),
                "language", "fr",
                "url", 'Canadian Product Database'[Product Monograph Link (FR)]
            )
        )
    )
RETURN Links
ORDER BY [url], [language]
'''.strip()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--market-access-repo", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model-key", default="Market Access Semantic Model")
    parser.add_argument("--timeout-seconds", type=int, default=180)
    return parser.parse_args()


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def file_name_from_url(url: str) -> str:
    parsed = urlparse(url)
    return Path(unquote(parsed.path)).name


def main() -> int:
    args = parse_args()
    market_repo = args.market_access_repo.resolve()
    helper = market_repo / ".agents" / "skills" / "powerbi-dax-query-endpoint" / "scripts" / "Invoke-PowerBIDaxQuery.ps1"
    if not helper.exists():
        raise SystemExit(f"DAX endpoint helper not found: {helper}")

    shell = shutil.which("pwsh") or shutil.which("powershell") or shutil.which("powershell.exe")
    if shell is None:
        raise SystemExit("PowerShell is required to invoke the repository DAX endpoint skill.")

    completed = subprocess.run(
        [
            shell,
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-File",
            str(helper),
            "-ModelKey",
            args.model_key,
            "-Query",
            DAX_QUERY,
        ],
        cwd=market_repo,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=args.timeout_seconds,
        check=False,
    )
    if completed.returncode != 0:
        message = completed.stderr.strip() or "The DAX helper returned a non-zero exit code."
        raise SystemExit(message)

    try:
        rows = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise SystemExit("The DAX endpoint helper did not return valid JSON.") from exc
    if not isinstance(rows, list):
        raise SystemExit("The DAX endpoint result was not a row array.")

    records: set[tuple[str, str, str]] = set()
    for row in rows:
        if not isinstance(row, dict):
            continue
        language = str(row.get("[language]") or row.get("language") or "").strip().lower()
        url = str(row.get("[url]") or row.get("url") or "").strip()
        if language not in {"en", "fr"} or not url:
            continue
        name = file_name_from_url(url)
        if not name.lower().endswith(".pdf"):
            continue
        records.add((name, language, url))

    ordered = sorted(records, key=lambda item: (item[0].casefold(), item[1], item[2]))
    counts = Counter(language for _, language, _ in ordered)
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".new")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(
            json.dumps(
                {
                    "recordType": "semanticSnapshot",
                    "snapshotUtc": utc_now(),
                    "modelKey": args.model_key,
                    "rowCount": len(ordered),
                    "countsByLanguage": dict(sorted(counts.items())),
                    "querySha256": __import__("hashlib").sha256(DAX_QUERY.encode("utf-8")).hexdigest(),
                },
                ensure_ascii=False,
                separators=(",", ":"),
            )
            + "\n"
        )
        for name, language, url in ordered:
            stream.write(
                json.dumps(
                    {"name": name, "language": language, "url": url},
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
                + "\n"
            )
    temporary.replace(output)
    print(json.dumps({"completed": True, "rowCount": len(ordered), "countsByLanguage": counts, "output": str(output)}, default=dict))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
