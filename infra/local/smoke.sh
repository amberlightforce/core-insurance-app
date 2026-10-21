#!/usr/bin/env bash
# Smoke test of the local stack over HTTP (synthetic data): dev sign-in, create a person, search it three ways,
# read it masked and revealed. Needs curl and python3 (or python). Usage: infra/local/smoke.sh [api-base-url]
set -euo pipefail
export PYTHONIOENCODING=utf-8
API=${1:-http://127.0.0.1:5000}
PY=$(command -v python3 || command -v python)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
json() { "$PY" -c "import sys,json;d=json.load(open(sys.argv[1],encoding='utf-8'));print(eval(sys.argv[2]))" "$@"; }
uuid() { "$PY" -c 'import uuid;print(uuid.uuid4())'; }

echo "ready: $(curl -s -o /dev/null -w '%{http_code}' "$API/health/ready")"
curl -s -X POST "$API/api/plt/v1/dev/sign-in" -H 'Content-Type: application/json' -d '{"userId":"underwriter"}' -o "$WORK/token.json"
AUTH="Authorization: Bearer $(json "$WORK/token.json" "d['accessToken']")"

# The body goes through a UTF-8 file so the Greek text survives any shell code page.
"$PY" - "$WORK/party.json" <<'PYEOF'
import json, sys
body = {
    "partyType": "PERSON",
    "person": {"givenNames": "Αγγελική", "familyName": "Ευθυμίου", "fatherName": "Νικόλαος", "birthDate": "1984-03-09"},
    "identifiers": [{"scheme": "AFM", "value": "123456783"}],
    "addresses": [{"types": ["LEGAL", "MAILING"], "country": "GR", "street": "Λεωφ. Κηφισίας", "number": "124",
                   "postcode": "11526", "locality": "Αθήνα"}],
    "contactPoints": [{"type": "EMAIL", "value": "a.efthymiou@example.org"}, {"type": "MOBILE", "value": "6912345678"}],
}
json.dump(body, open(sys.argv[1], "w", encoding="utf-8"), ensure_ascii=False)
PYEOF

echo "--- create (201, or 409 PTY-ERR-DUPLICATE-IDENTIFIER on a second run)"
curl -s -X POST "$API/api/pty/v1/parties" -H "$AUTH" -H 'Content-Type: application/json; charset=utf-8' \
  -H "Idempotency-Key: $(uuid)" --data-binary "@$WORK/party.json" -o "$WORK/created.json" -w '%{http_code}\n'
ID=$(json "$WORK/created.json" "d['party']['partyId'] if 'party' in d else d['existingPartyId']")
echo "party $ID"

# Every search goes in a POST body: names, identifiers and free text are never in a URL (D-SLC-05). The bodies are
# written as \u escapes so non-ASCII text never depends on the shell's code page.
search() {
  echo "--- search $1"
  curl -s -X POST "$API/api/pty/v1/parties/search" -H "$AUTH" -H 'Content-Type: application/json' -d "$2" -o "$WORK/search.json"
  json "$WORK/search.json" "[(i['partyNumber'], i['displayName'], i['displayNameLatin'], i['matchQuality']) for i in d['items']]"
}
search "efthymiou (Latin, lower case)" '{"name":"efthymiou"}'
search "ΕΥΘΥΜΙΟΥ angeliki (Greek capitals without accents + Latin)" '{"name":"\u0395\u03a5\u0398\u03a5\u039c\u0399\u039f\u03a5 angeliki"}'
search "AFM through the blind index" '{"identifierScheme":"AFM","identifierValue":"123456783"}'
echo "--- get (P2 masked)"
curl -s "$API/api/pty/v1/parties/$ID" -H "$AUTH" -o "$WORK/get.json"
json "$WORK/get.json" "(d['party']['partyNumber'], d['party']['birthDate'], d['party']['identifiers'][0]['value'], d['party']['addresses'][0]['formattedLinesLatin'])"
echo "--- get (P2 revealed for purpose RATING; audited)"
curl -s "$API/api/pty/v1/parties/$ID?revealPurpose=RATING" -H "$AUTH" -o "$WORK/reveal.json"
json "$WORK/reveal.json" "(d['party']['birthDate'], d['party']['identifiers'][0]['value'])"

# ---- seed smoke (NX-OT-10): the demo seed runs twice; the second run must change nothing (idempotent) and the data of every
# module must be there. Skip with SMOKE_SEED=0 (the first run takes a few minutes: it binds policies and waits for the worker).
if [ "${SMOKE_SEED:-1}" = "1" ]; then
  HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
  counts() {
    "$PY" - "$API" <<'PYEOF'
import json, sys, urllib.request
api = sys.argv[1]
def get(user, path):
    def req(url, data=None, headers=None):
        r = urllib.request.Request(api + url, data=data, headers=headers or {})
        return json.load(urllib.request.urlopen(r))
    tok = req("/api/plt/v1/dev/sign-in", json.dumps({"userId": user}).encode(), {"Content-Type": "application/json"})["accessToken"]
    return req(path, headers={"Authorization": "Bearer " + tok})
def items(user, path):
    out, cursor = [], None
    while True:
        page = get(user, path + ("&" if "?" in path else "?") + "limit=100" + (f"&cursor={cursor}" if cursor else ""))
        out += page["items"]
        cursor = page.get("nextCursor")
        if not cursor:
            return out
refunds = [i for i in items("billing", "/api/bil/v1/invoices") if i["invoice"]["kind"] == "CREDIT_NOTE"]
c = {
    "invoices": len(items("billing", "/api/bil/v1/invoices")),
    "credit notes": len(refunds),
    "journals": len(items("finance", "/api/fin/v1/journals/query")),
    "complaints": len(items("dpo", "/api/cmp/v1/complaints/query")),
    "dsar cases": len(items("dpo", "/api/cmp/v1/dsar/query")),
    "ri contracts": len(items("riacct", "/api/ri/v1/contracts")),
    "pending refunds": len([i for i in items("billingmgr", "/api/bil/v1/refunds?state=PENDING_APPROVAL")]),
}
print(json.dumps(c, sort_keys=True))
PYEOF
  }
  echo "--- seed run 1"
  "$PY" "$HERE/seed-demo.py" "$API" | tail -4
  first=$(counts); echo "after run 1: $first"
  echo "--- seed run 2 (idempotent: nothing new)"
  "$PY" "$HERE/seed-demo.py" "$API" | tail -4
  second=$(counts); echo "after run 2: $second"
  if [ "$first" != "$second" ]; then echo "SEED SMOKE FAILED: the second run changed the data" >&2; exit 1; fi
  "$PY" -c "
import json, sys
c = json.loads(sys.argv[1])
missing = [k for k in ('invoices', 'journals', 'complaints', 'dsar cases', 'ri contracts', 'pending refunds') if not c[k]]
sys.exit('SEED SMOKE FAILED, no data for: ' + ', '.join(missing) if missing else 0)" "$second"
  echo "seed smoke ok"
fi
