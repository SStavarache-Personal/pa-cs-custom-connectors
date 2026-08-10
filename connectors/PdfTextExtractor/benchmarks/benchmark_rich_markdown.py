#!/usr/bin/env python3
"""Benchmark rich Markdown and tagged-table reconstruction against PyMuPDF4LLM."""

from __future__ import annotations

import argparse
import collections
import difflib
import json
import pathlib
import re
import subprocess
import sys
import time
import unicodedata

import pymupdf4llm

from benchmark import CUSTOM_CODE_TIMEOUT_SECONDS, FIXTURES, get_fixture
from benchmark_page_chunks import validate_metadata, write_markdown


MINIMUM_DOCUMENT_TOKEN_F1 = 0.97
MINIMUM_DOCUMENT_SEQUENCE_RATIO = 0.94
MINIMUM_MEAN_PAGE_TOKEN_F1 = 0.96
MINIMUM_TABLE_TOKEN_F1 = 0.88
MINIMUM_MEAN_MATCHED_CELL_F1 = 0.70


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
        default=repo_root / "tmp" / "pdf-rich-markdown-benchmark",
    )
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--runner-dll", type=pathlib.Path, required=True)
    return parser.parse_args()


def normalized_tokens(text: str) -> list[str]:
    text = unicodedata.normalize("NFKC", text).casefold()
    text = re.sub(r"---\s*page\s+\d+\s*---", " ", text)
    text = re.sub(
        r"</?(?:br|sup|sub|u|strong|em)(?:\s[^>]*)?/?>",
        " ",
        text,
        flags=re.IGNORECASE,
    )
    text = re.sub(r"[\\*_`#~]", "", text)
    return re.findall(r"[^\W_]+(?:[-'’][^\W_]+)*", text, flags=re.UNICODE)


def compare(candidate: str, reference: str) -> dict[str, float | int]:
    candidate_tokens = normalized_tokens(candidate)
    reference_tokens = normalized_tokens(reference)
    candidate_counts = collections.Counter(candidate_tokens)
    reference_counts = collections.Counter(reference_tokens)
    matching = sum((candidate_counts & reference_counts).values())
    precision = matching / len(candidate_tokens) if candidate_tokens else 0.0
    recall = matching / len(reference_tokens) if reference_tokens else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    sequence_ratio = difflib.SequenceMatcher(
        None, candidate_tokens, reference_tokens, autojunk=False
    ).ratio()
    return {
        "candidateTokens": len(candidate_tokens),
        "referenceTokens": len(reference_tokens),
        "matchingTokens": matching,
        "tokenPrecision": precision,
        "tokenRecall": recall,
        "tokenF1": f1,
        "tokenSequenceRatio": sequence_ratio,
    }


def run_connector(
    args: argparse.Namespace,
    repo_root: pathlib.Path,
    pdf_path: pathlib.Path,
    output_path: pathlib.Path,
    operation_id: str,
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
        operation_id,
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
            f"C# rich-Markdown runner failed ({completed.returncode}):\n"
            f"{completed.stdout}\n{completed.stderr}"
        )
    diagnostics = [line for line in completed.stderr.splitlines() if line.startswith("{")]
    return json.loads(diagnostics[-1]) if diagnostics else {}


def split_table_row(line: str) -> list[str]:
    line = line.strip()
    if line.startswith("|"):
        line = line[1:]
    if line.endswith("|"):
        line = line[:-1]
    cells: list[str] = []
    current: list[str] = []
    escaped = False
    for character in line:
        if escaped:
            current.append(character)
            escaped = False
        elif character == "\\":
            current.append(character)
            escaped = True
        elif character == "|":
            cells.append("".join(current).strip())
            current = []
        else:
            current.append(character)
    cells.append("".join(current).strip())
    return cells


def parse_markdown_tables(text: str) -> list[list[list[str]]]:
    tables: list[list[list[str]]] = []
    current: list[str] = []
    for line in [*text.splitlines(), ""]:
        if line.strip().startswith("|"):
            current.append(line.strip())
            continue
        if len(current) >= 2:
            parsed = [split_table_row(row) for row in current]
            separator = parsed[1] if len(parsed) > 1 else []
            if separator and all(re.fullmatch(r":?-{3,}:?", cell.strip()) for cell in separator):
                tables.append([parsed[0], *parsed[2:]])
        current = []
    return tables


def table_text(table: list[list[str]]) -> str:
    return "\n".join("\t".join(row) for row in table)


def greedy_pairs(
    candidate_tables: list[list[list[str]]],
    reference_tables: list[list[list[str]]],
) -> list[tuple[int, int, dict[str, float | int]]]:
    scores = sorted(
        (
            compare(table_text(candidate), table_text(reference))["tokenF1"],
            candidate_index,
            reference_index,
        )
        for candidate_index, candidate in enumerate(candidate_tables)
        for reference_index, reference in enumerate(reference_tables)
    )
    used_candidate: set[int] = set()
    used_reference: set[int] = set()
    pairs = []
    for score, candidate_index, reference_index in reversed(scores):
        if score < 0.25:
            break
        if candidate_index in used_candidate or reference_index in used_reference:
            continue
        used_candidate.add(candidate_index)
        used_reference.add(reference_index)
        pairs.append(
            (
                candidate_index,
                reference_index,
                compare(
                    table_text(candidate_tables[candidate_index]),
                    table_text(reference_tables[reference_index]),
                ),
            )
        )
    return pairs


def cell_similarity(
    candidate_table: list[list[str]], reference_table: list[list[str]]
) -> tuple[list[float], int, int]:
    candidate_cells = [cell for row in candidate_table for cell in row if normalized_tokens(cell)]
    reference_cells = [cell for row in reference_table for cell in row if normalized_tokens(cell)]
    scores = sorted(
        (
            compare(candidate, reference)["tokenF1"],
            candidate_index,
            reference_index,
        )
        for candidate_index, candidate in enumerate(candidate_cells)
        for reference_index, reference in enumerate(reference_cells)
    )
    used_candidate: set[int] = set()
    used_reference: set[int] = set()
    matched: list[float] = []
    for score, candidate_index, reference_index in reversed(scores):
        if score < 0.25:
            break
        if candidate_index in used_candidate or reference_index in used_reference:
            continue
        used_candidate.add(candidate_index)
        used_reference.add(reference_index)
        matched.append(score)
    return matched, len(candidate_cells), len(reference_cells)


def compare_tables(
    candidate_chunks: list[dict], reference_chunks: list[dict]
) -> dict[str, float | int]:
    all_pairs = []
    candidate_segment_count = 0
    reference_segment_count = 0
    cell_scores: list[float] = []
    candidate_cells = 0
    reference_cells = 0
    matched_candidate_text: list[str] = []
    matched_reference_text: list[str] = []
    row_ratios: list[float] = []
    column_ratios: list[float] = []
    for page_index in range(min(len(candidate_chunks), len(reference_chunks))):
        candidate_tables = parse_markdown_tables(candidate_chunks[page_index].get("text", ""))
        reference_tables = parse_markdown_tables(reference_chunks[page_index].get("text", ""))
        candidate_segment_count += len(candidate_tables)
        reference_segment_count += len(reference_tables)
        for candidate_index, reference_index, metrics in greedy_pairs(
            candidate_tables, reference_tables
        ):
            candidate_table = candidate_tables[candidate_index]
            reference_table = reference_tables[reference_index]
            all_pairs.append(metrics)
            matched_candidate_text.append(table_text(candidate_table))
            matched_reference_text.append(table_text(reference_table))
            row_ratios.append(
                min(len(candidate_table), len(reference_table))
                / max(len(candidate_table), len(reference_table), 1)
            )
            candidate_columns = max((len(row) for row in candidate_table), default=0)
            reference_columns = max((len(row) for row in reference_table), default=0)
            column_ratios.append(
                min(candidate_columns, reference_columns)
                / max(candidate_columns, reference_columns, 1)
            )
            matched_cells, candidate_count, reference_count = cell_similarity(
                candidate_table, reference_table
            )
            cell_scores.extend(matched_cells)
            candidate_cells += candidate_count
            reference_cells += reference_count
    aggregate = compare("\n".join(matched_candidate_text), "\n".join(matched_reference_text))
    return {
        "candidateTableSegments": candidate_segment_count,
        "referenceTableSegments": reference_segment_count,
        "matchedTableSegments": len(all_pairs),
        "candidateSegmentMatchRate": len(all_pairs) / candidate_segment_count
        if candidate_segment_count
        else 0.0,
        "referenceSegmentCoverage": len(all_pairs) / reference_segment_count
        if reference_segment_count
        else 0.0,
        "matchedTableTokenF1": aggregate["tokenF1"],
        "meanMatchedTableTokenF1": sum(pair["tokenF1"] for pair in all_pairs) / len(all_pairs)
        if all_pairs
        else 0.0,
        "meanRowCountSimilarity": sum(row_ratios) / len(row_ratios) if row_ratios else 0.0,
        "meanColumnCountSimilarity": sum(column_ratios) / len(column_ratios)
        if column_ratios
        else 0.0,
        "matchedCells": len(cell_scores),
        "candidateNonemptyCells": candidate_cells,
        "referenceNonemptyCells": reference_cells,
        "meanMatchedCellTokenF1": sum(cell_scores) / len(cell_scores) if cell_scores else 0.0,
    }


def benchmark_fixture(
    args: argparse.Namespace,
    repo_root: pathlib.Path,
    fixture: dict[str, str],
) -> dict:
    pdf_path = get_fixture(fixture, args.output_dir)
    prefix = args.output_dir / fixture["id"]
    candidate_markdown_path = pathlib.Path(str(prefix) + ".connector.rich.md")
    candidate_chunks_path = pathlib.Path(str(prefix) + ".connector.rich.page-chunks.json")
    reference_markdown_path = pathlib.Path(str(prefix) + ".pymupdf4llm.rich.md")
    reference_chunks_path = pathlib.Path(str(prefix) + ".pymupdf4llm.rich.page-chunks.json")

    started = time.perf_counter()
    full_diagnostics = run_connector(
        args, repo_root, pdf_path, candidate_markdown_path, "ExtractPdfMarkdown"
    )
    full_seconds = time.perf_counter() - started
    started = time.perf_counter()
    chunk_diagnostics = run_connector(
        args,
        repo_root,
        pdf_path,
        candidate_chunks_path,
        "ExtractPdfMarkdownPageChunks",
    )
    chunk_seconds = time.perf_counter() - started

    started = time.perf_counter()
    reference_markdown = pymupdf4llm.to_markdown(
        str(pdf_path),
        use_ocr=False,
        header=True,
        footer=True,
        table_format="grid",
    )
    reference_chunks = pymupdf4llm.to_markdown(
        str(pdf_path),
        page_chunks=True,
        use_ocr=False,
        header=True,
        footer=True,
        table_format="grid",
    )
    reference_seconds = time.perf_counter() - started
    reference_markdown_path.write_text(reference_markdown, encoding="utf-8")
    reference_chunks_path.write_text(
        json.dumps(reference_chunks, indent=2, ensure_ascii=False, default=str) + "\n",
        encoding="utf-8",
    )

    candidate_markdown = candidate_markdown_path.read_text(encoding="utf-8")
    candidate_chunks = json.loads(candidate_chunks_path.read_text(encoding="utf-8"))
    page_count_matches = len(candidate_chunks) == len(reference_chunks)
    metadata_valid, metadata_errors = validate_metadata(
        candidate_chunks, reference_chunks, fixture, pdf_path.stat().st_size
    )
    document_metrics = compare(candidate_markdown, reference_markdown)
    per_page = [
        compare(candidate.get("text", ""), reference.get("text", ""))
        for candidate, reference in zip(candidate_chunks, reference_chunks)
    ]
    mean_page_f1 = (
        sum(page["tokenF1"] for page in per_page) / len(per_page) if per_page else 0.0
    )
    minimum_page_f1 = min((page["tokenF1"] for page in per_page), default=0.0)
    table_metrics = compare_tables(candidate_chunks, reference_chunks)
    feature_counts = {
        "candidateHeadings": sum(
            bool(re.match(r"^#{1,6}\s+", line)) for line in candidate_markdown.splitlines()
        ),
        "referenceHeadings": sum(
            bool(re.match(r"^#{1,6}\s+", line)) for line in reference_markdown.splitlines()
        ),
        "candidateListItems": sum(
            bool(re.match(r"^(?:[-*+] |\d+\. )", line))
            for line in candidate_markdown.splitlines()
        ),
        "referenceListItems": sum(
            bool(re.match(r"^(?:[-*+] |\d+\. )", line))
            for line in reference_markdown.splitlines()
        ),
        "candidateBoldRuns": candidate_markdown.count("**") // 2,
        "referenceBoldRuns": reference_markdown.count("**") // 2,
    }
    passed = (
        page_count_matches
        and metadata_valid
        and full_seconds < CUSTOM_CODE_TIMEOUT_SECONDS
        and chunk_seconds < CUSTOM_CODE_TIMEOUT_SECONDS
        and document_metrics["tokenF1"] >= MINIMUM_DOCUMENT_TOKEN_F1
        and document_metrics["tokenSequenceRatio"] >= MINIMUM_DOCUMENT_SEQUENCE_RATIO
        and mean_page_f1 >= MINIMUM_MEAN_PAGE_TOKEN_F1
        and table_metrics["matchedTableTokenF1"] >= MINIMUM_TABLE_TOKEN_F1
        and table_metrics["meanMatchedCellTokenF1"] >= MINIMUM_MEAN_MATCHED_CELL_F1
        and feature_counts["candidateHeadings"] > 0
        and feature_counts["candidateBoldRuns"] > 0
        and table_metrics["candidateTableSegments"] > 0
    )
    return {
        "fixture": fixture,
        "fixtureBytes": pdf_path.stat().st_size,
        "fullConnectorSeconds": full_seconds,
        "pageChunkConnectorSeconds": chunk_seconds,
        "referenceSeconds": reference_seconds,
        "fullConnectorDiagnostics": full_diagnostics,
        "pageChunkConnectorDiagnostics": chunk_diagnostics,
        "pageCountMatches": page_count_matches,
        "metadataValid": metadata_valid,
        "metadataErrors": metadata_errors,
        "documentMetrics": document_metrics,
        "minimumPageTokenF1": minimum_page_f1,
        "meanPageTokenF1": mean_page_f1,
        "tableMetrics": table_metrics,
        "featureCounts": feature_counts,
        "outputs": {
            "connectorMarkdown": str(candidate_markdown_path),
            "connectorPageChunks": str(candidate_chunks_path),
            "pymupdf4llmMarkdown": str(reference_markdown_path),
            "pymupdf4llmPageChunks": str(reference_chunks_path),
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
        else tuple(fixture for fixture in FIXTURES if fixture["id"] == args.fixture)
    )
    results = [benchmark_fixture(args, repo_root, fixture) for fixture in selected]
    report = {
        "pymupdf4llmVersion": getattr(pymupdf4llm, "__version__", "unknown"),
        "thresholds": {
            "minimumDocumentTokenF1": MINIMUM_DOCUMENT_TOKEN_F1,
            "minimumDocumentTokenSequenceRatio": MINIMUM_DOCUMENT_SEQUENCE_RATIO,
            "minimumMeanPageTokenF1": MINIMUM_MEAN_PAGE_TOKEN_F1,
            "minimumMatchedTableTokenF1": MINIMUM_TABLE_TOKEN_F1,
            "minimumMeanMatchedCellTokenF1": MINIMUM_MEAN_MATCHED_CELL_F1,
            "maximumConnectorSeconds": CUSTOM_CODE_TIMEOUT_SECONDS,
        },
        "fixtures": results,
        "passed": all(result["passed"] for result in results),
    }
    result_path = args.output_dir / "rich-markdown-benchmark-results.json"
    result_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
