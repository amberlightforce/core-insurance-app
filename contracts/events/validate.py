#!/usr/bin/env python3
"""Validate the cross-module event contracts in contracts/events.

Usage:
    python contracts/events/validate.py [--require-jsonschema] [--no-samples]

Checks
  * catalog.json parses, matches catalog.schema.json, and its eventCount is right
  * event names are unique; producers are valid module codes; topics and registry names match the producer
  * every catalogue entry has a schema file at <module>/<EventName>.v<major>.schema.json, and every schema file
    has a catalogue entry
  * every schema is a valid JSON Schema 2020-12 document, composes the envelope (allOf $ref ../envelope.schema.json),
    pins eventType / producer / dataClassification consistently with the catalogue, and classifies every payload field
  * set-completeness events require set_id, set_size, index (contract D4, D-CON-19)
  * every consumer is a module other than the producer, is listed once, and cites a PRD §8 handler (D-CON-09)
  * every $ref resolves; a synthetic sample envelope is generated per event and validated against its schema

Needs Python 3.9+. Uses the `jsonschema` package (>= 4.18) when installed; without it, only the structural checks run
and the script says so (pass --require-jsonschema in CI to make that an error).
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import uuid
from pathlib import Path
from urllib.parse import urljoin

ROOT = Path(__file__).resolve().parent
MODULES = ["PTY", "PFC", "RAT", "UW", "POL", "BIL", "CLM", "RI", "FIN", "DOC", "CMP", "CHN", "WRK", "PLT", "DAT", "MIG", "MKT"]
CLASSES = ["P0", "P1", "P2", "P3"]
ENVELOPE_REF = "../envelope.schema.json"
SET_FIELDS = ["set_id", "set_size", "index"]


class Report:
    def __init__(self) -> None:
        self.errors: list[str] = []
        self.warnings: list[str] = []

    def error(self, msg: str) -> None:
        self.errors.append(msg)

    def warn(self, msg: str) -> None:
        self.warnings.append(msg)


def load_json(path: Path, rep: Report):
    try:
        with path.open(encoding="utf-8") as f:
            return json.load(f)
    except Exception as exc:  # noqa: BLE001
        rep.error(f"{path.relative_to(ROOT)}: cannot parse JSON: {exc}")
        return None


def walk_refs(node, out: list[str]) -> None:
    if isinstance(node, dict):
        for k, v in node.items():
            if k == "$ref" and isinstance(v, str):
                out.append(v)
            else:
                walk_refs(v, out)
    elif isinstance(node, list):
        for v in node:
            walk_refs(v, out)


def find_number_holes(node, where="$"):
    """Static guard: payload schemas never use JSON `number` and never accept unconstrained members.

    Amounts are Money objects with decimal-string amounts; integers are only allowed as typed counts."""
    found = []
    if isinstance(node, dict):
        t = node.get("type")
        if t == "number" or (isinstance(t, list) and "number" in t):
            found.append((where, "type number"))
        if node.get("additionalProperties") is True or node.get("additionalProperties") == {}:
            found.append((where, "unconstrained additionalProperties"))
        for k, v in node.items():
            found += find_number_holes(v, f"{where}/{k}")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            found += find_number_holes(v, f"{where}/{i}")
    return found


def check_envelope_semantics(inst) -> list[str]:
    """Rules JSON Schema cannot express. The C# envelope type must enforce the same (README, R8)."""
    errs = []
    present = [f for f in SET_FIELDS if f in inst]
    if present and len(present) != 3:
        errs.append("set_id, set_size and index must be given together")
    if len(present) == 3:
        size, idx = inst["set_size"], inst["index"]
        if not isinstance(idx, int) or not isinstance(size, int):
            errs.append("set_size and index must be integers")
        elif idx < 1:
            errs.append(f"index {idx} < 1")
        elif idx > size:
            errs.append(f"index {idx} > set_size {size}")
    return errs


def check_structure(catalog, schemas: dict[Path, dict], rep: Report) -> None:
    events = catalog.get("events", [])
    if catalog.get("eventCount") != len(events):
        rep.error(f"catalog.json: eventCount {catalog.get('eventCount')} != {len(events)} events")
    seen: set[tuple[str, str]] = set()
    producer_of: dict[str, str] = {}
    expected_files: set[Path] = set()
    for e in events:
        name = e.get("name", "?")
        prod = e.get("producer")
        major = str(e.get("version", "")).split(".")[0]
        if (name, major) in seen:
            rep.error(f"catalog: duplicate event {name} v{major}")
        seen.add((name, major))
        if producer_of.setdefault(name, prod) != prod:
            rep.error(f"catalog: {name} has two producers ({producer_of[name]}, {prod})")
        if prod not in MODULES:
            rep.error(f"{name}: producer {prod!r} is not a module code")
            continue
        if e.get("topic") != f"{prod.lower()}.events.v{major}":
            rep.error(f"{name}: topic {e.get('topic')} does not match {prod.lower()}.events.v{major}")
        if e.get("registryName") != f"{prod.lower()}.{name}":
            rep.error(f"{name}: registryName {e.get('registryName')} != {prod.lower()}.{name}")
        rel = f"{prod.lower()}/{name}.v{major}.schema.json"
        if e.get("schema") != rel:
            rep.error(f"{name}: schema path {e.get('schema')} != {rel}")
        path = ROOT / rel
        expected_files.add(path)
        if not path.is_file():
            rep.error(f"{name}: schema file {rel} missing")
            continue
        sch = schemas.get(path)
        if sch is None:
            continue
        # composition with the envelope
        if not any(isinstance(a, dict) and a.get("$ref") == ENVELOPE_REF for a in sch.get("allOf", [])):
            rep.error(f"{rel}: does not compose the envelope (allOf $ref {ENVELOPE_REF})")
        props = sch.get("properties", {})
        if props.get("eventType", {}).get("const") != name:
            rep.error(f"{rel}: properties.eventType.const != {name}")
        if props.get("producer", {}).get("const") != prod:
            rep.error(f"{rel}: properties.producer.const != {prod}")
        if props.get("schemaVersion", {}).get("pattern") != f"^{major}\\.\\d+$":
            rep.error(f"{rel}: schemaVersion pattern must pin major {major}")
        if sch.get("x-event-type") != name or sch.get("x-producer") != prod:
            rep.error(f"{rel}: x-event-type / x-producer disagree with the catalogue")
        if sch.get("unevaluatedProperties") is not False:
            rep.error(f"{rel}: unevaluatedProperties must be false")
        payload = sch.get("$defs", {}).get("Payload")
        if not isinstance(payload, dict) or props.get("payload", {}).get("$ref") != "#/$defs/Payload":
            rep.error(f"{rel}: payload must reference #/$defs/Payload")
            continue
        maxcls = "P0"
        personal = []
        for fname, fs in payload.get("properties", {}).items():
            cls = fs.get("x-classification")
            if cls not in CLASSES:
                rep.error(f"{rel}: payload field {fname} lacks x-classification P0..P3")
                continue
            if cls != "P0":
                personal.append(fname)
                if fs.get("x-personal-data") is not True:
                    rep.error(f"{rel}: payload field {fname} is {cls} but not marked x-personal-data")
            if CLASSES.index(cls) > CLASSES.index(maxcls):
                maxcls = cls
        if props.get("dataClassification", {}).get("const") != maxcls or e.get("dataClassification") != maxcls:
            rep.error(f"{rel}: dataClassification must be the highest field class {maxcls}")
        if sorted(personal) != sorted(e.get("personalDataFields", [])):
            rep.error(f"{name}: personalDataFields disagree with the schema ({personal})")
        if e.get("setCompleteness"):
            if sorted(sch.get("required", [])) != sorted(SET_FIELDS) and not set(SET_FIELDS) <= set(sch.get("required", [])):
                rep.error(f"{rel}: set-completeness event must require {SET_FIELDS}")
        if e.get("status") != sch.get("x-payload-status"):
            rep.error(f"{rel}: x-payload-status != catalogue status")
        if e.get("status") == "minimal" and not sch.get("x-payload-note"):
            rep.error(f"{rel}: minimal payload must carry x-payload-note")
        # consumers
        cmods = [c.get("module") for c in e.get("consumers", [])]
        if len(cmods) != len(set(cmods)):
            rep.error(f"{name}: duplicate consumer entries")
        for c in e.get("consumers", []):
            if c.get("module") not in MODULES:
                rep.error(f"{name}: consumer {c.get('module')} is not a module code")
            if c.get("module") == prod:
                rep.error(f"{name}: producer listed as its own consumer")
            if not re.fullmatch(r"PRD-\d{2} §8\.\d+", c.get("handler", "")):
                rep.error(f"{name}: consumer {c.get('module')} has no handler reference")
        # business keys (D-CON-28): declared, and enforced by the schema
        bkeys = e.get("x-business-keys") or []
        if not bkeys:
            rep.error(f"{name}: x-business-keys must declare at least one lineage key")
        if sch.get("x-business-keys") != bkeys:
            rep.error(f"{rel}: x-business-keys differs from the catalogue")
        bk = props.get("businessKeys", {})
        simple = sorted(k for k in bkeys if "|" not in k)
        alts = sorted(sorted(a["required"][0] for a in part.get("anyOf", [])) for part in bk.get("allOf", []))
        if sorted(bk.get("required", [])) != simple or alts != sorted(sorted(k.split("|")) for k in bkeys if "|" in k):
            rep.error(f"{rel}: properties.businessKeys does not require exactly the declared x-business-keys")
        # no bare JSON numbers where money or open structures can appear (R2)
        for where, bad in find_number_holes(payload):
            rep.error(f"{rel}: payload {where} allows {bad}")
    for path in schemas:
        if path not in expected_files:
            rep.error(f"{path.relative_to(ROOT).as_posix()}: schema file has no catalogue entry")
    names = {e.get("name") for e in events}
    for r in catalog.get("consumerChanges", {}).get("removed", []):
        if r.get("event") not in names:
            rep.error(f"consumerChanges.removed: unknown event {r.get('event')}")
        for e in events:
            if e.get("name") == r.get("event") and r.get("module") in [c["module"] for c in e.get("consumers", [])]:
                rep.error(f"{r.get('event')}: {r.get('module')} recorded as removed but still listed as consumer")


# ----------------------------------------------------------------------------- sample instances
class Sampler:
    """Builds one minimal valid instance from a schema (enough for the schemas in this folder)."""

    def __init__(self, resolve):
        self.resolve = resolve  # (base_uri, ref) -> (schema, base_uri)

    def make(self, s, base, depth=0):
        if depth > 40:
            raise RuntimeError("sample recursion too deep")
        if "$ref" in s:
            target, nbase = self.resolve(base, s["$ref"])
            return self.make(target, nbase, depth + 1)
        if "const" in s:
            return s["const"]
        if "enum" in s:
            return s["enum"][0]
        if "oneOf" in s:
            return self.make(s["oneOf"][0], base, depth + 1)
        if "allOf" in s:
            out = {}
            for part in s["allOf"]:
                v = self.make(part, base, depth + 1)
                if isinstance(v, dict):
                    out.update(v)
            props = s.get("properties", {})
            for k, sub in props.items():
                if k in out or k in s.get("required", []):
                    out[k] = self.make(sub, base, depth + 1)
            return out
        t = s.get("type")
        if t == "object" or "properties" in s:
            out = {}
            props = s.get("properties", {})
            for k in s.get("required", []):
                if k in props:
                    out[k] = self.make(props[k], base, depth + 1)
                elif isinstance(s.get("additionalProperties"), dict):
                    out[k] = self.make(s["additionalProperties"], base, depth + 1)
            for k, sub in props.items():  # optional members too, so their constraints are exercised
                if k not in out:
                    out[k] = self.make(sub, base, depth + 1)
            if s.get("minProperties", 0) > len(out) and props:
                k = next(iter(props))
                out[k] = self.make(props[k], base, depth + 1)
            return out
        if t == "array":
            return [self.make(s["items"], base, depth + 1)] if "items" in s else []
        if t == "integer":
            return max(1, s.get("minimum", 1))
        if t == "boolean":
            return False
        if t == "null":
            return None
        if t == "string":
            return self.string(s)
        return {}

    @staticmethod
    def string(s):
        p = s.get("pattern", "")
        fmt = s.get("format")
        if "7[0-9a-f]{3}" in p:
            return "01890a5d-ac96-774b-bcce-b302099a8057"
        if "[0-9a-f]{8}-" in p:
            return str(uuid.UUID(int=1))
        if "[0-9a-f]{64}" in p:
            return "0" * 63 + "1"
        if "0{32}" in p or "[0-9a-f]{32}" in p:
            return "4bf92f3577b34da6a3ce929d0e0e4736"
        if fmt == "date-time":
            return "2026-10-07T09:00:00Z"
        if fmt == "date":
            return "2026-10-07"
        known = {
            "^[A-Z]{3}$": "EUR", "^[A-Z]{2}$": "GR", "^-?(0|[1-9]\\d*)(\\.\\d+)?$": "0.00",
            "^[a-z]{2,3}(-[A-Z]{2})?$": "el", "^\\d+\\.\\d+$": "1.0", "^1\\.\\d+$": "1.0",
            "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$": "1.0", "^[A-Z][A-Za-z0-9]+$": "Sample",
            "^[A-Z]{2,3}_[A-Z0-9_]+$": "POL_REFUND_DUE", "^AI-[A-Z]{2,3}-\\d{2,}$": "AI-CLM-01",
            "^RC-[A-Z0-9-]+$": "RC-POL-QUOTE", "^OBL-[A-Z0-9-]+$": "OBL-GDPR",
            "^dc\\.[a-z]+\\.[a-z0-9_.-]+\\.v\\d+$": "dc.pol.policy.v1", "^[a-z][A-Za-z0-9]*$": "policyId",
        }
        if p.startswith("^P(?!$)"):
            return "P15D"
        if p in known:
            return known[p]
        return "X"


def run_jsonschema(catalog, schemas: dict[Path, dict], envelope, common, catalog_schema, rep: Report, samples: bool) -> bool:
    try:
        import jsonschema  # noqa: F401
        from jsonschema import Draft202012Validator
        from referencing import Registry, Resource
        from referencing.jsonschema import DRAFT202012
    except ImportError:
        return False

    resources = []
    all_docs = [envelope, common, catalog_schema] + list(schemas.values())
    for doc in all_docs:
        try:
            Draft202012Validator.check_schema(doc)
        except Exception as exc:  # noqa: BLE001
            rep.error(f"{doc.get('$id')}: not a valid JSON Schema 2020-12: {str(exc).splitlines()[0]}")
        resources.append((doc["$id"], Resource.from_contents(doc, default_specification=DRAFT202012)))
    registry = Registry().with_resources(resources)

    cat_errors = sorted(Draft202012Validator(catalog_schema).iter_errors(catalog), key=lambda e: list(e.path))
    for err in cat_errors[:20]:
        rep.error(f"catalog.json: {'/'.join(map(str, err.path))}: {err.message}")

    # every $ref resolves
    for doc in all_docs:
        refs: list[str] = []
        walk_refs(doc, refs)
        resolver = registry.resolver(base_uri=doc["$id"])
        for ref in sorted(set(refs)):
            try:
                resolver.lookup(ref)
            except Exception as exc:  # noqa: BLE001
                rep.error(f"{doc['$id']}: $ref {ref} does not resolve ({type(exc).__name__})")

    if not samples:
        return True

    def resolve(base, ref):
        res = registry.resolver(base_uri=base).lookup(ref)
        return res.contents, urljoin(base, ref).split("#")[0]

    sampler = Sampler(resolve)
    copy = lambda o: json.loads(json.dumps(o))  # noqa: E731
    for path, sch in schemas.items():
        rel = path.relative_to(ROOT).as_posix()
        try:
            inst = sampler.make(sch, sch["$id"])
        except Exception as exc:  # noqa: BLE001
            rep.error(f"{rel}: cannot build a sample instance: {exc}")
            continue
        inst.update({"set_id": str(uuid.UUID(int=2)), "set_size": 2, "index": 2})
        # samples carry the declared business keys (D-CON-28)
        keys = sch.get("x-business-keys") or []
        inst["businessKeys"] = {k.split("|")[0]: f"bk-{i}" for i, k in enumerate(keys)}
        validator = Draft202012Validator(sch, registry=registry, format_checker=Draft202012Validator.FORMAT_CHECKER)

        def rejects(bad, what, semantic=False):
            ok = validator.is_valid(bad) and not (semantic and check_envelope_semantics(bad))
            if ok:
                rep.error(f"{rel}: {what} is not rejected")

        for err in list(validator.iter_errors(inst))[:3]:
            rep.error(f"{rel}: sample instance invalid at {'/'.join(map(str, err.path))}: {err.message}")
        for msg in check_envelope_semantics(inst):
            rep.error(f"{rel}: sample instance: {msg}")
        # unknown payload field
        bad = copy(inst)
        bad["payload"]["__unknownField"] = "x"
        rejects(bad, "an unknown payload field")
        # D-CON-26: actor, jurisdiction, aiInteractionId always present
        for f in ("actor", "jurisdiction", "aiInteractionId"):
            bad = copy(inst)
            del bad[f]
            rejects(bad, f"a missing envelope {f}")
        # D-CON-28: every declared business key is required; businessKeys is never empty
        for k in keys:
            bad = copy(inst)
            bad["businessKeys"].pop(k.split("|")[0], None)
            rejects(bad, f"a missing business key {k}")
        bad = copy(inst)
        bad["businessKeys"] = {}
        rejects(bad, "an empty businessKeys")
        # D4 set completeness: required where declared; index range (semantic, R8)
        if sch.get("x-set-completeness"):
            bad = {k: v for k, v in inst.items() if k not in SET_FIELDS}
            rejects(bad, "an event without set fields")
        for idx, size in ((3, 2), (0, 2)):
            bad = copy(inst)
            bad["index"], bad["set_size"] = idx, size
            rejects(bad, f"index {idx} with set_size {size}", semantic=True)
        # R2: no bare JSON number in any structured payload member (money totals, open objects, lines...)
        for fname, val in inst["payload"].items():
            if isinstance(val, dict) or (isinstance(val, list) and val and isinstance(val[0], dict)):
                bad = copy(inst)
                bad["payload"][fname] = {"total": 12.5} if isinstance(val, dict) else [{"total": 12.5}]
                rejects(bad, f"a bare number inside payload.{fname}")
        bad = copy(inst)
        n_amounts = mutate_amounts(bad["payload"])
        if n_amounts:
            rejects(bad, "a Money amount given as a JSON number")
        # R1: a pinned rating slot must carry the pinned artefact hash
        rsd = inst["payload"].get("ratingSlotDeclaration")
        if isinstance(rsd, dict):
            bad = copy(inst)
            bad["payload"]["ratingSlotDeclaration"]["bindingMode"] = "PINNED"
            bad["payload"]["ratingSlotDeclaration"].pop("pinnedArtefactHash", None)
            rejects(bad, "a PINNED rating slot without pinnedArtefactHash")
            bad["payload"]["ratingSlotDeclaration"]["pinnedArtefactHash"] = None
            rejects(bad, "a PINNED rating slot with a null pinnedArtefactHash")
            good = copy(inst)
            good["payload"]["ratingSlotDeclaration"].update({"bindingMode": "FLOATING", "pinnedArtefactHash": None})
            if not validator.is_valid(good):
                rep.error(f"{rel}: a FLOATING rating slot without pinned hash should be valid")
    return True


def mutate_amounts(node) -> int:
    """Replace every Money `amount` string with a JSON number; returns how many were replaced."""
    n = 0
    if isinstance(node, dict):
        for k, v in list(node.items()):
            if k == "amount" and isinstance(v, str):
                node[k] = 1.5
                n += 1
            else:
                n += mutate_amounts(v)
    elif isinstance(node, list):
        for v in node:
            n += mutate_amounts(v)
    return n


def validate_instances(paths: list[str], catalog, schemas: dict[Path, dict], rep: Report) -> None:
    """Validate concrete event JSON files (one event, or a JSON array of events) against their schemas."""
    from jsonschema import Draft202012Validator
    from referencing import Registry, Resource
    from referencing.jsonschema import DRAFT202012

    docs = [load_json(ROOT / n, rep) for n in ("envelope.schema.json", "common.schema.json")] + list(schemas.values())
    registry = Registry().with_resources([(d["$id"], Resource.from_contents(d, default_specification=DRAFT202012)) for d in docs])
    by_type = {}
    for e in catalog["events"]:
        by_type[(e["name"], e["version"].split(".")[0])] = ROOT / e["schema"]
    for p in paths:
        data = json.loads(Path(p).read_text(encoding="utf-8"))
        for i, ev in enumerate(data if isinstance(data, list) else [data]):
            label = f"{p}[{i}]"
            key = (ev.get("eventType"), str(ev.get("schemaVersion", "")).split(".")[0])
            if key not in by_type:
                rep.error(f"{label}: unknown event type/major {key}")
                continue
            v = Draft202012Validator(schemas[by_type[key]], registry=registry, format_checker=Draft202012Validator.FORMAT_CHECKER)
            for err in v.iter_errors(ev):
                rep.error(f"{label}: {'/'.join(map(str, err.path))}: {err.message}")
            for msg in check_envelope_semantics(ev):
                rep.error(f"{label}: {msg}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--require-jsonschema", action="store_true", help="fail when the jsonschema package is missing")
    ap.add_argument("--no-samples", action="store_true", help="skip synthetic sample validation")
    ap.add_argument("--instance", nargs="*", default=[], metavar="FILE",
                    help="also validate concrete event JSON files (an event or an array of events), e.g. test fixtures")
    args = ap.parse_args()

    rep = Report()
    catalog = load_json(ROOT / "catalog.json", rep)
    envelope = load_json(ROOT / "envelope.schema.json", rep)
    common = load_json(ROOT / "common.schema.json", rep)
    catalog_schema = load_json(ROOT / "catalog.schema.json", rep)
    schemas: dict[Path, dict] = {}
    for mod in MODULES:
        for p in sorted((ROOT / mod.lower()).glob("*.schema.json")):
            doc = load_json(p, rep)
            if doc is not None:
                schemas[p] = doc
    for d in ROOT.iterdir():
        if d.is_dir() and d.name.upper() not in MODULES and not d.name.startswith((".", "_")):
            rep.error(f"{d.name}/: directory is not a module code")
    if catalog is None or envelope is None or common is None or catalog_schema is None:
        print("\n".join(rep.errors))
        return 1

    check_structure(catalog, schemas, rep)
    for where, bad in find_number_holes(common):
        rep.error(f"common.schema.json: {where} allows {bad}")
    used = run_jsonschema(catalog, schemas, envelope, common, catalog_schema, rep, samples=not args.no_samples)
    if not used:
        msg = "jsonschema package not installed: JSON Schema meta-validation, $ref resolution and sample checks skipped"
        (rep.error if args.require_jsonschema else rep.warn)(msg)
    elif args.instance:
        validate_instances(args.instance, catalog, schemas, rep)

    events = catalog.get("events", [])
    by_prod: dict[str, int] = {}
    for e in events:
        by_prod[e["producer"]] = by_prod.get(e["producer"], 0) + 1
    status = {s: sum(1 for e in events if e.get("status") == s) for s in ("full", "minimal")}
    print(f"events: {len(events)}  schemas: {len(schemas)}  full: {status['full']}  minimal: {status['minimal']}")
    print("by producer: " + ", ".join(f"{m} {by_prod.get(m, 0)}" for m in MODULES))
    for w in rep.warnings:
        print(f"WARNING: {w}")
    for e in rep.errors:
        print(f"ERROR: {e}")
    print("OK" if not rep.errors else f"FAILED ({len(rep.errors)} errors)")
    return 0 if not rep.errors else 1


if __name__ == "__main__":
    sys.exit(main())
