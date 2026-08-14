#!/usr/bin/env python3
"""Download selected Production PDFs read-only with size/hash verification and resume."""

from __future__ import annotations

import argparse
import hashlib
import json
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--selection", type=Path, required=True)
    parser.add_argument("--market-access-repo", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--result", type=Path, required=True)
    parser.add_argument("--site", default="Production")
    parser.add_argument("--max-files", type=int)
    parser.add_argument("--max-retries", type=int, default=3)
    return parser.parse_args()


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_records(path: Path) -> list[dict[str, Any]]:
    with path.open("r", encoding="utf-8") as stream:
        values = [json.loads(line) for line in stream if line.strip()]
    return values[1:]


def main() -> int:
    args = parse_args()
    selection = read_records(args.selection.resolve())
    if args.max_files is not None:
        selection = selection[: args.max_files]

    from sharepoint_ops.config import load_config, resolve_site
    from sharepoint_ops import sp_api

    market_repo = args.market_access_repo.resolve()
    config = load_config(market_repo)
    site = resolve_site(config, args.site, allow_default=False, write_operation=False)
    sp_api.configure(proxy_endpoint=config.proxy_endpoint, timeout_seconds=config.timeout_seconds)

    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    result_path = args.result.resolve()
    result_path.parent.mkdir(parents=True, exist_ok=True)
    completed_names: set[str] = set()
    if result_path.exists():
        with result_path.open("r", encoding="utf-8") as stream:
            for line in stream:
                if line.strip():
                    value = json.loads(line)
                    if value.get("status") in {"downloaded", "alreadyPresent"}:
                        completed_names.add(str(value.get("name")))

    with result_path.open("a", encoding="utf-8", newline="\n") as results:
        for index, record in enumerate(selection, 1):
            name = str(record["name"])
            expected_length = int(record["lengthBytes"])
            target = output_dir / name
            if name in completed_names and target.exists() and target.stat().st_size == expected_length:
                continue

            status = "failed"
            error = None
            file_hash = None
            started = time.monotonic()
            if target.exists() and target.stat().st_size == expected_length:
                data = target.read_bytes()
                status = "alreadyPresent"
                file_hash = digest(data)
            else:
                data = None
                for attempt in range(args.max_retries):
                    try:
                        data = sp_api.download_file_bytes(site.url, str(record["serverRelativeUrl"]))
                        if data is None:
                            raise RuntimeError("SharePoint returned no bytes.")
                        if len(data) != expected_length:
                            raise RuntimeError(f"Expected {expected_length} bytes but received {len(data)}.")
                        break
                    except Exception as exc:
                        error = str(exc)
                        data = None
                        if attempt + 1 < args.max_retries:
                            time.sleep(min(2 ** attempt, 8))
                if data is not None:
                    partial = target.with_suffix(target.suffix + ".part")
                    partial.write_bytes(data)
                    partial.replace(target)
                    status = "downloaded"
                    file_hash = digest(data)

            result = {
                "name": name,
                "status": status,
                "expectedLengthBytes": expected_length,
                "actualLengthBytes": target.stat().st_size if target.exists() else None,
                "sha256": file_hash,
                "elapsedMilliseconds": int((time.monotonic() - started) * 1000),
                "completedUtc": utc_now(),
                "error": error if status == "failed" else None,
            }
            results.write(json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n")
            results.flush()
            print(json.dumps({"index": index, "total": len(selection), "name": name, "status": status, "elapsedMilliseconds": result["elapsedMilliseconds"]}), flush=True)

    print(json.dumps({"completed": True, "requested": len(selection), "outputDir": str(output_dir), "result": str(result_path)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
