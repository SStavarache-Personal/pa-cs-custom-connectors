#!/usr/bin/env python3
"""Create a resumable, read-only manifest of the Production monograph folder."""

from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_LIBRARY = "/sites/JAMP-MarketAccess/Product Monographs"
DEFAULT_FOLDER = "/sites/JAMP-MarketAccess/Product Monographs/pdf"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--market-access-repo", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--site", default="Production")
    parser.add_argument("--library-server-relative-url", default=DEFAULT_LIBRARY)
    parser.add_argument("--folder-server-relative-url", default=DEFAULT_FOLDER)
    parser.add_argument("--row-limit", type=int, default=5000)
    parser.add_argument("--max-retries", type=int, default=3)
    parser.add_argument("--restart", action="store_true")
    return parser.parse_args()


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def atomic_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".new")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def response_json(response: Any) -> dict[str, Any]:
    if response is None:
        raise RuntimeError("SharePoint proxy returned no response.")
    if response.status_code != 200:
        raise RuntimeError(f"SharePoint RenderListDataAsStream returned HTTP {response.status_code}.")
    try:
        payload = response.json()
    except ValueError as exc:
        raise RuntimeError("SharePoint response was not JSON.") from exc
    if not isinstance(payload, dict) or not isinstance(payload.get("Row"), list):
        raise RuntimeError("SharePoint response did not contain a Row array.")
    return payload


def normalize_row(row: dict[str, Any]) -> dict[str, Any] | None:
    if str(row.get("FSObjType", "0")) != "0":
        return None
    name = str(row.get("FileLeafRef") or "").strip()
    if not name.lower().endswith(".pdf"):
        return None
    try:
        item_id = int(row["ID"])
        length = int(row.get("File_x0020_Size") or 0)
    except (KeyError, TypeError, ValueError) as exc:
        raise RuntimeError(f"Invalid SharePoint row for {name or '<unnamed>'}.") from exc
    return {
        "itemId": item_id,
        "name": name,
        "serverRelativeUrl": str(row.get("FileRef") or ""),
        "lengthBytes": length,
        "modified": row.get("Modified"),
        "uniqueId": str(row.get("UniqueId") or "").strip("{}").lower(),
    }


def main() -> int:
    args = parse_args()
    if not 1 <= args.row_limit <= 5000:
        raise SystemExit("--row-limit must be between 1 and 5000.")

    market_repo = args.market_access_repo.resolve()
    if not (market_repo / ".sharepoint-ops.local.json").exists() and not (market_repo / ".sharepoint-ops.json").exists():
        raise SystemExit("The Market Access repository has no SharePoint Ops configuration.")

    from sharepoint_ops.config import load_config, resolve_site
    from sharepoint_ops import sp_api

    config = load_config(market_repo)
    site = resolve_site(config, args.site, allow_default=False, write_operation=False)
    sp_api.configure(proxy_endpoint=config.proxy_endpoint, timeout_seconds=config.timeout_seconds)

    output = args.output.resolve()
    partial = output.with_suffix(output.suffix + ".partial")
    state_path = output.with_suffix(output.suffix + ".state.json")
    if args.restart:
        partial.unlink(missing_ok=True)
        state_path.unlink(missing_ok=True)

    state: dict[str, Any] = {
        "startedUtc": utc_now(),
        "nextPaging": None,
        "pagesCompleted": 0,
        "recordsWritten": 0,
    }
    if state_path.exists():
        state = json.loads(state_path.read_text(encoding="utf-8"))
        if not partial.exists():
            raise SystemExit("Resume state exists but the partial manifest is missing. Use --restart.")

    escaped_library = args.library_server_relative_url.replace("'", "''")
    uri = (
        "_api/web/GetListUsingPath(DecodedUrl=@a1)/RenderListDataAsStream"
        f"?@a1='{escaped_library}'"
    )
    folder_xml = (
        args.folder_server_relative_url.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
    )
    view_xml = (
        "<View Scope='RecursiveAll'><ViewFields>"
        "<FieldRef Name='ID'/><FieldRef Name='FileLeafRef'/><FieldRef Name='FileRef'/>"
        "<FieldRef Name='File_x0020_Size'/><FieldRef Name='Modified'/><FieldRef Name='UniqueId'/>"
        "<FieldRef Name='FSObjType'/></ViewFields><Query><Where><Eq><FieldRef Name='FileDirRef'/>"
        f"<Value Type='Text'>{folder_xml}</Value></Eq></Where></Query>"
        f"<RowLimit Paged='TRUE'>{args.row_limit}</RowLimit></View>"
    )

    partial.parent.mkdir(parents=True, exist_ok=True)
    paging = state.get("nextPaging")
    with partial.open("a", encoding="utf-8", newline="\n") as stream:
        while True:
            parameters: dict[str, Any] = {
                "RenderOptions": 2,
                "ViewXml": view_xml,
                "FolderServerRelativeUrl": args.folder_server_relative_url,
            }
            if paging:
                parameters["Paging"] = paging

            payload = None
            last_error: Exception | None = None
            for attempt in range(args.max_retries):
                try:
                    response = sp_api._sp_request(site.url, "POST", uri, body={"parameters": parameters})
                    payload = response_json(response)
                    break
                except Exception as exc:  # retry only read operations
                    last_error = exc
                    if attempt + 1 < args.max_retries:
                        time.sleep(min(2 ** attempt, 8))
            if payload is None:
                raise RuntimeError(f"SharePoint snapshot failed after retries: {last_error}")

            normalized = [item for item in (normalize_row(row) for row in payload["Row"]) if item is not None]
            for item in normalized:
                stream.write(json.dumps(item, ensure_ascii=False, separators=(",", ":")) + "\n")
            stream.flush()

            next_href = str(payload.get("NextHref") or "").lstrip("?") or None
            state["nextPaging"] = next_href
            state["pagesCompleted"] = int(state.get("pagesCompleted", 0)) + 1
            state["recordsWritten"] = int(state.get("recordsWritten", 0)) + len(normalized)
            state["lastPageUtc"] = utc_now()
            atomic_json(state_path, state)
            print(
                json.dumps(
                    {
                        "pagesCompleted": state["pagesCompleted"],
                        "recordsWritten": state["recordsWritten"],
                        "hasMore": bool(next_href),
                    }
                ),
                file=sys.stderr,
                flush=True,
            )
            if not next_href:
                break
            paging = next_href

    records_by_id: dict[int, dict[str, Any]] = {}
    for line in partial.read_text(encoding="utf-8").splitlines():
        if line.strip():
            record = json.loads(line)
            records_by_id[int(record["itemId"])] = record
    records = sorted(records_by_id.values(), key=lambda item: (item["name"].casefold(), item["itemId"]))

    output.parent.mkdir(parents=True, exist_ok=True)
    completed = output.with_suffix(output.suffix + ".new")
    with completed.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(
            json.dumps(
                {
                    "recordType": "snapshot",
                    "snapshotUtc": utc_now(),
                    "siteUrl": site.url,
                    "libraryServerRelativeUrl": args.library_server_relative_url,
                    "folderServerRelativeUrl": args.folder_server_relative_url,
                    "fileCount": len(records),
                    "readOnly": True,
                },
                ensure_ascii=False,
                separators=(",", ":"),
            )
            + "\n"
        )
        for record in records:
            stream.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")
    completed.replace(output)
    partial.unlink(missing_ok=True)
    state_path.unlink(missing_ok=True)
    print(json.dumps({"completed": True, "fileCount": len(records), "output": str(output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
