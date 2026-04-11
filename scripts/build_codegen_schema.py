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

    # NJsonSchema struggles with some discriminator/oneOf shapes and may generate empty DTOs.
    # We normalize a few known cases into codegen-friendly object schemas.
    #
    # RequestPermissionOutcome is a discriminated union in the ACP schema:
    # - { outcome: "cancelled" }
    # - { outcome: "selected", optionId: PermissionOptionId }
    #
    # We flatten it into a single object with optional optionId so the generated model
    # has real properties (regen-safe) and can still represent both cases.
    if "RequestPermissionOutcome" in defs_rewritten:
        defs_rewritten["RequestPermissionOutcome"] = {
            "description": "The outcome of a permission request.",
            "type": "object",
            "properties": {
                "_meta": {
                    "type": ["object", "null"],
                    "additionalProperties": True,
                    "description": "The _meta property is reserved by ACP to allow clients and agents to attach additional metadata.",
                },
                "outcome": {
                    "type": "string",
                    "enum": ["cancelled", "selected"],
                    "description": "Either 'cancelled' or 'selected'.",
                },
                "optionId": {
                    "description": "The ID of the option the user selected (only present when outcome == 'selected').",
                    "type": ["string", "null"],
                },
            },
            "required": ["outcome"],
        }

    # SessionConfigOption is a discriminator/oneOf that uses allOf to pull in the actual
    # payload (currentValue/options) based on type. NJsonSchema sometimes drops those
    # allOf fields, producing an incomplete DTO.
    # We flatten the currently supported option kind (select) into a single object.
    if "SessionConfigSelectOptions" in defs_rewritten:
        # NOTE: ACP supports grouped select options, but the anyOf shape produces anonymous placeholder
        # types in NJsonSchema. For now we flatten to the ungrouped form (array of SessionConfigSelectOption).
        # Grouping can be reintroduced later via a custom union generator if needed.
        defs_rewritten["SessionConfigSelectOptions"] = {
            "description": "Possible values for a session configuration option (ungrouped).",
            "type": "array",
            "items": {"$ref": "#/definitions/SessionConfigSelectOption"},
        }

    if "SessionConfigOption" in defs_rewritten:
        defs_rewritten["SessionConfigOption"] = {
            "description": "A session configuration option selector and its current state.",
            "type": "object",
            "properties": {
                "_meta": {
                    "type": ["object", "null"],
                    "additionalProperties": True,
                },
                "id": {"$ref": "#/definitions/SessionConfigId"},
                "name": {"type": "string"},
                "description": {"type": ["string", "null"]},
                "category": {
                    "anyOf": [
                        {"$ref": "#/definitions/SessionConfigOptionCategory"},
                        {"type": "null"},
                    ]
                },
                "type": {
                    "type": "string",
                    "enum": ["select"],
                },
                "currentValue": {"$ref": "#/definitions/SessionConfigValueId"},
                "options": {"$ref": "#/definitions/SessionConfigSelectOptions"},
            },
            "required": ["id", "name", "type", "currentValue", "options"],
        }

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
