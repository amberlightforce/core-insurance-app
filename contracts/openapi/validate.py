#!/usr/bin/env python3
"""Validate the cross-module API contracts in contracts/openapi.

Usage:
    python contracts/openapi/validate.py [--require-spec-validator] [--write-index]

Checks
  * every module document (pty … mkt) and common.yaml parse; info.x-module matches the file name
  * full OpenAPI 3.1 validation with `openapi-spec-validator` (pinned in requirements.txt) when it is installed;
    --require-spec-validator makes its absence an error (use it in CI)
  * every $ref resolves (local pointers, common.yaml, ../events/common.schema.json), recursively
  * operationIds are unique across all modules and follow `<module>.<Resource>.<verb>` with the file's module
  * every operation has x-requirement (REQ ids or REQ ranges), x-wave, x-status, x-operation-kind,
    x-idempotency, x-dry-run, x-exposure, x-permission, x-error-codes and x-source
  * every state-changing operation (POST/PUT/PATCH/DELETE that is not declared a query) is a command, and every
    command requires the Idempotency-Key header; GET operations are queries
  * x-dry-run: true ⇒ the dryRun parameter is declared; every operation declares traceparent
  * every 4xx/5xx response is application/problem+json with the shared Problem schema; at least one 2xx response
  * error codes match `<MOD>-ERR-...`; x-in-process: true ⇒ x-exposure is [internal]; x-maturity is
    pre-release or stable (D-API-06)
  * D-API-08: no parameter or schema property is named asOf / asAt (valid time is `validAt`, transaction time `knownAt`)
  * D-API-09: no request body carries `validAt` / `knownAt`; they are query parameters only
  * list-style queries (list*, search, query, history, ...) are paginated (cursor + limit + page envelope) or carry
    `x-bounded` with the reason
  * warning: schema property names that join two concepts with And/Or (split them, review D-5)
  * security: each document has a security requirement and every scheme it names is declared
  * path template parameters are declared as path parameters
  * x-operation-families: family name `<module>.<Resource>.*`, non-empty x-requirement
  * anchors.yaml: every contract anchor is covered by an operation/family x-requirement or has a notApi reason
  * spi.md lists the 40 SPIs of the catalogue (REQ-MKT-002)
  * INDEX.md is up to date (regenerate with --write-index)

Needs Python 3.9+, PyYAML. See requirements.txt for pinned versions.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import OrderedDict, defaultdict
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover
    print("PyYAML is required: pip install -r contracts/openapi/requirements.txt", file=sys.stderr)
    sys.exit(2)

ROOT = Path(__file__).resolve().parent
MODULES = ["pty", "pfc", "rat", "uw", "pol", "bil", "clm", "ri", "fin", "doc", "cmp", "chn", "wrk", "plt", "dat",
           "mig", "mkt"]
MODCODES = [m.upper() for m in MODULES]
WAVES = ["W1", "W2", "W3", "W4", "W5", "W6", "W7", "W8", "W9", "unscheduled"]
METHODS = ["get", "put", "post", "delete", "options", "head", "patch", "trace"]
REQ_RE = re.compile(r"^REQ-[A-Z]+-\d{3,4}(\.\.REQ-[A-Z]+-\d{3,4})?$")
ERR_RE = re.compile(r"^(%s)-ERR-[A-Z0-9]+(-[A-Z0-9]+)*$" % "|".join(MODCODES))
IDEMPOTENCY_REF = "common.yaml#/components/parameters/IdempotencyKey"
FORBIDDEN_TIME_NAMES = {"asof", "asat"}
TIME_PARAMS = {"validAt", "knownAt"}
PAGE_VERBS = {"list", "search", "query", "history", "deliveries", "outcomes", "runs", "statements", "changes",
              "calculations", "breaks", "results"}
JOINED = re.compile(r"[a-z0-9](And|Or)[A-Z]")
PROBLEM_REF = "common.yaml#/components/schemas/Problem"


class Report:
    def __init__(self) -> None:
        self.errors: list[str] = []
        self.warnings: list[str] = []

    def error(self, msg: str) -> None:
        self.errors.append(msg)

    def warn(self, msg: str) -> None:
        self.warnings.append(msg)


# ------------------------------------------------------------------ loading and $ref resolution
_DOCS: dict[Path, object] = {}


def load(path: Path):
    path = path.resolve()
    if path not in _DOCS:
        text = path.read_text(encoding="utf-8")
        _DOCS[path] = json.loads(text) if path.suffix == ".json" else yaml.safe_load(text)
    return _DOCS[path]


def pointer(doc, ptr: str):
    node = doc
    if ptr in ("", "/"):
        return node
    for part in ptr.lstrip("/").split("/"):
        part = part.replace("~1", "/").replace("~0", "~")
        if isinstance(node, list):
            node = node[int(part)]
        elif isinstance(node, dict) and part in node:
            node = node[part]
        else:
            raise KeyError(part)
    return node


def resolve(ref: str, base: Path):
    """Return (node, file) for a $ref relative to file `base`."""
    if ref.startswith("#"):
        return pointer(load(base), ref[1:]), base
    target, _, frag = ref.partition("#")
    if re.match(r"^[a-z]+://", target):
        raise KeyError(f"remote $ref not allowed: {ref}")
    f = (base.parent / target).resolve()
    if not f.exists():
        raise KeyError(f"file not found: {target}")
    return pointer(load(f), frag), f


def deref(node, base: Path, depth: int = 0):
    while isinstance(node, dict) and "$ref" in node and depth < 20:
        node, base = resolve(node["$ref"], base)
        depth += 1
    return node, base


def check_refs(node, base: Path, rep: Report, where: str, seen: set) -> None:
    """Resolve every $ref reachable from node (following into referenced files once)."""
    stack = [(node, base, where)]
    while stack:
        n, b, w = stack.pop()
        if isinstance(n, dict):
            if "$ref" in n and isinstance(n["$ref"], str):
                ref = n["$ref"]
                key = (str(b), ref)
                if key not in seen:
                    seen.add(key)
                    try:
                        target, tb = resolve(ref, b)
                        stack.append((target, tb, f"{w} -> {ref}"))
                    except Exception as exc:  # noqa: BLE001
                        rep.error(f"{w}: unresolved $ref {ref!r} ({exc})")
            for k, v in n.items():
                if k != "$ref":
                    stack.append((v, b, w))
        elif isinstance(n, list):
            for v in n:
                stack.append((v, b, w))


# ------------------------------------------------------------------ checks
def iter_ops(doc):
    for path, item in (doc.get("paths") or {}).items():
        for method in METHODS:
            if method in (item or {}):
                yield path, method, item[method], item


def check_module(m: str, rep: Report, seen_ids: dict, seen_refs: set) -> None:
    f = ROOT / f"{m}.yaml"
    if not f.exists():
        rep.error(f"{m}.yaml missing")
        return
    try:
        doc = load(f)
    except Exception as exc:  # noqa: BLE001
        rep.error(f"{m}.yaml: cannot parse: {exc}")
        return
    if doc.get("openapi") != "3.1.0":
        rep.error(f"{m}.yaml: openapi must be 3.1.0")
    if (doc.get("info") or {}).get("x-module") != m.upper():
        rep.error(f"{m}.yaml: info.x-module must be {m.upper()}")
    check_refs(doc, f, rep, f"{m}.yaml", seen_refs)
    schemes = set((doc.get("components") or {}).get("securitySchemes", {}) or {})
    doc_sec = doc.get("security")
    if not doc_sec:
        rep.error(f"{m}.yaml: top-level security requirement missing")
    for req in doc_sec or []:
        for name in req:
            if name not in schemes:
                rep.error(f"{m}.yaml: security scheme {name!r} not declared")
    op_re = re.compile(rf"^{m}\.[A-Z][A-Za-z0-9]*\.[a-z][A-Za-z0-9]*$")
    for path, method, op, item in iter_ops(doc):
        oid = op.get("operationId")
        w = f"{m}.yaml {method.upper()} {path}"
        if not oid:
            rep.error(f"{w}: operationId missing")
            continue
        w = f"{m}.yaml {oid}"
        if not op_re.match(oid):
            rep.error(f"{w}: operationId must be {m}.<Resource>.<verb>")
        if oid in seen_ids:
            rep.error(f"{w}: duplicate operationId (also in {seen_ids[oid]})")
        seen_ids[oid] = f"{m}.yaml"
        if not path.startswith(f"/api/{m}/v1/"):
            rep.error(f"{w}: path must start with /api/{m}/v1/")
        reqs = op.get("x-requirement")
        if not reqs or not isinstance(reqs, list):
            rep.error(f"{w}: x-requirement missing")
        else:
            for r in reqs:
                if not REQ_RE.match(str(r)):
                    rep.error(f"{w}: bad requirement id {r!r}")
        if op.get("x-wave") not in WAVES:
            rep.error(f"{w}: x-wave must be one of {WAVES}")
        if op.get("x-status") not in ("full", "minimal"):
            rep.error(f"{w}: x-status must be full or minimal")
        kind = op.get("x-operation-kind")
        if kind not in ("command", "query"):
            rep.error(f"{w}: x-operation-kind must be command or query")
        if op.get("x-idempotency") not in ("required", "optional", "none"):
            rep.error(f"{w}: x-idempotency must be required, optional or none")
        if not isinstance(op.get("x-dry-run"), bool):
            rep.error(f"{w}: x-dry-run must be a boolean")
        exp = op.get("x-exposure")
        if not exp or not set(exp) <= {"ui", "partner", "internal"}:
            rep.error(f"{w}: x-exposure must be a non-empty subset of ui, partner, internal")
        if op.get("x-in-process") and exp != ["internal"]:
            rep.error(f"{w}: x-in-process operations must have x-exposure [internal]")
        if op.get("x-maturity") not in ("pre-release", "stable"):
            rep.error(f"{w}: x-maturity must be pre-release or stable (D-API-06)")
        if not op.get("x-permission"):
            rep.error(f"{w}: x-permission missing")
        if not op.get("x-source"):
            rep.error(f"{w}: x-source missing")
        for e in op.get("x-error-codes") or []:
            if not ERR_RE.match(str(e.get("code", ""))):
                rep.error(f"{w}: bad error code {e.get('code')!r}")
            if not isinstance(e.get("status"), int) or not 400 <= e["status"] <= 599:
                rep.error(f"{w}: error {e.get('code')} needs an HTTP status 400-599")
        if "x-error-codes" not in op:
            rep.error(f"{w}: x-error-codes missing (use [] when the PRD names none)")
        # parameters
        params = list(item.get("parameters") or []) + list(op.get("parameters") or [])
        resolved = []
        for p in params:
            try:
                pn, _ = deref(p, f)
                resolved.append((p, pn))
            except Exception:  # noqa: BLE001
                pass  # reported by check_refs
        names = {(pn.get("in"), pn.get("name")) for _, pn in resolved if isinstance(pn, dict)}
        state_changing = method in ("post", "put", "patch", "delete")
        if method == "get" and kind != "query":
            rep.error(f"{w}: GET operations must be queries")
        if state_changing and kind != "command" and op.get("x-operation-kind") != "query":
            rep.error(f"{w}: state-changing operation must be a command")
        if kind == "command":
            idem = [pn for p, pn in resolved if isinstance(pn, dict) and pn.get("in") == "header"
                    and pn.get("name") == "Idempotency-Key"]
            if not idem or not idem[0].get("required"):
                rep.error(f"{w}: command without a required Idempotency-Key header")
            if op.get("x-idempotency") != "required":
                rep.error(f"{w}: command must have x-idempotency: required")
            if not any(str(e.get("code", "")).endswith("-ERR-IDEMPOTENCY-MISMATCH") for e in op.get("x-error-codes") or []):
                rep.error(f"{w}: command must list <MOD>-ERR-IDEMPOTENCY-MISMATCH")
        for (pin, pname) in names:
            if str(pname).lower() in FORBIDDEN_TIME_NAMES:
                rep.error(f"{w}: parameter {pname!r} is forbidden; use validAt / knownAt (D-API-08)")
        # D-API-09: time-travel inputs never in the body
        rb = op.get("requestBody")
        if rb:
            try:
                rbn, rbb = deref(rb, f)
                sch = ((rbn.get("content") or {}).get("application/json") or {}).get("schema")
                if sch is not None:
                    sch, _ = deref(sch, rbb)
                    props = set((sch or {}).get("properties") or {})
                    dup = props & (TIME_PARAMS | {"asOf", "asAt"})
                    if dup:
                        rep.error(f"{w}: request body carries time-travel input(s) {sorted(dup)}; use the query "
                                  f"parameter only (D-API-09)")
            except Exception:  # noqa: BLE001
                pass
        # pagination
        verb = oid.split(".")[-1]
        if kind == "query" and (verb.startswith("list") or verb in PAGE_VERBS) and not op.get("x-bounded"):
            if ("query", "cursor") not in names or ("query", "limit") not in names:
                rep.error(f"{w}: list query without cursor/limit (paginate or set x-bounded with the reason)")
            else:
                ok2 = [r for c, r in (op.get("responses") or {}).items() if str(c).startswith("2")]
                paged = False
                for r in ok2:
                    try:
                        rn, rb2 = deref(r, f)
                        sch = (((rn or {}).get("content") or {}).get("application/json") or {}).get("schema") or {}
                        sch, _ = deref(sch, rb2)
                        paged = any("PageEnvelope" in str(x.get("$ref", "")) for x in (sch or {}).get("allOf", []))
                    except Exception:  # noqa: BLE001
                        pass
                if not paged:
                    rep.error(f"{w}: list query response must use the page envelope")
        if op.get("x-dry-run") and ("query", "dryRun") not in names:
            rep.error(f"{w}: x-dry-run true but no dryRun parameter")
        if op.get("x-dry-run") and kind != "command":
            rep.error(f"{w}: dry-run is only meaningful on commands")
        if ("header", "traceparent") not in names:
            rep.error(f"{w}: traceparent parameter missing")
        for tp in re.findall(r"\{(\w+)\}", path):
            if ("path", tp) not in names:
                rep.error(f"{w}: path parameter {tp} not declared")
        # request body
        if state_changing and kind == "command" and "requestBody" not in op:
            rep.warn(f"{w}: command without request body")
        # responses
        resps = op.get("responses") or {}
        if not any(str(c).startswith("2") for c in resps):
            rep.error(f"{w}: no 2xx response")
        for code, r in resps.items():
            if not re.match(r"^[45]\d\d$", str(code)):
                continue
            ref = r.get("$ref") if isinstance(r, dict) else None
            try:
                rn, rb = deref(r, f)
            except Exception:  # noqa: BLE001
                continue
            content = (rn or {}).get("content") or {}
            pj = content.get("application/problem+json")
            if not pj:
                rep.error(f"{w}: response {code} is not application/problem+json")
                continue
            sch = pj.get("schema") or {}
            sref = sch.get("$ref", "")
            if not (sref.endswith("#/components/schemas/Problem") and (rb.name == "common.yaml" or "common.yaml" in sref)):
                rep.error(f"{w}: response {code} must use the shared Problem schema")
        # security
        for req in op.get("security") or []:
            for name in req:
                if name not in schemes:
                    rep.error(f"{w}: security scheme {name!r} not declared in {m}.yaml")
    # schema property names: forbidden time names and joined concepts
    def walk_props(node, where):
        if isinstance(node, dict):
            props = node.get("properties")
            if isinstance(props, dict):
                for k in props:
                    if k.lower() in FORBIDDEN_TIME_NAMES:
                        rep.error(f"{where}: property {k!r} is forbidden; use validAt / knownAt (D-API-08)")
                    elif JOINED.search(k):
                        rep.warn(f"{where}: property {k!r} may join two concepts (And/Or); split it")
            for k, v in node.items():
                walk_props(v, where)
        elif isinstance(node, list):
            for v in node:
                walk_props(v, where)
    walk_props((doc.get("components") or {}).get("schemas") or {}, f"{m}.yaml components")
    for fam in doc.get("x-operation-families") or []:
        name = fam.get("family", "")
        if not re.match(rf"^{m}\.[A-Z][A-Za-z0-9]*\.\*$", name):
            rep.error(f"{m}.yaml family {name!r}: must be {m}.<Resource>.*")
        if not fam.get("x-requirement") or not all(REQ_RE.match(str(r)) for r in fam["x-requirement"]):
            rep.error(f"{m}.yaml family {name!r}: bad or missing x-requirement")
        if fam.get("x-maturity") not in ("pre-release", "stable"):
            rep.error(f"{m}.yaml family {name!r}: x-maturity must be pre-release or stable")
        if fam.get("x-wave") not in WAVES:
            rep.error(f"{m}.yaml family {name!r}: bad x-wave")


def check_common(rep: Report, seen_refs: set) -> None:
    f = ROOT / "common.yaml"
    doc = load(f)
    check_refs(doc, f, rep, "common.yaml", seen_refs)
    comps = doc.get("components") or {}
    for name, prm in (comps.get("parameters") or {}).items():
        if str(prm.get("name", "")).lower() in FORBIDDEN_TIME_NAMES:
            rep.error(f"common.yaml: parameter {name} uses a forbidden time name (D-API-08)")
    for need in ("IdempotencyKey", "Traceparent", "ValidAt", "KnownAt", "DryRun", "Cursor", "Limit"):
        if need not in (comps.get("parameters") or {}):
            rep.error(f"common.yaml: parameter {need} missing")
    idem = comps["parameters"]["IdempotencyKey"]
    if not (idem.get("in") == "header" and idem.get("name") == "Idempotency-Key" and idem.get("required") is True):
        rep.error("common.yaml: IdempotencyKey must be a required header named Idempotency-Key")
    prob = (comps.get("schemas") or {}).get("Problem") or {}
    for field in ("type", "title", "status", "code", "traceId"):
        if field not in (prob.get("required") or []):
            rep.error(f"common.yaml: Problem must require {field}")


def check_spec_validator(rep: Report, require: bool) -> None:
    try:
        from openapi_spec_validator import validate as osv_validate  # type: ignore
        from openapi_spec_validator.readers import read_from_filename  # type: ignore
    except ImportError:
        msg = "openapi-spec-validator not installed: full OpenAPI 3.1 schema validation skipped"
        (rep.error if require else rep.warn)(msg)
        return
    for f in [ROOT / "common.yaml"] + [ROOT / f"{m}.yaml" for m in MODULES]:
        if not f.exists():
            continue
        try:
            spec, _ = read_from_filename(str(f))
            osv_validate(spec, base_uri=f.resolve().as_uri())
        except Exception as exc:  # noqa: BLE001
            rep.error(f"{f.name}: OpenAPI 3.1 validation failed: {str(exc)[:400]}")


def collect():
    ops, fams = [], []
    for m in MODULES:
        f = ROOT / f"{m}.yaml"
        if not f.exists():
            continue
        doc = load(f)
        for path, method, op, _ in iter_ops(doc):
            ops.append((m, path, method, op))
        for fam in doc.get("x-operation-families") or []:
            fams.append((m, fam))
    return ops, fams


def expand_req(r: str) -> list[str]:
    mm = re.match(r"^REQ-([A-Z]+)-(\d+)\.\.REQ-\1-(\d+)$", r)
    if not mm:
        return [r]
    a, b = int(mm.group(2)), int(mm.group(3))
    w = len(mm.group(2))
    if b < a or b - a > 300:
        return [f"REQ-{mm.group(1)}-{mm.group(2)}", f"REQ-{mm.group(1)}-{mm.group(3)}"]
    return [f"REQ-{mm.group(1)}-{n:0{w}d}" for n in range(a, b + 1)]


def anchor_coverage():
    ops, fams = collect()
    cov = defaultdict(list)
    for m, path, method, op in ops:
        for r in op.get("x-requirement") or []:
            for x in expand_req(r):
                cov[x].append(op["operationId"])
    for m, fam in fams:
        for r in fam.get("x-requirement") or []:
            for x in expand_req(r):
                cov[x].append(fam["family"])
    return cov


def check_anchors(rep: Report) -> None:
    f = ROOT / "anchors.yaml"
    if not f.exists():
        rep.error("anchors.yaml missing")
        return
    anchors = (load(f) or {}).get("anchors") or {}
    cov = anchor_coverage()
    if len(anchors) < 170:
        rep.error(f"anchors.yaml lists {len(anchors)} anchors; the contract has 174")
    for a, e in anchors.items():
        if not cov.get(a) and not (e or {}).get("notApi"):
            rep.error(f"anchor {a} is not covered by any operation and has no notApi reason")


def check_spi(rep: Report) -> None:
    f = ROOT / "spi.md"
    if not f.exists():
        rep.error("spi.md missing")
        return
    names = re.findall(r"^### \d+\. `([A-Za-z]+)`", f.read_text(encoding="utf-8"), re.M)
    if len(names) != 40 or len(set(names)) != 40:
        rep.error(f"spi.md must document 40 distinct SPIs (found {len(set(names))})")


# ------------------------------------------------------------------ INDEX.md
def render_index() -> str:
    ops, fams = collect()
    by_mod = defaultdict(list)
    for m, path, method, op in ops:
        by_mod[m].append((path, method, op))
    fam_by = defaultdict(list)
    for m, fam in fams:
        fam_by[m].append(fam)
    anchors = (load(ROOT / "anchors.yaml") or {}).get("anchors") or {}
    cov = anchor_coverage()
    out = []
    out.append("# API operation index")
    out.append("")
    out.append("Generated by `python contracts/openapi/validate.py --write-index` from the module documents; do not edit by hand.")
    out.append("")
    out.append("Columns: **Kind** C = command (requires `Idempotency-Key`), Q = query; **DR** = dry-run supported; "
               "**Exposure** ui / partner / internal (internal = in-process only, `x-in-process`); **Consumers** = modules "
               "that call the operation in-process (from the PRDs' §9.2 outbound tables, the owner row's \"called by\", "
               "and the CHN partner/MCP mappings); **Status** full / minimal (`x-status`).")
    out.append("")
    out.append("## Summary")
    out.append("")
    out.append("| Module | Operations | Full | Minimal | Commands | Queries | In-process only | Partner-facing | Unexpanded families |")
    out.append("|---|---|---|---|---|---|---|---|---|")
    tot = defaultdict(int)
    for m in MODULES:
        lst = by_mod[m]
        row = dict(n=len(lst), full=sum(o["x-status"] == "full" for _, _, o in lst),
                   minimal=sum(o["x-status"] == "minimal" for _, _, o in lst),
                   cmd=sum(o["x-operation-kind"] == "command" for _, _, o in lst),
                   qry=sum(o["x-operation-kind"] == "query" for _, _, o in lst),
                   ip=sum(bool(o.get("x-in-process")) for _, _, o in lst),
                   partner=sum("partner" in o["x-exposure"] for _, _, o in lst), fam=len(fam_by[m]))
        for k, v in row.items():
            tot[k] += v
        out.append(f"| [{m.upper()}](#{m}) | {row['n']} | {row['full']} | {row['minimal']} | {row['cmd']} | {row['qry']} | "
                   f"{row['ip']} | {row['partner']} | {row['fam']} |")
    out.append(f"| **Total** | {tot['n']} | {tot['full']} | {tot['minimal']} | {tot['cmd']} | {tot['qry']} | {tot['ip']} | "
               f"{tot['partner']} | {tot['fam']} |")
    out.append("")
    covered = sum(1 for a in anchors if cov.get(a))
    out.append(f"Contract anchors: {len(anchors)}; covered by operations or families: {covered}; "
               f"not an API (reason in `anchors.yaml`): {sum(1 for a, e in anchors.items() if not cov.get(a) and (e or {}).get('notApi'))}.")
    out.append("")
    for m in MODULES:
        out.append(f"## {m}")
        out.append("")
        out.append(f"`{m}.yaml` — {(load(ROOT / f'{m}.yaml').get('info') or {}).get('title', '')}")
        out.append("")
        out.append("| Operation | Method and path | Kind | DR | Exposure | Consumers | Wave | Status | Requirements |")
        out.append("|---|---|---|---|---|---|---|---|---|")
        for path, method, op in sorted(by_mod[m], key=lambda x: x[2]["operationId"]):
            reqs = op.get("x-requirement") or []
            rq = ", ".join(reqs[:3]) + (" …" if len(reqs) > 3 else "")
            out.append(f"| `{op['operationId']}` | {method.upper()} `{path}` | {'C' if op['x-operation-kind'] == 'command' else 'Q'} | "
                       f"{'yes' if op['x-dry-run'] else ''} | {', '.join(op['x-exposure'])} | {', '.join(op.get('x-consumers') or [])} | "
                       f"{op['x-wave']} | {op['x-status']} | {rq} |")
        if fam_by[m]:
            out.append("")
            out.append("Operation families named only as `Resource.*` in the PRD (members named by the owning WP):")
            out.append("")
            out.append("| Family | Purpose | Consumers | Wave | Requirements |")
            out.append("|---|---|---|---|---|")
            for fam in fam_by[m]:
                reqs = fam.get("x-requirement") or []
                out.append(f"| `{fam['family']}` | {fam.get('summary', '')} | {', '.join(fam.get('x-consumers') or [])} | "
                           f"{fam['x-wave']} | {', '.join(reqs[:3])}{' …' if len(reqs) > 3 else ''} |")
        out.append("")
    out.append("## Contract anchor coverage")
    out.append("")
    out.append("| Anchor | Covered by | Note |")
    out.append("|---|---|---|")
    for a, e in anchors.items():
        c = sorted(set(cov.get(a, [])))
        shown = ", ".join(f"`{x}`" for x in c[:4]) + (f" (+{len(c) - 4})" if len(c) > 4 else "")
        note = (e or {}).get("notApi") or ("mapped by builder" if (e or {}).get("mappedTo") else "")
        out.append(f"| {a} | {shown or '—'} | {note} |")
    out.append("")
    return "\n".join(out)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--require-spec-validator", action="store_true",
                    help="fail when openapi-spec-validator is not installed")
    ap.add_argument("--write-index", action="store_true", help="regenerate INDEX.md")
    args = ap.parse_args()
    rep = Report()
    seen_ids: dict = {}
    seen_refs: set = set()
    try:
        check_common(rep, seen_refs)
    except Exception as exc:  # noqa: BLE001
        rep.error(f"common.yaml: {exc}")
    for m in MODULES:
        check_module(m, rep, seen_ids, seen_refs)
    extra = sorted(p.stem for p in ROOT.glob("*.yaml") if p.stem not in MODULES + ["common", "anchors"])
    for e in extra:
        rep.error(f"unexpected document {e}.yaml (not a module code)")
    check_anchors(rep)
    check_spi(rep)
    check_spec_validator(rep, args.require_spec_validator)
    idx = ROOT / "INDEX.md"
    text = render_index()
    if args.write_index:
        idx.write_text(text, encoding="utf-8", newline="\n")
    elif not idx.exists() or idx.read_text(encoding="utf-8") != text:
        rep.error("INDEX.md is out of date: run validate.py --write-index")
    for w in rep.warnings:
        print(f"WARNING: {w}")
    for e in rep.errors:
        print(f"ERROR: {e}")
    n_ops = len(seen_ids)
    print(f"{n_ops} operations in {len(MODULES)} modules; {len(rep.errors)} errors, {len(rep.warnings)} warnings")
    return 1 if rep.errors else 0


if __name__ == "__main__":
    sys.exit(main())
