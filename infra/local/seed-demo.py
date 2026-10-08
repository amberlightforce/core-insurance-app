"""Demo data for the local stack (synthetic only): one fully paid policy and one open quote.
Usage: python infra/local/seed-demo.py [http://127.0.0.1:5000] [--claims]
--claims also leaves, on a motor policy in force, one CLOSED claim with a cleared payment and one OPEN claim with a reserve."""
import json, pathlib, sys, time, uuid, datetime, urllib.request, urllib.error

_args = [a for a in sys.argv[1:] if not a.startswith("--")]
API = _args[0] if _args else "http://127.0.0.1:5000"
WITH_CLAIMS = "--claims" in sys.argv[1:]
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


def quote_for(party_id, usage="PRIVATE", eff=None):
    eff = eff or (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=1)).replace(microsecond=0)
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
summary = {"paidPolicy": bound.get("policyNumber"), "policyId": policy_id, "invoiceId": invoice_id,
           "billingAccountId": account, "openQuoteJob": job2, "parties": [pno, pno2]}


def eur(amount):
    return {"amount": amount, "currency": "EUR"}


def wait_for(label, read, tries=60):
    for _ in range(tries):
        v = read()
        if v:
            return v
        time.sleep(2)
    print("!! timed out waiting for", label)
    sys.exit(1)


def seed_claims():
    """3) Claims flow (E2E-02a): a motor policy whose term has started, a CLOSED paid claim and an OPEN claim with a reserve."""
    handler = token("claims")  # both amounts stay within the handler's illustrative EUR 5,000.00 authority: no approval needed
    iban = "GR1601101250000000012300695"  # synthetic test IBAN
    holder_id, _ = make_party("Μαρία", "Ιωάννου", "1988-07-02")
    # A policy cannot start in the past (REQ-POL-137): start it in a few seconds and wait for the loss time to be inside the term
    start = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=20)).replace(microsecond=0)
    job3, _ = quote_for(holder_id, eff=start)
    bound3 = must("bind (claims policy)", *call("POST", "/api/pol/v1/jobs/bind", {"jobId": job3, "versionNo": 1, "paymentPlanOption": "ANNUAL", "confirmation": True}, uw))
    policy3 = bound3["policyId"]
    print("claims policy:", bound3.get("policyNumber"), "term starts", start.strftime("%H:%M:%S"), "UTC")
    while datetime.datetime.now(datetime.timezone.utc) < start + datetime.timedelta(seconds=2):
        time.sleep(1)
    loss_at = (start + datetime.timedelta(seconds=1)).strftime("%Y-%m-%dT%H:%M:%SZ")

    def fnol(cause):
        return must("fnol", *call("POST", "/api/clm/v1/fnol/submit", {
            "lineOfBusiness": "MOTOR", "policyId": policy3, "lossAt": loss_at, "lossCause": cause, "lossLocation": "Λεωφ. Κηφισίας 124, Αθήνα",
            "description": "Demo claim (synthetic)", "channel": "STAFF", "receiptMedium": "TELEPHONE",
            "incidents": [{"incidentType": "VEHICLE", "vehicleRef": "IKX1234", "drivable": True, "damageAreas": ["FRONT"]}],
            "exposures": [{"kind": "OWN_DAMAGE", "coverageCode": "OWN-DAMAGE"}]}, handler))

    def run_set(claim_id, transactions, label):
        built = must(label + " build", *call("POST", "/api/clm/v1/transaction-sets/build", {"claimId": claim_id, "transactions": transactions}, handler))
        must(label + " submit", *call("POST", "/api/clm/v1/transaction-sets/submit", {"setId": built["setId"]}, handler))
        return built["setId"]

    def line(kind, exposure, amount, **more):
        return {"kind": kind, "exposureId": exposure, "costType": "INDEMNITY", "costCategory": "VEHICLE_REPAIR", "amount": eur(amount), **more}

    # CLOSED claim: reserve 1,200.00 (within the handler's authority), pay it as the final payment, close
    c1 = fnol("COLLISION")
    cid1, insured, exp1 = c1["claimId"], c1["claim"]["insuredPartyId"], c1["exposures"][0]["exposureId"]
    payee = must("payee", *call("POST", "/api/clm/v1/payee-accounts/capture", {"claimId": cid1, "partyId": insured, "iban": iban, "holderName": "Μαρία Ιωάννου"}, handler))["payeeAccount"]["payeeAccountId"]
    run_set(cid1, [line("RESERVE", exp1, "1200.00", reason="INITIAL_ESTIMATE")], "reserve")
    run_set(cid1, [line("PAYMENT", exp1, "1200.00", payeePartyId=insured, payeeAccountId=payee, paymentType="FINAL")], "payment")
    wait_for("payment cleared", lambda: any(p["status"] == "CLEARED" for p in must("payments", *call("GET", f"/api/clm/v1/claims/{cid1}/payments", token=handler, key=False))["items"]))
    claim1 = must("claim", *call("GET", f"/api/clm/v1/claims/{cid1}", token=handler, key=False))
    closed = must("close", *call("POST", "/api/clm/v1/claims/close", {"claimId": cid1, "expectedRecordVersion": claim1["claim"]["summary"]["recordVersion"], "outcome": "COMPLETED"}, handler))
    print("closed claim:", c1["claimNumber"], (closed.get("claim") or closed).get("status"))

    # OPEN claim with a reserve of 2,500.00
    c2 = fnol("GLASS_BREAKAGE")
    run_set(c2["claimId"], [line("RESERVE", c2["exposures"][0]["exposureId"], "2500.00", reason="INITIAL_ESTIMATE")], "open reserve")
    print("open claim:", c2["claimNumber"], "reserve 2500.00")
    return {"claimsPolicy": bound3.get("policyNumber"), "closedClaim": c1["claimNumber"], "openClaim": c2["claimNumber"]}


if WITH_CLAIMS:
    summary["claims"] = seed_claims()
print(json.dumps(summary, ensure_ascii=False))
