# Greek Friendly Settlement stub (SL4-MKT-FS)

`FriendlySettlementClearingStub` implements `IFriendlySettlementClearing` (SPI 23) for the slice. It is bound only outside
Production (`CountryPackBinding`); in Production the SPI is unbound and CLM answers `CLM-ERR-FS-DISABLED`. State is an
in-memory singleton (lost on restart). Nothing is Settled: decisions carry `legalStatus = Unverified`, `provisional = true`,
clearing value basis `ACTUAL`.

## Configuration (host section `FriendlySettlement:Stub`)

| Key | Meaning |
| --- | --- |
| `OwnInsurerCode` | our insurer code on the member list (default `OWN-INSURER`) |
| `MemberCodes` | list of member insurer codes (organisation party ids); empty by default, so nothing is eligible until seeded |

Environment form for the E2E stack: `FriendlySettlement__Stub__OwnInsurerCode=OWN-INSURER`,
`FriendlySettlement__Stub__MemberCodes__0=OWN-INSURER`, `FriendlySettlement__Stub__MemberCodes__1=ALPHA-INS`.

## Eligibility (all must hold; every failing reason is listed)

Own insurer and counterparty on the member list; accident in Greece; exactly 2 vehicles; material damage only; liability 0/100
(insured fault 0 or 100); amount in EUR and <= 5,000.00. Reason codes: `OWN_INSURER_NOT_MEMBER`, `COUNTERPARTY_NOT_MEMBER`,
`NOT_IN_GREECE`, `VEHICLE_COUNT`, `DAMAGE_TYPE_NOT_MATERIAL_ONLY`, `LIABILITY_NOT_CLEAR`, `CURRENCY_NOT_EUR`, `AMOUNT_ABOVE_LIMIT`.
Rule `GR-FS-ELIGIBILITY-STUB` v`0.1.0`. The limit is the lower of the conflicting PRD candidates (C-01: 6,500 press vs
5,000/15,000 prompt); see `FriendlySettlementClearingStub.LegalSourceRef`.

## Submit

`SubmitAsync` is idempotent on `FsCaseId`; the clearing reference is `STUB-FS-` + the last 12 hex digits of the case id, upper case.
The line is stored as a receivable.

## Notification test format (`ReceiveNotificationAsync`)

`FsNotificationMessage.Format` = `coreins.fs-notification.stub+json`; body (UTF-8 JSON), at-fault notification (we owe):

```json
{
  "clearingReference": "STUB-FS-AB12CD34EF56",
  "policyNumber": "POL-2026-000123",
  "lossDate": "2026-10-01",
  "counterpartyInsurerPartyId": "ALPHA-INS",
  "amount": "3000.00",
  "currency": "EUR",
  "damageType": "MATERIAL_DAMAGE"
}
```

`currency` is optional (default `EUR`). The result has role `AtFaultInsurer`, basis `ACTUAL`. The payable is stored once per
`clearingReference`. A malformed message (unknown format, bad JSON, missing/invalid field, amount <= 0) throws `SpiException`
with category `Validation` and code `FS_NOTIFICATION_MALFORMED` (field name in `Error.Field`); no other exception escapes.

## Settlement statement (`SettlementStatementAsync`)

`period` is half-open `[From, To)` on the accident date. Statement id `STUB-FS-STMT-<yyyyMMdd from>-<yyyyMMdd to>-<counterparty|ALL>`
(deterministic). One line per stored receivable/payable ordered by clearing reference; `LineRef` = `<statementId>-001`...
Net per counterparty = receivables - payables; `Direction` is `Payable` when negative, amount absolute. Example: payable 3,000.00
and receivable 1,200.00 from `ALPHA-INS` give net `Payable 1,800.00`.

```json
{ "statementId": "STUB-FS-STMT-20261001-20261101-ALL",
  "lines": [ { "clearingReference": "STUB-FS-111111111111", "counterpartyCode": "ALPHA-INS", "direction": "RECEIVABLE", "amount": "1200.00" },
             { "clearingReference": "STUB-FS-N1", "counterpartyCode": "ALPHA-INS", "direction": "PAYABLE", "amount": "3000.00" } ],
  "nets": [ { "counterpartyCode": "ALPHA-INS", "direction": "PAYABLE", "amount": "1800.00" } ] }
```

`SubmitDisputeAsync` / `RecordReplyAsync` throw `SpiException` `NotApplicable` / `FS_DISPUTE_NOT_IMPLEMENTED` (OI-CLM-02).
Cyprus (`CountryPacks.CY/Claims`): eligibility returns `NOT_APPLICABLE`; every other operation throws `FS_NOT_APPLICABLE`.
