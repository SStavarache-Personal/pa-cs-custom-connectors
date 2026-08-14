#!/usr/bin/env python3
"""Run ProductMonographParser twice and publish a deterministic smoke report."""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path


def read_jsonl(path: Path, include_headers: bool = False) -> list[dict]:
    rows: list[dict] = []
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            if not line.strip():
                continue
            row = json.loads(line)
            if not include_headers and row.get("recordType"):
                continue
            rows.append(row)
    return rows


def last_by_name(path: Path) -> dict[str, dict]:
    if not path.exists():
        return {}
    return {row["name"].lower(): row for row in read_jsonl(path) if row.get("name")}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def append_jsonl(path: Path, row: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")


def run_once(runner: Path, pages: Path, output: Path, source_url: str | None, timeout: int) -> tuple[subprocess.CompletedProcess, int]:
    output.parent.mkdir(parents=True, exist_ok=True)
    command = ["dotnet", str(runner.resolve()), str(pages.resolve()), str(output.resolve())]
    if source_url:
        command.append(source_url)
    started = time.perf_counter()
    completed = subprocess.run(command, capture_output=True, text=True, timeout=timeout, check=False)
    return completed, round((time.perf_counter() - started) * 1000)


def section_status(parsed: dict, *path: str) -> str | None:
    value: object = parsed
    for key in path:
        if not isinstance(value, dict):
            return None
        value = value.get(key)
    return value if isinstance(value, str) else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--selection", type=Path, required=True)
    parser.add_argument("--extraction-results", type=Path, required=True)
    parser.add_argument("--markdown-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--runner", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=int, default=110)
    args = parser.parse_args()

    selection = read_jsonl(args.selection)
    extracted = last_by_name(args.extraction_results)
    latest: dict[str, dict] = {}

    for index, item in enumerate(selection, 1):
        name = item["name"]
        key = name.lower()
        extraction = extracted.get(key)
        result = {
            "name": name,
            "documentId": item.get("documentId"),
            "expectedLanguage": item.get("expectedLanguage"),
        }

        if not extraction:
            result["terminalStatus"] = "missingExtractionDisposition"
        elif extraction.get("terminalStatus") != "extracted":
            result["terminalStatus"] = "upstream:" + str(extraction.get("terminalStatus"))
        else:
            page_path = args.markdown_dir / (Path(name).stem + ".json")
            first = args.output_dir / "first" / (Path(name).stem + ".json")
            second = args.output_dir / "second" / (Path(name).stem + ".json")
            urls = item.get("healthCanadaUrls") or []
            source_url = urls[0] if urls else None
            try:
                first_run, first_ms = run_once(args.runner, page_path, first, source_url, args.timeout_seconds)
                second_run, second_ms = run_once(args.runner, page_path, second, source_url, args.timeout_seconds)
                result.update(firstElapsedMilliseconds=first_ms, secondElapsedMilliseconds=second_ms)
                if first_run.returncode != 0 or second_run.returncode != 0 or not first.exists() or not second.exists():
                    result.update(
                        terminalStatus="parserError",
                        firstReturnCode=first_run.returncode,
                        secondReturnCode=second_run.returncode,
                        error=(first_run.stderr + "\n" + second_run.stderr)[-4000:],
                    )
                else:
                    first_hash = sha256(first)
                    second_hash = sha256(second)
                    parsed = json.loads(first.read_text(encoding="utf-8"))
                    detected = (parsed.get("language") or {}).get("value")
                    expected = item.get("expectedLanguage")
                    if expected not in ("en", "fr"):
                        language_disposition = "notLabeled"
                    elif detected not in ("en", "fr"):
                        language_disposition = "abstained"
                    elif detected == expected:
                        language_disposition = "matched"
                    else:
                        language_disposition = "falseAccepted"
                    result.update(
                        terminalStatus=str(parsed.get("documentStatus") or "missingDocumentStatus"),
                        deterministic=first_hash == second_hash,
                        responseSha256=first_hash,
                        sourceUrlPreserved=parsed.get("sourceUrl") == source_url,
                        detectedLanguage=detected,
                        languageMatchesExpected=None if expected not in ("en", "fr") else detected == expected,
                        languageDisposition=language_disposition,
                        controlStatus=(parsed.get("controlNumber") or {}).get("status"),
                        templateFamily=(parsed.get("template") or {}).get("family"),
                        initialAuthorizationStatus=((parsed.get("dates") or {}).get("initialAuthorization") or {}).get("status"),
                        revisionStatus=((parsed.get("dates") or {}).get("revision") or {}).get("status"),
                        sectionStatuses={
                            "indications": section_status(parsed, "sections", "indications", "status"),
                            "pediatricsPrimary": section_status(parsed, "sections", "pediatrics", "primary", "status"),
                            "geriatricsPrimary": section_status(parsed, "sections", "geriatrics", "primary", "status"),
                            "contraindications": section_status(parsed, "sections", "contraindications", "status"),
                        },
                    )
            except subprocess.TimeoutExpired:
                result["terminalStatus"] = "parserTimeout"
            except Exception as exc:  # keep every document terminally classified
                result.update(terminalStatus="harnessError", error=str(exc))

        append_jsonl(args.results, result)
        latest[key] = result
        print(json.dumps({"index": index, "total": len(selection), "name": name, "status": result["terminalStatus"]}))

    rows = [latest[item["name"].lower()] for item in selection]
    terminal_counts = collections.Counter(row["terminalStatus"] for row in rows)
    comparable = [row for row in rows if row.get("languageDisposition") in {"matched", "falseAccepted", "abstained"}]
    accepted_languages = [row for row in comparable if row.get("languageDisposition") in {"matched", "falseAccepted"}]
    report = {
        "generatedUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "selection": str(args.selection).replace("\\", "/"),
        "sampleSize": len(rows),
        "terminalStatusCounts": dict(sorted(terminal_counts.items())),
        "explicitTerminalDispositionCount": sum(bool(row.get("terminalStatus")) for row in rows),
        "deterministicRepeat": {
            "evaluated": sum(row.get("deterministic") is not None for row in rows),
            "matched": sum(row.get("deterministic") is True for row in rows),
        },
        "semanticLanguageComparison": {
            "evaluated": len(comparable),
            "accepted": len(accepted_languages),
            "matched": sum(row.get("languageDisposition") == "matched" for row in comparable),
            "falseAccepted": [row["name"] for row in comparable if row.get("languageDisposition") == "falseAccepted"],
            "abstained": [row["name"] for row in comparable if row.get("languageDisposition") == "abstained"],
            "acceptedPrecision": (
                sum(row.get("languageDisposition") == "matched" for row in accepted_languages) / len(accepted_languages)
                if accepted_languages else None
            ),
        },
        "sourceUrlPreservation": {
            "evaluated": sum(row.get("sourceUrlPreserved") is not None for row in rows),
            "matched": sum(row.get("sourceUrlPreserved") is True for row in rows),
        },
        "unhandledExceptions": sum(row["terminalStatus"] == "harnessError" for row in rows),
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
    return 0 if report["unhandledExceptions"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
