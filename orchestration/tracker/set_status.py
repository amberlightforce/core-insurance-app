"""Update WP status in backlog.json. Usage: python set_status.py <WP-ID> <status> [owner] [blocker1;blocker2]
Prints the JSON patch to push to the tracker with ArtifactData update."""
import json, sys, datetime, pathlib
p = pathlib.Path(__file__).resolve().parents[1] / "backlog" / "backlog.json"
b = json.loads(p.read_text(encoding="utf-8"))
wid, status = sys.argv[1], sys.argv[2]
owner = sys.argv[3] if len(sys.argv) > 3 else None
blockers = [x for x in sys.argv[4].split(";") if x] if len(sys.argv) > 4 else None
now = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")
for w in b["workPackages"]:
    if w["id"] == wid:
        w["status"] = status
        if owner is not None: w["owner"] = owner
        if blockers is not None: w["blockers"] = blockers
        if status == "failed": w["reviewAttempts"] = w.get("reviewAttempts", 0) + 1
        patch = {"status": w["status"], "owner": w.get("owner"), "blockers": w.get("blockers", []), "reviewAttempts": w.get("reviewAttempts", 0), "updatedAt": now}
        break
else:
    sys.exit(f"unknown WP {wid}")
p.write_text(json.dumps(b, ensure_ascii=False, indent=1), encoding="utf-8")
print(json.dumps(patch, ensure_ascii=False))
