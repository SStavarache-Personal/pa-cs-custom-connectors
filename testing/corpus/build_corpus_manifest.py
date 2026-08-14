#!/usr/bin/env python3
"""Join the SharePoint inventory to semantic-model EN/FR link ground truth."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


MIB = 1024 * 1024


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sharepoint", type=Path, required=True)
    parser.add_argument("--semantic", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--summary", type=Path, required=True)
    return parser.parse_args()


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read_jsonl(path: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    header: dict[str, Any] | None = None
    records: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as stream:
        for line_number, line in enumerate(stream, 1):
            if not line.strip():
                continue
            value = json.loads(line)
            if header is None:
                header = value
            else:
                records.append(value)
    if header is None:
        raise ValueError(f"Manifest is empty: {path}")
    return header, records


def atomic_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".new")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def main() -> int:
    args = parse_args()
    sharepoint_path = args.sharepoint.resolve()
    semantic_path = args.semantic.resolve()
    sharepoint_header, files = read_jsonl(sharepoint_path)
    semantic_header, links = read_jsonl(semantic_path)

    labels: dict[str, list[dict[str, str]]] = defaultdict(list)
    for link in links:
        labels[str(link["name"]).casefold()].append(
            {"language": str(link["language"]), "url": str(link["url"])}
        )

    records: list[dict[str, Any]] = []
    status_counts: Counter[str] = Counter()
    language_counts: Counter[str] = Counter()
    for file in files:
        name = str(file["name"])
        matched = sorted(labels.get(name.casefold(), []), key=lambda value: (value["language"], value["url"]))
        languages = sorted({value["language"] for value in matched})
        if len(languages) == 1:
            expected_language: str | None = languages[0]
            language_status = "matched"
            language_counts[expected_language] += 1
        elif len(languages) > 1:
            expected_language = None
            language_status = "ambiguous"
        else:
            expected_language = None
            language_status = "unlabeled"
        status_counts[language_status] += 1

        length = int(file.get("lengthBytes") or 0)
        records.append(
            {
                "documentId": Path(name).stem,
                "name": name,
                "itemId": int(file["itemId"]),
                "uniqueId": file.get("uniqueId"),
                "serverRelativeUrl": file.get("serverRelativeUrl"),
                "lengthBytes": length,
                "modified": file.get("modified"),
                "expectedLanguage": expected_language,
                "languageLabelStatus": language_status,
                "semanticLanguages": languages,
                "healthCanadaUrls": [value["url"] for value in matched],
                "over20MiB": length > 20 * MIB,
                "over32MiB": length > 32 * MIB,
                "over64MiB": length > 64 * MIB,
            }
        )

    records.sort(key=lambda value: (value["name"].casefold(), value["itemId"]))
    largest = sorted(records, key=lambda value: value["lengthBytes"], reverse=True)[:20]
    summary = {
        "generatedUtc": utc_now(),
        "sharePointSnapshotUtc": sharepoint_header.get("snapshotUtc"),
        "semanticSnapshotUtc": semantic_header.get("snapshotUtc"),
        "fileCount": len(records),
        "languageLabelStatusCounts": dict(sorted(status_counts.items())),
        "expectedLanguageCounts": dict(sorted(language_counts.items())),
        "over20MiB": sum(1 for value in records if value["over20MiB"]),
        "over32MiB": sum(1 for value in records if value["over32MiB"]),
        "over64MiB": sum(1 for value in records if value["over64MiB"]),
        "maximumLengthBytes": max((value["lengthBytes"] for value in records), default=0),
        "largestFiles": [
            {
                "name": value["name"],
                "lengthBytes": value["lengthBytes"],
                "expectedLanguage": value["expectedLanguage"],
            }
            for value in largest
        ],
        "sourceHashes": {
            "sharepointSha256": sha256(sharepoint_path),
            "semanticSha256": sha256(semantic_path),
        },
    }

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".new")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(
            json.dumps(
                {"recordType": "corpusManifest", **summary},
                ensure_ascii=False,
                separators=(",", ":"),
            )
            + "\n"
        )
        for record in records:
            stream.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")
    temporary.replace(output)
    atomic_json(args.summary.resolve(), summary)
    print(json.dumps({"completed": True, **{key: summary[key] for key in ("fileCount", "languageLabelStatusCounts", "expectedLanguageCounts", "over20MiB", "over32MiB", "over64MiB", "maximumLengthBytes")}}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
