#!/usr/bin/env python3
"""Exercise ProductMonographOcr on oversized PDFs and the locked scan target."""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path


def read_jsonl(path: Path) -> list[dict]:
    rows: list[dict] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        if not row.get("recordType"):
            rows.append(row)
    return rows


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def append_jsonl(path: Path, row: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--selection", type=Path, required=True)
    parser.add_argument("--pdf-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--runner", type=Path, required=True)
    parser.add_argument("--oversized-threshold", type=int, default=20 * 1024 * 1024)
    parser.add_argument("--timeout-seconds", type=int, default=110)
    args = parser.parse_args()

    chosen = [
        item for item in read_jsonl(args.selection)
        if item.get("lengthBytes", 0) > args.oversized_threshold or item.get("documentId") == "00003161"
    ]
    args.output_dir.mkdir(parents=True, exist_ok=True)
    final_rows: list[dict] = []

    for index, item in enumerate(chosen, 1):
        name = item["name"]
        pdf = args.pdf_dir / name
        combined_output = args.output_dir / (Path(name).stem + ".json")
        row = {
            "name": name,
            "documentId": item.get("documentId"),
            "lengthBytes": item.get("lengthBytes"),
            "pdfSha256": sha256(pdf) if pdf.exists() else None,
            "invocations": [],
        }
        if not pdf.exists():
            row["terminalStatus"] = "missingPdf"
        else:
            start_page = 1
            pages: list[dict] = []
            completed = False
            error: str | None = None
            source_page_count: int | None = None
            for invocation in range(1, 1001):
                batch_output = args.output_dir / "batches" / f"{Path(name).stem}-{start_page}.json"
                batch_output.parent.mkdir(parents=True, exist_ok=True)
                command = [
                    "dotnet", str(args.runner.resolve()), str(pdf.resolve()), str(batch_output.resolve()),
                    str(start_page), "25", item.get("expectedLanguage") or "auto", "0.86",
                ]
                started = time.perf_counter()
                try:
                    process = subprocess.run(command, capture_output=True, text=True, timeout=args.timeout_seconds, check=False)
                except subprocess.TimeoutExpired:
                    error = "Invocation exceeded the harness timeout."
                    row["invocations"].append({"startPage": start_page, "terminalStatus": "timeout"})
                    break
                wall_ms = round((time.perf_counter() - started) * 1000)
                if process.returncode != 0 or not batch_output.exists():
                    error = process.stderr[-4000:]
                    row["invocations"].append({"startPage": start_page, "terminalStatus": "error", "wallMilliseconds": wall_ms})
                    break
                batch = json.loads(batch_output.read_text(encoding="utf-8"))
                source_page_count = batch.get("sourcePageCount") or batch.get("pageCount")
                batch_pages = batch.get("pages") or []
                pages.extend(batch_pages)
                row["invocations"].append({
                    "startPage": start_page,
                    "pagesReturned": len(batch_pages),
                    "ocrPagesProcessed": (batch.get("processedRange") or {}).get("ocrPagesProcessed"),
                    "connectorMilliseconds": batch.get("elapsedMilliseconds"),
                    "wallMilliseconds": wall_ms,
                    "completed": batch.get("completed"),
                    "nextPage": batch.get("nextPage"),
                })
                if batch.get("completed"):
                    completed = True
                    break
                next_page = batch.get("nextPage")
                if not isinstance(next_page, int) or next_page <= start_page:
                    error = "Continuation did not advance nextPage."
                    break
                start_page = next_page

            page_numbers = [((page.get("metadata") or {}).get("pageNumber")) for page in pages]
            statuses = collections.Counter(str(page.get("ocrStatus")) for page in pages)
            combined = {
                "sourcePageCount": source_page_count,
                "completed": completed,
                "nextPage": None if completed else start_page,
                "pages": pages,
                "warnings": [],
            }
            combined_output.write_text(json.dumps(combined, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            combined_text = "\n".join(str(page.get("text") or "") for page in pages)
            row.update(
                terminalStatus="completed" if completed else "incomplete",
                sourcePageCount=source_page_count,
                pagesReturned=len(pages),
                orderedCompletePages=page_numbers == list(range(1, (source_page_count or 0) + 1)),
                pageStatusCounts=dict(sorted(statuses.items())),
                maximumInvocationMilliseconds=max((call.get("connectorMilliseconds") or 0 for call in row["invocations"]), default=0),
                responseSha256=sha256(combined_output),
                error=error,
            )
            if item.get("documentId") == "00003161":
                row["requiredAnchors"] = {
                    anchor: anchor.casefold() in combined_text.casefold()
                    for anchor in ["MONOGRAPHIE DE PRODUIT", "OXIZOLE", "15 janvier 2004", "089170"]
                }

        append_jsonl(args.results, row)
        final_rows.append(row)
        print(json.dumps({"index": index, "total": len(chosen), "name": name, "status": row["terminalStatus"]}))

    report = {
        "generatedUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "sampleSize": len(final_rows),
        "terminalStatusCounts": dict(sorted(collections.Counter(row["terminalStatus"] for row in final_rows).items())),
        "completedOrderedDocuments": sum(row.get("terminalStatus") == "completed" and row.get("orderedCompletePages") for row in final_rows),
        "maximumInputBytes": max((row.get("lengthBytes") or 0 for row in final_rows), default=0),
        "maximumInvocationMilliseconds": max((row.get("maximumInvocationMilliseconds") or 0 for row in final_rows), default=0),
        "over32MiB": [
            {"name": row["name"], "lengthBytes": row["lengthBytes"], "terminalStatus": row["terminalStatus"]}
            for row in final_rows if (row.get("lengthBytes") or 0) > 32 * 1024 * 1024
        ],
        "scan00003161": next(({
            "terminalStatus": row["terminalStatus"],
            "sourcePageCount": row.get("sourcePageCount"),
            "pageStatusCounts": row.get("pageStatusCounts"),
            "requiredAnchors": row.get("requiredAnchors"),
        } for row in final_rows if row.get("documentId") == "00003161"), None),
        "unhandledExceptions": 0,
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
