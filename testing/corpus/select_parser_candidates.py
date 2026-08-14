#!/usr/bin/env python3
"""Select a deterministic bilingual, era- and size-stratified parser candidate set."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--per-language", type=int, default=240)
    parser.add_argument("--unlabeled", type=int, default=40)
    parser.add_argument("--include-document-id", action="append", default=["00003161"])
    return parser.parse_args()


def read_records(path: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    with path.open("r", encoding="utf-8") as stream:
        values = [json.loads(line) for line in stream if line.strip()]
    return values[0], values[1:]


def stable_key(record: dict[str, Any]) -> str:
    return hashlib.sha256(str(record["name"]).casefold().encode("utf-8")).hexdigest()


def stratified_pick(records: list[dict[str, Any]], count: int) -> list[dict[str, Any]]:
    if count >= len(records):
        return list(records)
    by_id = sorted(records, key=lambda value: (str(value["documentId"]), int(value["itemId"])))
    era_pools = [by_id[: max(1, len(by_id) // 2)], by_id[max(1, len(by_id) // 2) :]]
    selected: list[dict[str, Any]] = []
    for era_index, era in enumerate(era_pools):
        era_quota = count // 2 + (1 if era_index < count % 2 else 0)
        by_size = sorted(era, key=lambda value: (int(value["lengthBytes"]), stable_key(value)))
        size_buckets = [
            by_size[(len(by_size) * bucket) // 4 : (len(by_size) * (bucket + 1)) // 4]
            for bucket in range(4)
        ]
        for bucket_index, bucket in enumerate(size_buckets):
            quota = era_quota // 4 + (1 if bucket_index < era_quota % 4 else 0)
            selected.extend(sorted(bucket, key=stable_key)[:quota])
    unique = {int(value["itemId"]): value for value in selected}
    if len(unique) < count:
        for record in sorted(records, key=stable_key):
            unique.setdefault(int(record["itemId"]), record)
            if len(unique) >= count:
                break
    return list(unique.values())[:count]


def main() -> int:
    args = parse_args()
    header, records = read_records(args.manifest.resolve())
    selected: dict[int, dict[str, Any]] = {}
    reasons: dict[int, set[str]] = {}

    def add(record: dict[str, Any], reason: str) -> None:
        item_id = int(record["itemId"])
        selected[item_id] = record
        reasons.setdefault(item_id, set()).add(reason)

    for language in ("en", "fr"):
        candidates = [value for value in records if value.get("expectedLanguage") == language]
        for record in stratified_pick(candidates, args.per_language):
            add(record, f"{language}-era-size-stratified")

    unlabeled = [value for value in records if value.get("languageLabelStatus") == "unlabeled"]
    for record in stratified_pick(unlabeled, args.unlabeled):
        add(record, "semantic-unlabeled")

    for record in records:
        if record.get("over20MiB"):
            add(record, "oversized")
        if str(record.get("documentId")) in set(args.include_document_id):
            add(record, "required-document")

    output_records = []
    for item_id, record in selected.items():
        output_records.append({**record, "selectionReasons": sorted(reasons[item_id])})
    output_records.sort(key=lambda value: (str(value.get("expectedLanguage") or "zz"), str(value["name"])))

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".new")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(
            json.dumps(
                {
                    "recordType": "parserCandidateSelection",
                    "sourceManifestGeneratedUtc": header.get("generatedUtc"),
                    "requestedPerLanguage": args.per_language,
                    "requestedUnlabeled": args.unlabeled,
                    "selectedCount": len(output_records),
                },
                ensure_ascii=False,
                separators=(",", ":"),
            )
            + "\n"
        )
        for record in output_records:
            stream.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")
    temporary.replace(output)
    counts = {
        language: sum(1 for value in output_records if value.get("expectedLanguage") == language)
        for language in ("en", "fr")
    }
    counts["unlabeled"] = sum(1 for value in output_records if not value.get("expectedLanguage"))
    print(json.dumps({"selectedCount": len(output_records), "counts": counts, "output": str(output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
