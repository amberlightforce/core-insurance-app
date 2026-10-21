"""Demo data for the local stack (synthetic only), created through the public API only (NX-OT-10).
Usage: python infra/local/seed-demo.py [http://127.0.0.1:5000] [--basic] [--claims] [--delinquency]

Default: the basic data (one fully paid policy, one open quote) plus the extended data of the other modules:
  billing    an unpaid invoice, a payment in suspense, a cancelled policy with its refund PENDING approval
  finance    the journals those postings produce (verified, FIN posts them from the billing events)
  work       a pack-rollback review and PSR activities (WRK queues)
  complaints one RECEIVED and one UNDER_INVESTIGATION complaint (CMP)
  DSAR       an ACCESS case with identity verified and the module fan-out done (CMP)
  RI         one approved XoL treaty in the registry (RI); premium cessions need a quota-share treaty that only exists as
             configuration (Reinsurance:Cession), so none are created here
  migration  one dry-run batch (MIG, a FIN opening balance; never applied)
--basic       only the basic data (the original seed)
--claims      also, on a motor policy in force, one CLOSED claim with a cleared payment and one OPEN claim with a reserve
--delinquency also a delinquency case: moves the dev clock 12 days forward (forward-only!) and runs the overdue sweep; needs
              Platform__Time__Mode=Shiftable in the stack's environment, otherwise this step is skipped

Idempotent: every block looks for its own data first (parties by name, then the records of that party) and does nothing
when it is there, so the script can run again against a database that already has demo data (also the old basic data)."""
import json, pathlib, sys, time, uuid, datetime, urllib.request, urllib.error

_args = [a for a in sys.argv[1:] if not a.startswith("--")]
API = _args[0] if _args else "http://127.0.0.1:5000"
WITH_CLAIMS = "--claims" in sys.argv[1:]
BASIC_ONLY = "--basic" in sys.argv[1:]
WITH_DELINQUENCY = "--delinquency" in sys.argv[1:]
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")
    except Exception:
        pass
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


def find_party(display_name):
    """(partyId, partyNumber) of the party with exactly this display name, or None. It is the anchor of every idempotent block."""
    s, b = call("POST", "/api/pty/v1/parties/search", {"name": display_name}, uw, key=False)
    if s != 200:
        return None
    for item in b.get("items", []):
        if item.get("displayName") == display_name:
            return item["partyId"], item.get("partyNumber")
    return None


def ensure_party(given, family, birth):
    """(partyId, partyNumber, created): finds the party by name or creates it."""
    found = find_party(f"{given} {family}")
    if found:
        return found[0], found[1], False
    b = must("party " + family, *call("POST", "/api/pty/v1/parties", person(given, family, birth), uw))
    return b["party"]["partyId"], b["party"].get("partyNumber"), True


def quote_for(party_id, usage="PRIVATE", eff=None, plate="ikx-1234"):
    eff = eff or (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=1)).replace(microsecond=0)
    sub = must("submission", *call("POST", "/api/pol/v1/submissions", {
        "policyholderPartyId": party_id, "product": "MOTOR-GR", "channel": "STAFF",
        "effectiveAt": eff.strftime("%Y-%m-%dT%H:%M:%SZ"), "quoteType": "FULL"}, uw))
    job = sub["jobId"]
    d1 = must("draft 1", *call("POST", "/api/pol/v1/jobs/update-draft", {"jobId": job, "versionNo": 1, "expectedDraftVersion": 0, "instructions": [
        {"op": "SET_VEHICLE", "vehicle": {"plate": plate, "make": "Toyota", "model": "Yaris", "firstRegistrationYear": 2021,
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


def bind_policy(party_id, plate):
    job, _ = quote_for(party_id, plate=plate)
    return must("bind " + plate, *call("POST", "/api/pol/v1/jobs/bind", {"jobId": job, "versionNo": 1, "paymentPlanOption": "ANNUAL", "confirmation": True}, uw))


def invoice_of(policy_id):
    """Waits for the worker (outbox) to bill the policy; returns (invoiceId, billingAccountId, total money, invoiceNumber)."""
    def read():
        s, page = call("GET", f"/api/bil/v1/invoices?policyId={policy_id}", token=bil, key=False)
        return page["items"][0] if s == 200 and page and page.get("items") else None
    inv = wait_for("the invoice of " + policy_id, read)["invoice"]
    return inv["invoiceId"], inv["billingAccountId"], inv["total"], inv.get("invoiceNumber")


def two_decimals(amount):
    """'494.5100' -> '494.51' (the API answers four decimals, requests take two)."""
    return f"{float(amount):.2f}"


summary = {}

# ---------------------------------------------------------------------------------------------------------------- basic
# 1) Fully paid policy
pid, pno, created = ensure_party("Ελένη", "Νικολάου", "1984-03-09")
subject_party = pid
if created:
    job, q = quote_for(pid)
    print("quote:", q.get("state"), q.get("decision"), json.dumps(q.get("totals") or q.get("total") or "", ensure_ascii=False)[:200])
    bound = must("bind", *call("POST", "/api/pol/v1/jobs/bind", {"jobId": job, "versionNo": 1, "paymentPlanOption": "ANNUAL", "confirmation": True}, uw))
    policy_id = bound.get("policyId")
    print("bind:", bound.get("policyNumber"), policy_id, "gates:", bound.get("gateResults"))
    if not policy_id:
        sys.exit(1)
    invoice_id, account, total, inv_no = invoice_of(policy_id)
    print("invoice:", inv_no, total)
    paid = must("payment", *call("POST", "/api/bil/v1/payments/take", {"billingAccountId": account, "amount": total,
                                                                      "method": "BANK_TRANSFER", "bankReference": "DEMO-0001"}, bil))
    print("payment:", paid.get("allocationOutcome"))
    summary.update({"paidPolicy": bound.get("policyNumber"), "policyId": policy_id, "invoiceId": invoice_id, "billingAccountId": account})
else:
    print("paid policy: party", pno, "exists, skipped")

# 2) An open quote to bind in the UI
pid2, pno2, created2 = ensure_party("Κώστας", "Παπαδάκης", "1979-11-21")
if created2:
    job2, q2 = quote_for(pid2)
    print("open quote:", job2, q2.get("state"))
    summary["openQuoteJob"] = job2
else:
    print("open quote: party", pno2, "exists, skipped")
summary["parties"] = [pno, pno2]


def seed_claims():
    """3) Claims flow (E2E-02a): a motor policy whose term has started, a CLOSED paid claim and an OPEN claim with a reserve."""
    handler = token("claims")  # both amounts stay within the handler's illustrative EUR 5,000.00 authority: no approval needed
    iban = "GR1601101250000000012300695"  # synthetic test IBAN
    holder_id, _, created_holder = ensure_party("Μαρία", "Ιωάννου", "1988-07-02")
    if not created_holder:
        print("claims: party exists, skipped")
        return {}
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

# ------------------------------------------------------------------------------------------------------------- extended
IBAN = "GR1601101250000000012300695"  # synthetic test IBAN
posted_policies = []  # policy numbers whose FIN journals are checked at the end


def seed_billing():
    """Billing: a refund PENDING approval (after a cancellation), an unpaid invoice and a payment in suspense."""
    out = {}
    # (a) A paid policy that is cancelled at once: the credit note is refunded to a payee account that changed (a second account
    #     of the policyholder, still in its cooling-off), so the refund is referred to the billing manager and waits PENDING.
    pid_a, _, new_a = ensure_party("Σοφία", "Κωνσταντίνου", "1990-01-15")
    if new_a:
        start = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=20)).replace(microsecond=0)  # a term cannot be cancelled before it starts
        job_a, _ = quote_for(pid_a, eff=start, plate="iky-2001")
        bound = must("bind (refund policy)", *call("POST", "/api/pol/v1/jobs/bind", {"jobId": job_a, "versionNo": 1, "paymentPlanOption": "ANNUAL", "confirmation": True}, uw))
        invoice_id, account, total, inv_no = invoice_of(bound["policyId"])
        must("payment", *call("POST", "/api/bil/v1/payments/take", {"billingAccountId": account, "amount": total, "method": "BANK_TRANSFER",
                                                                   "invoiceId": invoice_id, "bankReference": "DEMO-REFUND"}, bil))
        while datetime.datetime.now(datetime.timezone.utc) < start + datetime.timedelta(seconds=3):
            time.sleep(1)
        now = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).strftime("%Y-%m-%dT%H:%M:%SZ")
        must("cancel", *call("POST", "/api/pol/v1/cancellations", {"policyId": bound["policyId"], "source": "Policyholder", "reasonCode": "CUSTOMER_REQUEST",
                                                                  "effectiveAt": now, "kind": "STANDARD"}, uw))
        wait_for("the credit note", lambda: any(i["invoice"]["kind"] == "CREDIT_NOTE" for i in must("invoices", *call("GET", f"/api/bil/v1/invoices?policyId={bound['policyId']}", token=bil, key=False))["items"]))
        for iban in ("GR9608100010000001234567890", IBAN):  # the second one is the change
            payee = must("payee account", *call("POST", "/api/bil/v1/payee-accounts", {"partyId": pid_a, "purpose": "REFUND", "iban": iban, "holderName": "Σοφία Κωνσταντίνου",
                                                                                    "source": "STAFF", "evidenceRef": "demo-iban-letter"}, bil))
        refund = must("refund propose", *call("POST", "/api/bil/v1/refunds/propose", {"billingAccountId": account, "payeeAccountId": payee["payeeAccountId"], "reasonCode": "POLICY_CANCELLED"}, bil))["refund"]
        print("refund:", bound.get("policyNumber"), "cancelled,", refund["amount"]["amount"], "EUR", refund["state"], refund["approvalState"])
        out["refund"] = {"policy": bound.get("policyNumber"), "refundId": refund["refundId"], "state": refund["state"]}
        posted_policies.append(bound.get("policyNumber"))
    else:
        print("billing: refund case exists, skipped")
    # (b) Unpaid invoice (the subject of the delinquency case)
    pid_b, _, new_b = ensure_party("Δημήτρης", "Αναστασίου", "1975-06-30")
    if new_b:
        bound = bind_policy(pid_b, "iky-2002")
        invoice_id, account, total, inv_no = invoice_of(bound["policyId"])
        print("unpaid:", bound.get("policyNumber"), inv_no, total["amount"], "open")
        out["unpaid"] = {"policy": bound.get("policyNumber"), "invoiceId": invoice_id, "billingAccountId": account}
        posted_policies.append(bound.get("policyNumber"))
    else:
        print("billing: unpaid invoice exists, skipped")
    # (c) Half of the invoice amount paid: it matches no invoice, so the receipt waits in suspense (AMOUNT_MISMATCH)
    pid_c, _, new_c = ensure_party("Νίκος", "Βασιλείου", "1968-09-04")
    if new_c:
        bound = bind_policy(pid_c, "iky-2003")
        invoice_id, account, total, inv_no = invoice_of(bound["policyId"])
        part = two_decimals(float(total["amount"]) / 2)
        paid = must("partial payment", *call("POST", "/api/bil/v1/payments/take", {"billingAccountId": account, "amount": eur(part), "method": "BANK_TRANSFER",
                                                                                  "invoiceId": invoice_id, "bankReference": "DEMO-PARTIAL"}, bil))
        print("suspense:", bound.get("policyNumber"), inv_no, "received", part, "of", total["amount"], "->", paid.get("allocationOutcome"))
        out["suspense"] = {"policy": bound.get("policyNumber"), "invoiceId": invoice_id}
        posted_policies.append(bound.get("policyNumber"))
    else:
        print("billing: suspense payment exists, skipped")
    return out


def seed_delinquency():
    """Billing: a delinquency case on the unpaid invoices. Needs the shiftable dev clock; the clock only moves forward (12 days)."""
    s, _ = call("GET", "/api/plt/v1/dev/clock", token=admin, key=False)
    if s != 200:
        print("delinquency: skipped (the dev clock is not shiftable: set Platform__Time__Mode=Shiftable in the stack environment)")
        return {"skipped": "clock is not shiftable"}
    s, cases = call("GET", "/api/bil/v1/delinquencies?limit=200", token=bil, key=False)
    if s == 200 and cases.get("items"):
        print("delinquency: a case exists already, skipped")
        return {"existing": len(cases["items"])}
    must("advance clock", *call("POST", "/api/plt/v1/dev/clock/advance", {"days": 12, "hours": 0, "minutes": 0, "seconds": 0}, admin))
    s, swept = call("POST", "/dev/bil/delinquency/sweep", token=bil, key=False)
    print("delinquency: clock +12 days; overdue sweep", s, swept)
    return {"swept": swept}


def seed_work():
    """Work: a pack-rollback review (queue PACK-ROLLBACK-REVIEW) and PSR review activities (queue PSR). A duplicate answers 409: fine."""
    ns = uuid.UUID("5e0ed000-0000-4000-8000-0000000d3a0e")  # fixed, so the primary objects (and so the dedupe key) are the same on every run
    uwsenior, billingmgr = token("uwsenior"), token("billingmgr")
    specs = [
        (uwsenior, "WRK-MKT-PACK-ROLLBACK-REVIEW", "MKT", "PackVersion", "demo-pack-gr-1.1", "demo-seed-pack-rollback"),
        (uwsenior, "WRK-POL-NONPAY-PSR-EXCEPTION", "POL", "PolicyTerm", str(uuid.uuid5(ns, "psr-exception")), "demo-seed-psr-exception"),
        (billingmgr, "WRK-BIL-NONPAY-PSR-HOLD", "BIL", "BillingAccount", str(uuid.uuid5(ns, "psr-hold")), "demo-seed-psr-hold"),
        (billingmgr, "WRK-BIL-NONPAY-NOTICE-RETURNED", "BIL", "BillingAccount", str(uuid.uuid5(ns, "notice-returned")), "demo-seed-notice-returned"),
    ]
    made = 0
    for tok, pattern, module, otype, oid, cause in specs:
        s, b = call("POST", "/api/wrk/v1/activities", {"patternCode": pattern, "primaryObject": {"module": module, "type": otype, "id": oid},
                                                       "raisingModule": module, "causeRef": cause}, tok)
        if s == 200:
            made += 1
        elif s != 409:
            must("activity " + pattern, s, b)
    print(f"work: {made} activities created, {len(specs) - made} existed already")
    return {"created": made}


COMPLAINTS = [
    ("Καθυστέρηση στην αποζημίωση της ζημιάς του οχήματός μου (demo seed, synthetic).", "INDIVIDUAL", False, True),
    ("Αμφισβήτηση χρέωσης ασφαλίστρου στο τιμολόγιο (demo seed, synthetic).", "INDIVIDUAL", True, False),
]


def seed_complaints():
    """Compliance: two complaints, one UNDER_INVESTIGATION (classified, acknowledged, one action) and one just RECEIVED (vulnerable flag)."""
    dpo = token("dpo")
    existing, cursor = set(), None
    while True:
        s, page = call("GET", "/api/cmp/v1/complaints/query?limit=100" + (f"&cursor={cursor}" if cursor else ""), token=dpo, key=False)
        if s != 200:
            break
        existing |= {i["complaint"]["description"] for i in page["items"]}
        cursor = page.get("nextCursor")
        if not cursor:
            break
    made = []
    for text, kind, vulnerable, investigate in COMPLAINTS:
        if text in existing:
            continue
        c = must("complaint", *call("POST", "/api/cmp/v1/complaints", {
            "complainantPartyId": subject_party, "complainantType": kind, "channel": "EMAIL", "description": text, "language": "el",
            "productLine": "MOTOR", "vulnerable": vulnerable}, dpo))["complaint"]
        if investigate:
            must("classify", *call("POST", "/api/cmp/v1/complaints/classify", {"id": c["complaintId"], "category": "CLAIM_HANDLING", "cause": "DELAY", "productLine": "MOTOR"}, dpo))
            must("acknowledge", *call("POST", "/api/cmp/v1/complaints/acknowledge", {"id": c["complaintId"], "note": "Acknowledged by phone"}, dpo))
            must("action", *call("POST", "/api/cmp/v1/complaints/add-action", {"id": c["complaintId"], "kind": "REQUEST_INFO", "note": "Asked the claims handler for the file status"}, dpo))
        made.append(c["number"])
    print(f"complaints: created {made}, {len(COMPLAINTS) - len(made)} existed already")
    return {"created": made}


def seed_dsar():
    """Compliance: an ACCESS data-subject request for the first demo customer: identity verified, module fan-out started (IN_REVIEW)."""
    dpo = token("dpo")
    cursor = None
    while True:
        s, page = call("GET", "/api/cmp/v1/dsar/query?limit=100" + (f"&cursor={cursor}" if cursor else ""), token=dpo, key=False)
        if s != 200:
            break
        if any(i["request"]["subjectPartyId"] == subject_party and i["request"]["requestType"] == "ACCESS" for i in page["items"]):
            print("dsar: an ACCESS case exists already, skipped")
            return {"existing": True}
        cursor = page.get("nextCursor")
        if not cursor:
            break
    created = must("dsar", *call("POST", "/api/cmp/v1/dsar", {"subjectPartyId": subject_party, "requestType": "ACCESS", "notes": "Demo data-subject request (synthetic)"}, dpo))
    dsar_id = created["request"]["dsarId"]
    must("verify identity", *call("POST", "/api/cmp/v1/dsar/verify-identity", {"id": dsar_id, "method": "ID_DOCUMENT"}, dpo))
    fan = must("fan-out", *call("POST", "/api/cmp/v1/dsar/start-fan-out", {"id": dsar_id}, dpo))
    print("dsar: ACCESS case", dsar_id, fan["request"]["status"], f"{len(fan['tasks'])} module tasks")
    return {"dsarId": dsar_id, "status": fan["request"]["status"]}


def seed_ri():
    """Reinsurance: one approved XoL per-risk treaty (maker riacct, checker rimgr) with a synthetic reinsurer."""
    riacct, rimgr = token("riacct"), token("rimgr")
    name = "Demo Reinsurance SE (synthetic)"
    if find_party(name):
        print("ri: treaty exists already, skipped")
        return {"existing": True}
    reinsurer = must("reinsurer party", *call("POST", "/api/pty/v1/parties", {"partyType": "ORGANISATION", "organisation": {"legalName": name}}, uw))["party"]["partyId"]
    year = datetime.date.today().year
    created = must("treaty", *call("POST", "/api/ri/v1/contracts", {
        "legalEntity": "GR-TEST", "contractType": "XOL_PER_RISK", "contractYear": year, "currency": "EUR",
        "period": {"from": f"{year}-01-01", "to": f"{year + 1}-01-01"},
        "scope": {"productCodes": ["MOTOR-GR"], "coverageCodes": ["MTPL", "OWN-DAMAGE"]},
        "clause": {"alaeIncluded": True, "statutoryInterestIncluded": False, "recoveriesInure": "REALISED_ONLY"},
        "layers": [{"layerNo": 1, "attachment": eur("200000.00"), "limit": eur("800000.00"), "aad": eur("0.00")}],
        "participations": [{"reinsurerPartyId": reinsurer, "signedLinePct": "100", "lead": True}], "placedPct": "100"}, riacct))["contract"]
    submitted = must("submit treaty", *call("POST", "/api/ri/v1/contracts/submit", {"contractId": created["contractId"], "expectedRecordVersion": created["recordVersion"]}, riacct))
    approved = must("approve treaty", *call("POST", "/api/ri/v1/contracts/approve", {"contractId": created["contractId"], "expectedRecordVersion": submitted["contract"]["recordVersion"], "decision": "APPROVE"}, rimgr))
    print("ri: XoL treaty", created["contractId"], approved["contract"]["status"])
    return {"contractId": created["contractId"], "status": approved["contract"]["status"]}


def seed_migration():
    """Migration: one dry-run batch (a FIN opening balance). It is never started for real; a cross-reference marks it as seeded."""
    maker = token("migmgr")
    s, known = call("POST", "/api/mig/v1/xrefs/resolve", {"objectType": "Batch", "source": "DEMOSEED", "legacyKey": "dry-run-1"}, maker)
    if s == 200 and (known or {}).get("coreId"):
        print("migration: dry-run batch exists already, skipped")
        return {"existing": True}
    today = datetime.date.today().isoformat()
    item = {"module": "FIN", "objectType": "OpeningBalance", "legacyId": "DEMO-TB-1", "payload": {
        "legacyId": "DEMO-TB-1", "accountingDate": today, "currency": "EUR",
        "lines": [{"accountCode": "GL-1210", "debit": 600.50, "credit": 0}, {"accountCode": "GL-1215", "debit": 399.50, "credit": 0},
                  {"accountCode": "GL-2110", "debit": 0, "credit": 1000.00}],
        "trialBalance": {"debit": 1000.00, "credit": 1000.00}}}
    batch = must("batch", *call("POST", "/api/mig/v1/batches", {"name": "Demo opening balance (dry run, synthetic)", "items": [item]}, maker))["batch"]["batchId"]
    dry = must("dry run", *call("POST", "/api/mig/v1/batches/start", {"batchId": batch, "dryRun": True}, maker))
    must("xref", *call("POST", "/api/mig/v1/xrefs/register", {"source": "DEMOSEED", "objectType": "Batch", "legacyKey": "dry-run-1", "coreId": batch, "batchId": batch}, maker))
    print("migration: dry-run batch", batch, [o.get("outcome") for o in dry.get("outcomes", [])])
    return {"batchId": batch}


def check_finance():
    """Finance: FIN posts its journals from the billing events of the policies above; wait for them and report."""
    fin = token("finance")
    out = {}
    for number in posted_policies + ([summary["paidPolicy"]] if summary.get("paidPolicy") else []):
        def read(number=number):
            s, page = call("GET", f"/api/fin/v1/journals/query?policyNumber={number}&limit=100", token=fin, key=False)
            return page["items"] if s == 200 and page and page.get("items") else None
        out[number] = len(wait_for("journals of " + number, read))
    if out:
        print("finance: journals per policy", out)
    return out


if not BASIC_ONLY:
    ext = {}
    for label, step in (("billing", seed_billing), ("work", seed_work), ("complaints", seed_complaints), ("dsar", seed_dsar), ("ri", seed_ri), ("migration", seed_migration)):
        ext[label] = step()
    ext["finance"] = check_finance()
    if WITH_DELINQUENCY:
        ext["delinquency"] = seed_delinquency()
    summary["extended"] = ext
print(json.dumps(summary, ensure_ascii=False))
