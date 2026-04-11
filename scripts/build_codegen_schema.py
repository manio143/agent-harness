#!/usr/bin/env python3
"""Build a codegen-friendly JSON Schema.

ACP's published schema/schema.json primarily contains $defs and may not reference them
from the root schema. Some generators only emit types that are reachable from the root.

This script creates schema/schema.codegen.json where the root schema has an `anyOf` that
references every definition under `$defs`, making all types reachable.

It does not modify the original schema.json.
"""

from __future__ import annotations

import json
from pathlib import Path


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    src = root / "schema" / "schema.json"
    out = root / "schema" / "schema.codegen.json"

    data = json.loads(src.read_text(encoding="utf-8"))
    defs = data.get("$defs") or data.get("definitions")
    if not isinstance(defs, dict) or not defs:
        raise SystemExit("No $defs/definitions found in schema.json")

    def rewrite_refs(obj):
        if isinstance(obj, dict):
            out = {}
            for k, v in obj.items():
                if k == "$ref" and isinstance(v, str):
                    out[k] = v.replace("#/$defs/", "#/definitions/")
                else:
                    out[k] = rewrite_refs(v)
            return out
        if isinstance(obj, list):
            return [rewrite_refs(x) for x in obj]
        return obj

    defs_rewritten = rewrite_refs(defs)

    # Note: we keep all defs in the codegen schema.
    # Some union-heavy refs and some schema patterns still require deterministic postprocessing
    # of the generated C# (performed by tools/Agent.Acp.TypeGen).

    # NJsonSchema (and many generators) expect draft-07 style `definitions` rather than `$defs`.
    # So we rewrite to `definitions` and re-point all refs to `#/definitions/...`.
    codegen = {
        "$schema": data.get("$schema"),
        "$id": data.get("$id", "https://agentclientprotocol.com/protocol/schema.codegen"),
        "title": data.get("title", "ACP Codegen Root"),
        "description": "Synthetic root to make all ACP defs reachable for code generation.",
        "definitions": defs_rewritten,
        # anyOf so every definition becomes reachable
        "anyOf": [{"$ref": f"#/definitions/{name}"} for name in sorted(defs_rewritten.keys())],
    }

    out.write_text(json.dumps(codegen, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {out} with {len(defs)} defs")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
