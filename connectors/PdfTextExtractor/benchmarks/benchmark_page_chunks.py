#!/usr/bin/env python3
"""Benchmark page-chunk JSON and metadata against PyMuPDF4LLM page_chunks."""

from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import sys
import time

import pymupdf4llm

from benchmark import (
    CUSTOM_CODE_TIMEOUT_SECONDS,
    FIXTURES,
    MINIMUM_TOKEN_F1,
    MINIMUM_TOKEN_SEQUENCE_RATIO,
    compare,
    get_fixture,
)

MINIMUM_PAGE_TOKEN_F1 = 0.85
MINIMUM_MEAN_PAGE_TOKEN_F1 = 0.97


def parse_args() -> argparse.Namespace:
    repo_root = pathlib.Path(__file__).resolve().parents[3]
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--fixture",
        choices=["all", *[fixture["id"] for fixture in FIXTURES]],
        default="all",
    )
    parser.add_argument(
        "--output-dir",
        type=pathlib.Path,
        default=repo_root / "tmp" / "pdf-page-chunk-benchmark",
    )
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--runner-dll", type=pathlib.Path, required=True)
    return parser.parse_args()


def run_connector(
    args: argparse.Namespace,
    repo_root: pathlib.Path,
    pdf_path: pathlib.Path,
    output_path: pathlib.Path,
) -> dict:
    command = [
        args.dotnet,
        str(args.runner_dll.resolve()),
        str(pdf_path),
        "true",
        str(output_path),
        "1",
        "null",
        str(6 * 1024 * 1024),
        "ExtractPdfPageChunks",
        pdf_path.name,
    ]
    completed = subprocess.run(
        command,
        cwd=repo_root,
        text=True,
        capture_output=True,
        timeout=CUSTOM_CODE_TIMEOUT_SECONDS,
        check=False,
    )
    if completed.returncode:
        raise RuntimeError(
            f"C# page-chunk runner failed ({completed.returncode}):\n"
            f"{completed.stdout}\n{completed.stderr}"
        )
    diagnostic_lines = [line for line in completed.stderr.splitlines() if line.startswith("{")]
    return json.loads(diagnostic_lines[-1]) if diagnostic_lines else {}


def write_markdown(chunks: list[dict], path: pathlib.Path) -> None:
    pages = []
    for index, chunk in enumerate(chunks):
        if index:
            pages.append(f"\n\n--- Page {index + 1} ---\n\n")
        pages.append(chunk.get("text", ""))
    path.write_text("".join(pages), encoding="utf-8")


def validate_metadata(
    chunks: list[dict],
    reference_chunks: list[dict],
    fixture: dict[str, str],
    fixture_bytes: int,
) -> tuple[bool, list[str]]:
    errors = []
    expected_pages = len(chunks)
    for index, chunk in enumerate(chunks):
        metadata = chunk.get("metadata") or {}
        reference_metadata = (
            reference_chunks[index].get("metadata") or {}
            if index < len(reference_chunks)
            else {}
        )
        page = chunk.get("page") or {}
        if metadata.get("pageNumber") != index + 1:
            errors.append(f"page {index + 1}: pageNumber mismatch")
        if metadata.get("pageCount") != expected_pages:
            errors.append(f"page {index + 1}: pageCount mismatch")
        if metadata.get("fileName") != f"{fixture['id']}.pdf":
            errors.append(f"page {index + 1}: fileName mismatch")
        if metadata.get("fileSizeBytes") != fixture_bytes:
            errors.append(f"page {index + 1}: fileSizeBytes mismatch")
        if not str(metadata.get("format", "")).startswith("PDF "):
            errors.append(f"page {index + 1}: PDF format missing")
        metadata_pairs = (
            ("format", "format"),
            ("title", "title"),
            ("author", "author"),
            ("subject", "subject"),
            ("keywords", "keywords"),
            ("creator", "creator"),
            ("producer", "producer"),
            ("creationDate", "creationDate"),
            ("modificationDate", "modDate"),
            ("trapped", "trapped"),
        )
        for candidate_name, reference_name in metadata_pairs:
            candidate_value = metadata.get(candidate_name) or ""
            reference_value = reference_metadata.get(reference_name) or ""
            if candidate_value != reference_value:
                errors.append(
                    f"page {index + 1}: {candidate_name} metadata mismatch"
                )
        if chunk.get("contentType") != "text/markdown":
            errors.append(f"page {index + 1}: contentType mismatch")
        if chunk.get("characterCount") != len(chunk.get("text", "")):
            errors.append(f"page {index + 1}: characterCount mismatch")
        if not isinstance(page.get("width"), (int, float)) or page.get("width", 0) <= 0:
            errors.append(f"page {index + 1}: invalid width")
        if not isinstance(page.get("height"), (int, float)) or page.get("height", 0) <= 0:
            errors.append(f"page {index + 1}: invalid height")
    return not errors, errors


def benchmark_fixture(
    args: argparse.Namespace,
    repo_root: pathlib.Path,
    fixture: dict[str, str],
) -> dict:
    pdf_path = get_fixture(fixture, args.output_dir)
    candidate_json_path = args.output_dir / f"{fixture['id']}.connector.page-chunks.json"
    reference_json_path = args.output_dir / f"{fixture['id']}.pymupdf4llm.page-chunks.json"
    candidate_markdown_path = args.output_dir / f"{fixture['id']}.connector.md"
    reference_markdown_path = args.output_dir / f"{fixture['id']}.pymupdf4llm.md"

    started = time.perf_counter()
    connector_diagnostics = run_connector(
        args, repo_root, pdf_path, candidate_json_path
    )
    connector_seconds = time.perf_counter() - started
    candidate_chunks = json.loads(candidate_json_path.read_text(encoding="utf-8"))

    started = time.perf_counter()
    reference_chunks = pymupdf4llm.to_markdown(
        str(pdf_path),
        page_chunks=True,
        use_ocr=False,
        header=True,
        footer=True,
        table_format="grid",
    )
    reference_seconds = time.perf_counter() - started
    reference_json_path.write_text(
        json.dumps(reference_chunks, indent=2, ensure_ascii=False, default=str) + "\n",
        encoding="utf-8",
    )
    write_markdown(candidate_chunks, candidate_markdown_path)
    write_markdown(reference_chunks, reference_markdown_path)

    page_count_matches = len(candidate_chunks) == len(reference_chunks)
    compared_pages = min(len(candidate_chunks), len(reference_chunks))
    per_page = [
        {
            "pageNumber": index + 1,
            **compare(
                candidate_chunks[index].get("text", ""),
                reference_chunks[index].get("text", ""),
            ),
        }
        for index in range(compared_pages)
    ]
    aggregate_metrics = compare(
        "\n".join(chunk.get("text", "") for chunk in candidate_chunks),
        "\n".join(chunk.get("text", "") for chunk in reference_chunks),
    )
    metadata_valid, metadata_errors = validate_metadata(
        candidate_chunks, reference_chunks, fixture, pdf_path.stat().st_size
    )
    minimum_page_token_f1 = min(
        (page["tokenF1"] for page in per_page), default=0.0
    )
    mean_page_token_f1 = (
        sum(page["tokenF1"] for page in per_page) / len(per_page)
        if per_page
        else 0.0
    )
    passed = (
        page_count_matches
        and metadata_valid
        and aggregate_metrics["tokenF1"] >= MINIMUM_TOKEN_F1
        and aggregate_metrics["tokenSequenceRatio"] >= MINIMUM_TOKEN_SEQUENCE_RATIO
        and minimum_page_token_f1 >= MINIMUM_PAGE_TOKEN_F1
        and mean_page_token_f1 >= MINIMUM_MEAN_PAGE_TOKEN_F1
        and connector_seconds < CUSTOM_CODE_TIMEOUT_SECONDS
    )
    return {
        "fixture": fixture,
        "fixtureBytes": pdf_path.stat().st_size,
        "connectorSeconds": connector_seconds,
        "referenceSeconds": reference_seconds,
        "connectorDiagnostics": connector_diagnostics,
        "candidatePageChunks": len(candidate_chunks),
        "referencePageChunks": len(reference_chunks),
        "pageCountMatches": page_count_matches,
        "metadataValid": metadata_valid,
        "metadataErrors": metadata_errors,
        "aggregateMetrics": aggregate_metrics,
        "minimumPageTokenF1": minimum_page_token_f1,
        "meanPageTokenF1": mean_page_token_f1,
        "perPageMetrics": per_page,
        "outputs": {
            "connectorJson": str(candidate_json_path),
            "pymupdf4llmJson": str(reference_json_path),
            "connectorMarkdown": str(candidate_markdown_path),
            "pymupdf4llmMarkdown": str(reference_markdown_path),
        },
        "passed": passed,
    }


def main() -> int:
    args = parse_args()
    repo_root = pathlib.Path(__file__).resolve().parents[3]
    args.output_dir.mkdir(parents=True, exist_ok=True)
    selected = (
        FIXTURES
        if args.fixture == "all"
        else tuple(item for item in FIXTURES if item["id"] == args.fixture)
    )
    results = [benchmark_fixture(args, repo_root, fixture) for fixture in selected]
    report = {
        "pymupdf4llmVersion": getattr(pymupdf4llm, "__version__", "unknown"),
        "thresholds": {
            "minimumAggregateTokenF1": MINIMUM_TOKEN_F1,
            "minimumAggregateTokenSequenceRatio": MINIMUM_TOKEN_SEQUENCE_RATIO,
            "minimumPageTokenF1": MINIMUM_PAGE_TOKEN_F1,
            "minimumMeanPageTokenF1": MINIMUM_MEAN_PAGE_TOKEN_F1,
            "maximumConnectorSeconds": CUSTOM_CODE_TIMEOUT_SECONDS,
        },
        "fixtures": results,
        "passed": all(result["passed"] for result in results),
    }
    result_path = args.output_dir / "page-chunk-benchmark-results.json"
    result_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
