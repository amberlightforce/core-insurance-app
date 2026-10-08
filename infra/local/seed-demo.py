"""Demo data for the local stack (synthetic only): one fully paid policy and one open quote. Usage: python infra/local/seed-demo.py [http://127.0.0.1:5000]"""
import json, pathlib, sys, time, uuid, datetime, urllib.request, urllib.error

API = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5000"
PRODUCT = str(pathlib.Path(__file__).resolve().parents[2] / "src" / "CoreIns.Modules.Product" / "Seed" / "motor-gr.product.json")


def call(method, path, body=None, token=None, key=True):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Content-Type", "application/json; charset=utf-8")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    if key and method == "POST":
        req.add_header("Idempotency-Key", str(uuid.uuid4()))
    try:
        with urllib.request.urlopen(req) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, raw.decode(errors="replace")


def token(user):
    s, b = call("POST", "/api/plt/v1/dev/sign-in", {"userId": user}, key=False)
    assert s == 200, (s, b)
    return b["accessToken"]


def must(label, s, b, ok=(200, 201)):
    if s not in ok:
        print(f"!! {label}: {s} {json.dumps(b, ensure_ascii=False)[:800]}")
        sys.exit(1)
    return b


admin, uw, bil = token("admin"), token("underwriter"), token("billing")

# Product (idempotent: an existing locked version is fine)
definition = json.load(open(PRODUCT, encoding="utf-8"))
s, b = call("POST", "/api/pfc/v1/product-versions/import", {"definition": definition, "lock": True}, admin)
print("product:", s, (b or {}).get("status") if isinstance(b, dict) else b)


def person(given, family, birth, afm=None):
    return {
        "partyType": "PERSON",
        "person": {"givenNames": given, "familyName": family, "fatherName": "Γεώργιος", "birthDate": birth},
        "identifiers": [{"scheme": "AFM", "value": afm}] if afm else [],
        "addresses": [{"types": ["LEGAL", "MAILING"], "country": "GR", "street": "Λεωφ. Κηφισίας", "number": "124",
                       "postcode": "11526", "locality": "Αθήνα"}],
        "contactPoints": [{"type": "EMAIL", "value": f"{given.lower()}@example.org", "primary": True}],
        "reason": "NEW_CUSTOMER",
    }


def make_party(given, family, birth):
    b = must("party " + family, *call("POST", "/api/pty/v1/parties", person(given, family, birth), uw))
    return b["party"]["partyId"], b["party"].get("partyNumber")


def quote_for(party_id, usage="PRIVATE"):
    eff = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=1)).replace(microsecond=0)
    sub = must("submission", *call("POST", "/api/pol/v1/submissions", {
        "policyholderPartyId": party_id, "product": "MOTOR-GR", "channel": "STAFF",
        "effectiveAt": eff.strftime("%Y-%m-%dT%H:%M:%SZ"), "quoteType": "FULL"}, uw))
    job = sub["jobId"]
    d1 = must("draft 1", *call("POST", "/api/pol/v1/jobs/update-draft", {"jobId": job, "versionNo": 1, "expectedDraftVersion": 0, "instructions": [
        {"op": "SET_VEHICLE", "vehicle": {"plate": "ikx-1234", "make": "Toyota", "model": "Yaris", "firstRegistrationYear": 2021,
                                          "engineCapacityCc": 1400, "use": usage, "value": {"amount": "15000.00", "currency": "EUR"}}},
        {"op": "SET_ANSWERS", "questionSet": {"questionSetCode": "MOTOR-RISK", "questionSetVersion": "1",
                                              "answers": {"Q-USAGE": usage, "Q-HIRE-REWARD": "NO"}}}]}, uw))
    loc = d1["riskTree"]["vehicles"][0]["locator"]
    must("draft 2", *call("POST", "/api/pol/v1/jobs/update-draft", {"jobId": job, "versionNo": 1, "expectedDraftVersion": 1, "instructions": [
        {"op": "SET_DRIVER", "driver": {"partyId": party_id, "driverType": "MAIN", "yearFirstLicensed": 2010, "vehicleLocator": loc,
                                        "usagePercent": 100, "claimsLast5Years": 0}},
        {"op": "SET_COVERAGES", "coverages": [{"coverageCode": "MTPL", "elementLocator": loc, "selected": True},
                                              {"coverageCode": "OWN-DAMAGE", "elementLocator": loc, "selected": True},
                                              {"coverageCode": "WINDSCREEN", "elementLocator": loc, "selected": True}]}]}, uw))
    q = must("quote", *call("POST", "/api/pol/v1/jobs/quote", {"jobId": job, "versionNo": 1}, uw))
    return job, q


# 1) Fully paid policy
pid, pno = make_party("Ελένη", "Νικολάου", "1984-03-09")
job, q = quote_for(pid)
print("quote:", q.get("state"), q.get("decision"), json.dumps(q.get("totals") or q.get("total") or "", ensure_ascii=False)[:200])
bound = must("bind", *call("POST", "/api/pol/v1/jobs/bind", {"jobId": job, "versionNo": 1, "paymentPlanOption": "ANNUAL", "confirmation": True}, uw))
policy_id = bound.get("policyId")
print("bind:", bound.get("policyNumber"), policy_id, "gates:", bound.get("gateResults"))
if not policy_id:
    sys.exit(1)

# Wait for the worker (outbox) to bill the policy
inv = None
for _ in range(60):
    s, page = call("GET", f"/api/bil/v1/invoices?policyId={policy_id}", token=bil)
    if s == 200 and page and page.get("items"):
        inv = page["items"][0]
        break
    time.sleep(2)
if not inv:
    print("!! no invoice after 120 s")
    sys.exit(1)
invoice_id = inv["invoice"]["invoiceId"]
s, invoice = call("GET", f"/api/bil/v1/invoices/{invoice_id}", token=bil)
total = invoice["invoice"]["total"] if "invoice" in invoice else invoice["total"]
account = invoice["invoice"]["billingAccountId"] if "invoice" in invoice else invoice["billingAccountId"]
print("invoice:", (invoice.get("invoice") or invoice).get("invoiceNumber"), total)

paid = must("payment", *call("POST", "/api/bil/v1/payments/take", {"billingAccountId": account, "amount": total,
                                                                  "method": "BANK_TRANSFER", "bankReference": "DEMO-0001"}, bil))
print("payment:", paid.get("allocationOutcome"))

# 2) An open quote to bind in the UI
pid2, pno2 = make_party("Κώστας", "Παπαδάκης", "1979-11-21")
job2, q2 = quote_for(pid2)
print("open quote:", job2, q2.get("state"))
print(json.dumps({"paidPolicy": bound.get("policyNumber"), "policyId": policy_id, "invoiceId": invoice_id,
                  "billingAccountId": account, "openQuoteJob": job2, "parties": [pno, pno2]}, ensure_ascii=False))
