#!/usr/bin/env python3
"""Validates the generated API samples against the OpenAPI component and response schemas.

tools/CoreIns.ContractGen writes tests/CoreIns.Testing.Contracts/Generated/Samples/<mod>.json:
  {"responses": {operationId: sample}, "schemas": {componentName: sample}}
The sandbox doubles answer with the "responses" samples and the contract tests round-trip every "schemas" sample
through its generated C# record; this check proves the samples themselves are schema-valid (JSON Schema 2020-12,
the OpenAPI 3.1 dialect, with $refs into common.yaml and contracts/events/common.schema.json).
Event samples are validated by contracts/events/validate.py --instance.

Usage: python tools/CoreIns.ContractGen/validate_samples.py   (needs contracts/openapi/requirements.txt)
"""
import json
import sys
from pathlib import Path

import yaml
from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from referencing.jsonschema import DRAFT202012

REPO = Path(__file__).resolve().parents[2]
OPENAPI = REPO / "contracts" / "openapi"
SAMPLES = REPO / "tests" / "CoreIns.Testing.Contracts" / "Generated" / "Samples"


def load(uri: str):
    path = Path(uri.removeprefix("file://"))
    text = path.read_text(encoding="utf-8")
    contents = yaml.safe_load(text) if path.suffix in (".yaml", ".yml") else json.loads(text)
    return Resource.from_contents(contents, default_specification=DRAFT202012)


def main() -> int:
    registry = Registry(retrieve=load)
    errors: list[str] = []
    checked = 0
    for sample_file in sorted(SAMPLES.glob("*.json")):
        module = sample_file.stem
        doc_path = OPENAPI / f"{module}.yaml"
        doc = yaml.safe_load(doc_path.read_text(encoding="utf-8"))
        base = doc_path.resolve().as_uri()
        samples = json.loads(sample_file.read_text(encoding="utf-8"))

        responses = {}
        for item in doc["paths"].values():
            for method, op in item.items():
                if method in ("get", "post", "put", "patch", "delete"):
                    for status, resp in sorted(op["responses"].items()):
                        schema = (resp.get("content") or {}).get("application/json", {}).get("schema")
                        if status.startswith("2") and schema is not None:
                            responses[op["operationId"]] = schema
                            break

        def check(label: str, schema: dict, instance) -> None:
            nonlocal checked
            checked += 1
            wrapper = {"$id": base, **{k: v for k, v in doc.items() if k == "components"}, "$ref": "#/$defs/__target", "$defs": {"__target": schema}}
            validator = Draft202012Validator(wrapper, registry=registry, format_checker=Draft202012Validator.FORMAT_CHECKER)
            for err in validator.iter_errors(instance):
                errors.append(f"{module}.{label}: {'/'.join(map(str, err.path))}: {err.message}")

        for name, instance in samples.get("schemas", {}).items():
            check(name, {"$ref": f"#/components/schemas/{name}"}, instance)
        for op_id, instance in samples.get("responses", {}).items():
            if op_id not in responses:
                errors.append(f"{module}: sample for unknown operation {op_id}")
                continue
            check(op_id, responses[op_id], instance)

    for e in errors:
        print(f"ERROR: {e}")
    print(f"API samples checked: {checked}; " + ("OK" if not errors else f"FAILED ({len(errors)} errors)"))
    return 0 if not errors else 1


if __name__ == "__main__":
    sys.exit(main())
