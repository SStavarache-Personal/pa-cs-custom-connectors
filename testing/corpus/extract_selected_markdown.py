#!/usr/bin/env python3
"""Run the unchanged PdfTextExtractor local runner over a selected corpus.

PDFs and Markdown stay below an ignored tmp directory. The JSONL result is
append-only and resumable; each selected document receives a terminal status.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import time
from pathlib import Path


EXISTING_EXTRACTOR_LIMIT = 32 * 1024 * 1024


def read_jsonl(path: Path) -> list[dict]:
    rows: list[dict] = []
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            if not line.strip():
                continue
            row = json.loads(line)
            if row.get("recordType"):
                continue
            rows.append(row)
    return rows


def read_last_results(path: Path) -> dict[str, dict]:
    if not path.exists():
        return {}
    return {row["name"].lower(): row for row in read_jsonl(path) if row.get("name")}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def parse_last_json_line(value: str) -> dict | None:
    for line in reversed(value.splitlines()):
        try:
            parsed = json.loads(line)
            if isinstance(parsed, dict):
                return parsed
        except json.JSONDecodeError:
            pass
    return None


def append_result(path: Path, row: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--selection", type=Path, required=True)
    parser.add_argument("--pdf-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--runner", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=int, default=110)
    args = parser.parse_args()

    selection = read_jsonl(args.selection)
    previous = read_last_results(args.results)
    args.output_dir.mkdir(parents=True, exist_ok=True)

    for index, item in enumerate(selection, 1):
        name = item["name"]
        key = name.lower()
        pdf_path = args.pdf_dir / name
        output_path = args.output_dir / (Path(name).stem + ".json")

        cached = previous.get(key)
        if cached and cached.get("terminalStatus") == "extracted" and output_path.exists():
            if cached.get("outputSha256") == sha256(output_path):
                print(json.dumps({"index": index, "total": len(selection), "name": name, "status": "cached"}))
                continue

        started = time.perf_counter()
        result = {
            "name": name,
            "documentId": item.get("documentId"),
            "expectedLanguage": item.get("expectedLanguage"),
            "lengthBytes": item.get("lengthBytes"),
        }
        if not pdf_path.exists():
            result.update(terminalStatus="missingPdf", elapsedMilliseconds=0)
        elif pdf_path.stat().st_size != item.get("lengthBytes"):
            result.update(
                terminalStatus="sizeMismatch",
                actualLengthBytes=pdf_path.stat().st_size,
                elapsedMilliseconds=0,
            )
        elif pdf_path.stat().st_size > EXISTING_EXTRACTOR_LIMIT:
            result.update(
                terminalStatus="requiresOcr",
                reason="The unchanged PdfTextExtractor has a 32 MiB decoded-input limit.",
                pdfSha256=sha256(pdf_path),
                elapsedMilliseconds=0,
            )
        else:
            command = [
                "dotnet",
                str(args.runner.resolve()),
                str(pdf_path.resolve()),
                "false",
                str(output_path.resolve()),
                "1",
                "null",
                str(6 * 1024 * 1024),
                "ExtractPdfMarkdownPageChunks",
                name,
            ]
            try:
                completed = subprocess.run(
                    command,
                    capture_output=True,
                    text=True,
                    timeout=args.timeout_seconds,
                    check=False,
                )
                elapsed = round((time.perf_counter() - started) * 1000)
                runner_summary = parse_last_json_line(completed.stderr)
                error_code = ((runner_summary or {}).get("error") or {}).get("code")
                terminal_status = "extracted" if completed.returncode == 0 and output_path.exists() else "extractorError"
                if error_code in {"PDF_TOO_LARGE", "PDF_RESOURCE_LIMIT"}:
                    terminal_status = "requiresOcr"
                result.update(
                    terminalStatus=terminal_status,
                    returnCode=completed.returncode,
                    elapsedMilliseconds=elapsed,
                    pdfSha256=sha256(pdf_path),
                    runnerSummary=runner_summary,
                )
                if completed.returncode == 0 and output_path.exists():
                    result["outputSha256"] = sha256(output_path)
                else:
                    result["error"] = completed.stderr[-4000:]
            except subprocess.TimeoutExpired as exc:
                result.update(
                    terminalStatus="timeout",
                    elapsedMilliseconds=round((time.perf_counter() - started) * 1000),
                    error=(exc.stderr or "")[-4000:] if isinstance(exc.stderr, str) else "",
                )

        append_result(args.results, result)
        previous[key] = result
        print(json.dumps({"index": index, "total": len(selection), "name": name, "status": result["terminalStatus"]}))

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
