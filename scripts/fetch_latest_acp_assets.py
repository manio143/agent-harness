#!/usr/bin/env python3
"""Fetch ACP release assets (schema/meta) from the latest GitHub Release.

Usage:
  python3 scripts/fetch_latest_acp_assets.py [--tag vX.Y.Z] [--out schema]

- Defaults to the latest release tag.
- Downloads: schema.json, schema.unstable.json, meta.json, meta.unstable.json (if present).

This is intentionally dependency-free (stdlib only).
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.request
from pathlib import Path

REPO = "agentclientprotocol/agent-client-protocol"
API = "https://api.github.com"


def http_get_json(url: str) -> dict:
    req = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "User-Agent": "marian-agent/0.0 (schema fetcher)",
        },
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        data = resp.read().decode("utf-8")
    return json.loads(data)


def download(url: str, out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    req = urllib.request.Request(url, headers={"User-Agent": "marian-agent/0.0 (schema fetcher)"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        content = resp.read()
    out_path.write_bytes(content)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--tag", help="Release tag to fetch (e.g. v0.11.5). Defaults to latest.")
    ap.add_argument("--out", default="schema", help="Output directory (default: schema)")
    args = ap.parse_args()

    out_dir = Path(args.out)

    if args.tag:
        release = http_get_json(f"{API}/repos/{REPO}/releases/tags/{args.tag}")
    else:
        release = http_get_json(f"{API}/repos/{REPO}/releases/latest")

    tag = release.get("tag_name")
    assets = release.get("assets", [])

    wanted = {
        "schema.json",
        "schema.unstable.json",
        "meta.json",
        "meta.unstable.json",
    }

    downloaded = []
    for asset in assets:
        name = asset.get("name")
        if name in wanted:
            url = asset.get("browser_download_url")
            if not url:
                continue
            dest = out_dir / name
            download(url, dest)
            downloaded.append(name)

    # Write a small pointer file for humans/CI
    info = {
        "repo": REPO,
        "tag": tag,
        "downloaded": sorted(downloaded),
        "release_url": release.get("html_url"),
    }
    (out_dir / "FETCHED_FROM_RELEASE.json").write_text(json.dumps(info, indent=2) + "\n", encoding="utf-8")

    print(f"ACP assets fetched from {tag}: {', '.join(sorted(downloaded))}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
