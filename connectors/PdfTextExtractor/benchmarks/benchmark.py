#!/usr/bin/env python3
"""Benchmark PdfTextExtractor against PyMuPDF4LLM on pinned PDF fixtures."""

from __future__ import annotations

import argparse
import collections
import difflib
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import time
import unicodedata
import urllib.request

import pymupdf4llm


FIXTURES = (
    {
        "id": "health-canada-auro-irbesartan-00083665",
        "url": "https://pdf.hres.ca/dpd_pm/00083665.PDF",
        "sha256": "193e309f9cbe17bfd3e9e77d9ff3a2e73b1772f4bac4cd2a4001f3bc6243a25e",
        "creator": "Microsoft® Word for Microsoft 365",
        "description": "34-page Health Canada product monograph with tables, bullets, mixed fonts, superscripts, and tagged artifacts.",
    },
    {
        "id": "health-canada-oxaliplatin-00075769",
        "url": "https://pdf.hres.ca/dpd_pm/00075769.PDF",
        "sha256": "0a6e96b2ee9428a320220d90d208b7ee0ded1a419b221f01270885536a7547fe",
        "creator": "Microsoft® Word for Microsoft 365",
        "description": "37-page Health Canada product monograph with dense tables, bullets, symbols, and mixed simple/CID fonts.",
    },
)

MINIMUM_TOKEN_F1 = 0.975
MINIMUM_TOKEN_SEQUENCE_RATIO = 0.94
CUSTOM_CODE_TIMEOUT_SECONDS = 120


def parse_args() -> argparse.Namespace:
    repo_root = pathlib.Path(__file__).resolve().parents[3]
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--fixture",
        choices=["all", *[fixture["id"] for fixture in FIXTURES]],
        default="all",
        help="Pinned fixture to benchmark; defaults to every fixture.",
    )
    parser.add_argument("--output-dir", type=pathlib.Path, default=repo_root / "tmp" / "pdf-text-benchmark")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--runner-dll", type=pathlib.Path)
    return parser.parse_args()


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def get_fixture(fixture: dict[str, str], output_dir: pathlib.Path) -> pathlib.Path:
    fixture_dir = output_dir / "fixtures"
    fixture_dir.mkdir(parents=True, exist_ok=True)
    path = fixture_dir / f"{fixture['id']}.pdf"
    if not path.exists() or sha256(path) != fixture["sha256"]:
        request = urllib.request.Request(fixture["url"], headers={"User-Agent": "PdfTextExtractorBenchmark/1.0"})
        with urllib.request.urlopen(request, timeout=60) as response, path.open("wb") as target:
            while True:
                block = response.read(1024 * 1024)
                if not block:
                    break
                target.write(block)
    actual = sha256(path)
    if actual != fixture["sha256"]:
        raise RuntimeError(f"Fixture hash mismatch: expected {fixture['sha256']}, got {actual}")
    return path


def run_connector(args: argparse.Namespace, repo_root: pathlib.Path, pdf_path: pathlib.Path, output_path: pathlib.Path) -> dict:
    if args.runner_dll:
        command = [args.dotnet, str(args.runner_dll.resolve()), str(pdf_path), "true", str(output_path)]
    else:
        project = repo_root / "testing" / "PdfTextExtractorRunner" / "PdfTextExtractorRunner.csproj"
        command = [args.dotnet, "run", "--project", str(project), "-c", "Release", "--", str(pdf_path), "true", str(output_path)]
    completed = subprocess.run(command, cwd=repo_root, text=True, capture_output=True, timeout=CUSTOM_CODE_TIMEOUT_SECONDS, check=False)
    if completed.returncode:
        raise RuntimeError(f"C# runner failed ({completed.returncode}):\n{completed.stdout}\n{completed.stderr}")
    diagnostic_lines = [line for line in completed.stderr.splitlines() if line.startswith("{")]
    return json.loads(diagnostic_lines[-1]) if diagnostic_lines else {}


def normalized_tokens(text: str) -> list[str]:
    text = unicodedata.normalize("NFKC", text).casefold()
    text = re.sub(r"---\s*page\s+\d+\s*---", " ", text)
    text = re.sub(r"<br\s*/?>", " ", text, flags=re.IGNORECASE)
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
    sequence_ratio = difflib.SequenceMatcher(None, candidate_tokens, reference_tokens, autojunk=False).ratio()
    return {
        "candidateTokens": len(candidate_tokens),
        "referenceTokens": len(reference_tokens),
        "matchingTokens": matching,
        "tokenPrecision": precision,
        "tokenRecall": recall,
        "tokenF1": f1,
        "tokenSequenceRatio": sequence_ratio,
    }


def benchmark_fixture(args: argparse.Namespace, repo_root: pathlib.Path, fixture: dict[str, str]) -> dict:
    pdf_path = get_fixture(fixture, args.output_dir)
    candidate_path = args.output_dir / f"{fixture['id']}.connector.txt"
    reference_path = args.output_dir / f"{fixture['id']}.pymupdf4llm.txt"

    started = time.perf_counter()
    connector_diagnostics = run_connector(args, repo_root, pdf_path, candidate_path)
    connector_seconds = time.perf_counter() - started

    started = time.perf_counter()
    reference = pymupdf4llm.to_text(
        str(pdf_path),
        use_ocr=False,
        header=True,
        footer=True,
        table_format="grid",
    )
    reference_seconds = time.perf_counter() - started
    reference_path.write_text(reference, encoding="utf-8")
    candidate = candidate_path.read_text(encoding="utf-8")
    metrics = compare(candidate, reference)
    passed = (
        metrics["tokenF1"] >= MINIMUM_TOKEN_F1
        and metrics["tokenSequenceRatio"] >= MINIMUM_TOKEN_SEQUENCE_RATIO
        and connector_seconds < CUSTOM_CODE_TIMEOUT_SECONDS
    )
    return {
        "fixture": fixture,
        "fixtureBytes": pdf_path.stat().st_size,
        "connectorSeconds": connector_seconds,
        "referenceSeconds": reference_seconds,
        "connectorDiagnostics": connector_diagnostics,
        "metrics": metrics,
        "passed": passed,
    }


def main() -> int:
    args = parse_args()
    repo_root = pathlib.Path(__file__).resolve().parents[3]
    args.output_dir.mkdir(parents=True, exist_ok=True)
    selected = FIXTURES if args.fixture == "all" else tuple(
        item for item in FIXTURES if item["id"] == args.fixture
    )
    results = [benchmark_fixture(args, repo_root, fixture) for fixture in selected]
    passed = all(result["passed"] for result in results)
    report = {
        "pymupdf4llmVersion": getattr(pymupdf4llm, "__version__", "unknown"),
        "thresholds": {
            "minimumTokenF1": MINIMUM_TOKEN_F1,
            "minimumTokenSequenceRatio": MINIMUM_TOKEN_SEQUENCE_RATIO,
            "maximumConnectorSeconds": CUSTOM_CODE_TIMEOUT_SECONDS,
        },
        "fixtures": results,
        "passed": passed,
    }
    (args.output_dir / "benchmark-results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
