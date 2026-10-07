#!/usr/bin/env bash
# Smoke test of the local stack over HTTP (synthetic data): dev sign-in, create a person, search it three ways,
# read it masked and revealed. Needs curl and python3 (or python). Usage: infra/local/smoke.sh [api-base-url]
set -euo pipefail
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

# Queries are percent-encoded here (UTF-8), so non-ASCII text never depends on the shell's code page.
search() {
  echo "--- search $1"
  curl -s "$API/api/pty/v1/parties/search?$2" -H "$AUTH" -o "$WORK/search.json"
  json "$WORK/search.json" "[(i['partyNumber'], i['displayName'], i['displayNameLatin'], i['matchQuality']) for i in d['items']]"
}
search "efthymiou (Latin, lower case)" "name=efthymiou"
search "ΕΥΘΥΜΙΟΥ angeliki (Greek capitals without accents + Latin)" "name=%CE%95%CE%A5%CE%98%CE%A5%CE%9C%CE%99%CE%9F%CE%A5%20angeliki"
search "AFM through the blind index" "identifierScheme=AFM&identifierValue=123456783"

echo "--- get (P2 masked)"
curl -s "$API/api/pty/v1/parties/$ID" -H "$AUTH" -o "$WORK/get.json"
json "$WORK/get.json" "(d['party']['partyNumber'], d['party']['birthDate'], d['party']['identifiers'][0]['value'], d['party']['addresses'][0]['formattedLinesLatin'])"
echo "--- get (P2 revealed for purpose RATING; audited)"
curl -s "$API/api/pty/v1/parties/$ID?revealPurpose=RATING" -H "$AUTH" -o "$WORK/reveal.json"
json "$WORK/reveal.json" "(d['party']['birthDate'], d['party']['identifiers'][0]['value'])"
