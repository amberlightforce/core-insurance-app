"""Export orchestration/backlog/backlog.json into one JSON file per tracker document.

The orchestrator pushes these files to the tracker artifact's database with the
ArtifactData tool (batch writes of up to 50, each entry using `file_path`).
Usage: python export_tracker.py <out_dir> [--meta "phase|currentWave|note"]
Prints the batch write entries (op/collection/doc_id/file_path) as JSON chunks of 50.
"""
import json, os, sys, datetime, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
backlog = json.loads((ROOT / "backlog" / "backlog.json").read_text(encoding="utf-8"))
out = pathlib.Path(sys.argv[1]); (out / "wps").mkdir(parents=True, exist_ok=True); (out / "prds").mkdir(exist_ok=True); (out / "meta").mkdir(exist_ok=True)

meta_arg = sys.argv[sys.argv.index("--meta") + 1] if "--meta" in sys.argv else "Phase 1 — Foundations|P1|"
phase, wave, note = (meta_arg.split("|") + ["", "", ""])[:3]
now = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")

entries = []
for i, p in enumerate(backlog["prds"]):
    doc = {"prd": p["prd"], "module": p.get("module"), "title": p.get("title"), "mustP1": p.get("mustP1"), "order": i}
    f = out / "prds" / f"{p['prd']}.json"; f.write_text(json.dumps(doc, ensure_ascii=False), encoding="utf-8")
    entries.append({"op": "set", "collection": "prds", "doc_id": p["prd"], "file_path": str(f)})

for w in backlog["workPackages"]:
    doc = {k: w.get(k) for k in ("prd", "module", "wave", "seq", "title", "summary", "mustCount", "points", "dependsOn", "status", "owner", "reviewAttempts", "blockers")}
    doc["reqs"] = [{"id": r["id"], "title": r.get("title", ""), "s": r.get("s", "todo")} for r in w.get("reqs", [])]
    doc["updatedAt"] = now
    f = out / "wps" / f"{w['id']}.json"; f.write_text(json.dumps(doc, ensure_ascii=False), encoding="utf-8")
    entries.append({"op": "set", "collection": "wps", "doc_id": w["id"], "file_path": str(f)})

f = out / "meta" / "summary.json"
f.write_text(json.dumps({"phase": phase, "currentWave": wave, "note": note, "updatedAt": now}, ensure_ascii=False), encoding="utf-8")
entries.append({"op": "set", "collection": "meta", "doc_id": "summary", "file_path": str(f)})

for i in range(0, len(entries), 50):
    print(json.dumps(entries[i:i + 50]))
