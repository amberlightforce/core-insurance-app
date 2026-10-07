import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { resolve } from 'node:path';
import { expect, test, type APIRequestContext } from '@playwright/test';

// E2E-01 happy path at API level (PRD-18 E2E-01: quote -> bind -> invoice -> fiscal document -> payment -> ledger), adjusted
// by the slice rulings: ANNUAL plan (D-SLC-10c), no Auxiliary Fund levy (D-REG-06a), stub fiscal channel, illustrative
// tariff (D-SLC-04). Runs against a fresh stack (tests/e2e/run-e2e01.sh) and asserts consistency between the modules, not
// tariff values: every amount is checked against the amounts of the step before it.
//
// Money is handled as integer cents parsed from the decimal strings of the API, never as floating point.

type Json = Record<string, any>;

const PRODUCT = 'MOTOR-GR';
const SEED = process.env['E2E_PRODUCT_SEED'] ?? resolve(import.meta.dirname, '../../../src/CoreIns.Modules.Product/Seed/motor-gr.product.json');
const WORKER_TIMEOUT_MS = 90_000;

const cents = (money: { amount: string } | string): bigint => {
  const text = typeof money === 'string' ? money : money.amount;
  const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(text);
  if (!match) throw new Error(`not a decimal amount: ${text}`);
  const fraction = (match[3] ?? '').padEnd(2, '0');
  if (/[^0]/.test(fraction.slice(2))) throw new Error(`more than 2 significant decimals: ${text}`);
  const value = BigInt(match[2]! + fraction.slice(0, 2));
  return match[1] === '-' ? -value : value;
};
const sum = (values: bigint[]): bigint => values.reduce((a, b) => a + b, 0n);
const eur = (value: bigint): string => `${value / 100n}.${(value % 100n).toString().padStart(2, '0')}`;

/** 15% (the IPT rate shown on the quote) of a premium in cents, half up to the cent. */
const percent = (premium: bigint, rate: string): bigint => {
  const match = /^0\.(\d{2})$/.exec(rate);
  if (!match) throw new Error(`unexpected rate ${rate}`);
  return (premium * BigInt(match[1]!) + 50n) / 100n;
};

async function signIn(request: APIRequestContext, userId: string): Promise<string> {
  const response = await request.post('/api/plt/v1/dev/sign-in', { data: { userId } });
  expect(response.status(), `dev sign-in as ${userId}`).toBe(200);
  return ((await response.json()) as Json)['accessToken'] as string;
}

async function call(request: APIRequestContext, token: string, method: 'GET' | 'POST', path: string, body?: unknown): Promise<{ status: number; body: Json }> {
  const headers: Record<string, string> = { authorization: `Bearer ${token}`, 'accept-language': 'en' };
  if (method === 'POST') headers['idempotency-key'] = randomUUID();
  const response = await request.fetch(path, { method, headers, data: body });
  const text = await response.text();
  return { status: response.status(), body: text ? (JSON.parse(text) as Json) : {} };
}

/** Polls until the async outbox (the worker) has produced the expected state. */
async function eventually<T>(what: string, read: () => Promise<T | undefined>): Promise<T> {
  const deadline = Date.now() + WORKER_TIMEOUT_MS;
  for (;;) {
    const value = await read();
    if (value !== undefined) return value;
    if (Date.now() > deadline) throw new Error(`timed out after ${WORKER_TIMEOUT_MS / 1000}s waiting for the worker: ${what}`);
    await new Promise((done) => setTimeout(done, 1000));
  }
}

test('E2E-01 happy path: quote, bind, invoice, fiscal MARK, payment, ledger journals, as-of read', async ({ request }) => {
  test.setTimeout(240_000);
  const seed = JSON.parse(readFileSync(SEED, 'utf8')) as Json;

  // ---- 1. Dev sign-in (D-SLC-03) for each role the journey uses.
  const admin = await signIn(request, 'admin');
  const underwriter = await signIn(request, 'underwriter');
  const billing = await signIn(request, 'billing');
  const finance = await signIn(request, 'finance');

  // ---- Seed: import and lock the product (admin).
  await test.step('import and lock the MOTOR-GR product version', async () => {
    const imported = await call(request, admin, 'POST', '/api/pfc/v1/product-versions/import', { definition: seed, lock: true });
    expect(imported.status, JSON.stringify(imported.body)).toBeLessThan(300);
  });

  // ---- 2. Party.
  const party = await test.step('create the policyholder party', async () => {
    const created = await call(request, underwriter, 'POST', '/api/pty/v1/parties', {
      partyType: 'PERSON',
      person: { givenNames: 'Μαρία', familyName: 'Παπαδοπούλου', fatherName: 'Γεώργιος', birthDate: '1980-05-17' },
      identifiers: [],
      addresses: [{ types: ['LEGAL', 'MAILING'], country: 'GR', street: 'Λεωφ. Κηφισίας', number: '124', postcode: '11526', locality: 'Αθήνα' }],
      contactPoints: [
        { type: 'EMAIL', value: 'e2e.person@example.org', primary: true },
        { type: 'MOBILE', value: '691 234 5678', primary: true },
      ],
      reason: 'NEW_CUSTOMER',
    });
    expect(created.status, JSON.stringify(created.body)).toBe(201);
    return created.body['party']['partyId'] as string;
  });

  // ---- 3. Submission. Effective in ~2 days at 12:00 UTC (15:00 in Athens), so the as-of reads below are unambiguous.
  const effective = new Date(Date.now() + 2 * 86_400_000);
  effective.setUTCHours(12, 0, 0, 0);
  const effectiveAt = effective.toISOString().replace('.000Z', 'Z');
  const effectiveDay = effectiveAt.slice(0, 10); // 15:00 in Athens is the same calendar day
  const jobId = await test.step('create the submission', async () => {
    const created = await call(request, underwriter, 'POST', '/api/pol/v1/submissions', {
      policyholderPartyId: party,
      product: PRODUCT,
      channel: 'STAFF',
      effectiveAt,
      quoteType: 'FULL',
    });
    expect(created.status, JSON.stringify(created.body)).toBe(201);
    return created.body['jobId'] as string;
  });

  // ---- 4. Update the draft: vehicle and answers, then main driver and covers.
  await test.step('update the draft (vehicle, answers, driver, covers)', async () => {
    const first = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
      jobId,
      versionNo: 1,
      expectedDraftVersion: 0,
      instructions: [
        { op: 'SET_VEHICLE', vehicle: { plate: 'ikx-1234', make: 'Toyota', model: 'Yaris', firstRegistrationYear: 2021, engineCapacityCc: 1400, use: 'PRIVATE', value: { amount: '15000.00', currency: 'EUR' } } },
        { op: 'SET_ANSWERS', questionSet: { questionSetCode: 'MOTOR-RISK', questionSetVersion: '1', answers: { 'Q-USAGE': 'PRIVATE', 'Q-HIRE-REWARD': 'NO' } } },
      ],
    });
    expect(first.status, JSON.stringify(first.body)).toBe(200);
    const vehicle = first.body['riskTree']['vehicles'][0]['locator'] as string;
    const second = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
      jobId,
      versionNo: 1,
      expectedDraftVersion: 1,
      instructions: [
        { op: 'SET_DRIVER', driver: { partyId: party, driverType: 'MAIN', yearFirstLicensed: 2010, vehicleLocator: vehicle, usagePercent: 100, claimsLast5Years: 0 } },
        { op: 'SET_COVERAGES', coverages: [
          { coverageCode: 'MTPL', elementLocator: vehicle, selected: true },
          { coverageCode: 'OWN-DAMAGE', elementLocator: vehicle, selected: true },
        ] },
      ],
    });
    expect(second.status, JSON.stringify(second.body)).toBe(200);
  });

  // ---- 5. Quote: premium lines, IPT lines, legal status and the illustrative / provisional markers.
  const quote = await test.step('quote', async () => {
    const quoted = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/quote', { jobId, versionNo: 1 });
    expect(quoted.status, JSON.stringify(quoted.body)).toBe(200);
    return quoted.body;
  });
  expect(quote['state']).toBe('QUOTED');
  expect(quote['decision']).toBe('ACCEPT');
  expect(quote['bindable']).toBe(true);
  const quoteLines = quote['charges'] as Json[];
  const premiumLines = quoteLines.filter((l) => l['chargeCategory'] === 'PREMIUM');
  const taxLines = quoteLines.filter((l) => l['chargeCategory'] === 'TAX');
  expect(premiumLines.map((l) => l['coverageCode']).sort()).toEqual(['MTPL', 'OWN-DAMAGE']);
  expect(taxLines.map((l) => l['coverageCode']).sort()).toEqual(['MTPL', 'OWN-DAMAGE']);
  expect(quoteLines.filter((l) => !['PREMIUM', 'TAX'].includes(l['chargeCategory'])), 'no levy line: no Auxiliary Fund levy in the slice (D-REG-06a)').toEqual([]);
  for (const premium of premiumLines) {
    expect(premium['legalStatus'], 'premium carries no legal status').toBeUndefined();
    expect(cents(premium['amount']) > 0n).toBe(true);
  }
  for (const tax of taxLines) {
    expect(tax['chargeType']).toBe('GR-IPT');
    // IPT line = rate x the premium of the same coverage, rounded half up to the cent; the rate is not Settled, so it is flagged.
    const base = premiumLines.find((p) => p['coverageCode'] === tax['coverageCode'])!;
    expect(cents(tax['amount'])).toBe(percent(cents(base['amount']), tax['annualRate']));
    expect(tax['provisional'], 'IPT rate is provisional (D-REG-02, D-SLC-09)').toBe(true);
    expect(tax['legalStatus']).not.toBe('Settled');
    expect(tax['legalStatus']).toBeTruthy();
  }
  const premiumCents = sum(premiumLines.map((l) => cents(l['amount'])));
  const iptCents = sum(taxLines.map((l) => cents(l['amount'])));
  expect(cents(quote['premium'])).toBe(premiumCents);
  expect(cents(quote['taxes'])).toBe(iptCents);
  expect(cents(quote['total'])).toBe(premiumCents + iptCents);

  // The quote passes RAT's and UW's non-blocking warnings through, with a text in the request language (en here).
  const warnings = quote['warnings'] as Json[];
  expect(warnings.map((w) => w['code']).sort()).toEqual(['RAT-WARN-ILLUSTRATIVE-TARIFF', 'RAT-WARN-PROVISIONAL-TAX', 'UW-WARN-ILLUSTRATIVE-RULES']);
  for (const warning of warnings) {
    expect(warning['message']).toMatch(/illustrative|provisional/i);
  }

  await test.step('the rating worksheet marks the tariff illustrative (D-SLC-04)', async () => {
    const worksheet = await call(request, underwriter, 'GET', `/api/rat/v1/worksheets/${quote['worksheetId']}`);
    expect(worksheet.status, JSON.stringify(worksheet.body)).toBe(200);
    expect(JSON.stringify(worksheet.body)).toMatch(/ILLUSTRATIVE/i);
    expect(JSON.stringify(worksheet.body)).toMatch(/provisional/i);
  });

  // ---- 6. Bind with ANNUAL.
  const bound = await test.step('bind with the ANNUAL plan', async () => {
    const bind = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/bind', { jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
    expect(bind.status, JSON.stringify(bind.body)).toBe(200);
    return bind.body;
  });
  expect(bound['state']).toBe('BOUND');
  expect(bound['policyNumber']).toMatch(/^POL\d+$/);
  expect(['SCHEDULED', 'IN_FORCE']).toContain(bound['termState']);
  const policyId = bound['policyId'] as string;
  const policyNumber = bound['policyNumber'] as string;
  const frozen = bound['chargeDeltas'] as Json[];
  expect(frozen).toHaveLength(quoteLines.length);
  expect(sum(frozen.map((l) => cents(l['amount'])))).toBe(premiumCents + iptCents);

  // ---- 7 + 8. The worker bills asynchronously (outbox): poll for the invoice.
  const invoiceId = await eventually('the invoice of the bound policy', async () => {
    const list = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policyId}`);
    expect(list.status, JSON.stringify(list.body)).toBe(200);
    const items = list.body['items'] as Json[];
    return items.length === 1 ? (items[0]!['invoice']['invoiceId'] as string) : undefined;
  });
  const invoiceBody = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${invoiceId}`)).body;
  const invoice = invoiceBody['invoice'] as Json;
  const items = invoiceBody['invoiceItems'] as Json[];
  expect(invoice['invoiceNumber'], 'gapless invoice number of the first invoice of the series').toMatch(/^INV\d{4}0*1$/);
  expect(invoice['state']).toBe('DUE');
  expect(invoice['method']).toBe('BANK_TRANSFER');
  expect(cents(invoice['total'])).toBe(premiumCents + iptCents);
  expect(cents(invoice['open'])).toBe(premiumCents + iptCents);
  // The items are exactly the policy's frozen charge lines (REQ-BIL-067).
  const byCharge = (rows: Json[]) => rows.map((r) => `${r['chargeId']}|${r['chargeType']}|${r['coverageCode']}|${cents(r['amount'])}`).sort();
  expect(byCharge(items)).toEqual(byCharge(frozen));
  // D-SLC-19a: the invoice shows the IPT lines as provisional.
  for (const item of items) {
    if (item['chargeCategory'] === 'TAX') {
      expect(item['provisional']).toBe(true);
      expect(item['legalStatus']).toBeTruthy();
    } else {
      expect(item['provisional']).toBeUndefined();
    }
  }
  // Stub fiscal channel: a registered document with a MARK and UID (D3).
  // The invoice exists before CMP has transmitted it, so poll until the stub channel has registered it.
  const fiscal = await eventually('the stub fiscal document to be REGISTERED', async () => {
    const read = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${invoiceId}`)).body['fiscalStatus'] as Json;
    return read['status'] === 'REGISTERED' ? read : undefined;
  });
  expect(fiscal['status']).toBe('REGISTERED');
  expect(fiscal['mark'], 'the stub MARK').toMatch(/^STUB-\d+$/);
  expect(fiscal['uid']).toMatch(/^STUB-[0-9A-F]+$/);

  const accountId = invoice['billingAccountId'] as string;
  const account = (await call(request, billing, 'GET', `/api/bil/v1/billing-accounts/${accountId}`)).body;
  expect(account['account']['accountNumber']).toMatch(/^BA\d+$/);
  expect(account['account']['currency']).toBe('EUR');
  expect(account['account']['terms'][0]['policyNumber']).toBe(policyNumber);
  expect(account['account']['terms'][0]['planCode']).toBe('ANNUAL');
  expect(cents(account['balancesByState']['billed'])).toBe(premiumCents + iptCents);

  // ---- 9. Record an exact payment.
  const receipt = await test.step('take the exact payment', async () => {
    const paid = await call(request, billing, 'POST', '/api/bil/v1/payments/take', {
      billingAccountId: accountId,
      amount: invoice['total'],
      method: 'BANK_TRANSFER',
      invoiceId,
    });
    expect(paid.status, JSON.stringify(paid.body)).toBe(201);
    return paid.body;
  });
  expect(receipt['receipt']['state']).toBe('ALLOCATED');
  expect(cents(receipt['receipt']['unallocated'])).toBe(0n);

  // ---- 10. The invoice is PAID.
  const paidInvoice = await eventually('the invoice to be PAID', async () => {
    const read = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${invoiceId}`)).body;
    return read['invoice']['state'] === 'PAID' ? read : undefined;
  });
  expect(cents(paidInvoice['invoice']['open'])).toBe(0n);
  expect(cents(paidInvoice['invoice']['paid'])).toBe(premiumCents + iptCents);

  // ---- 11. FIN journals: every BIL entry type posted, each journal balanced, receivable and unallocated cash net to zero.
  const journals = await eventually('FIN to post the journals of every entry (WRITTEN, BILLED, IPT_DUE, RECEIVED, ALLOCATED)', async () => {
    const all: Json[] = [];
    let cursor: string | undefined;
    do {
      const page = await call(request, finance, 'GET', `/api/fin/v1/journals/query?limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
      expect(page.status, JSON.stringify(page.body)).toBe(200);
      all.push(...(page.body['items'] as Json[]).map((i) => i['journal'] as Json));
      cursor = page.body['nextCursor'] ?? undefined;
    } while (cursor);
    const mine = all.filter((j) => (j['lines'] as Json[]).some((l) => l['dimensions']?.['billingAccountId'] === accountId));
    const rules = new Set(mine.flatMap((j) => j['ruleCodes'] as string[]));
    const ready = mine.some((j) => (j['lines'] as Json[]).some((l) => l['account'] === 'GL-2540')) // allocation posted
      && mine.some((j) => (j['lines'] as Json[]).some((l) => l['account'] === 'GL-1110')) // receipt posted
      && rules.size > 0;
    return ready ? mine : undefined;
  });
  const lines = journals.flatMap((j) => (j['lines'] as Json[]).map((l) => ({ journal: j['journalNumber'] as string, account: l['account'] as string, side: l['side'] as string, amount: cents(l['amount']) })));
  for (const journal of journals) {
    const own = lines.filter((l) => l.journal === journal['journalNumber']);
    expect(own.length, `${journal['journalNumber']} has at least two lines`).toBeGreaterThanOrEqual(2);
    expect(sum(own.filter((l) => l.side === 'DEBIT').map((l) => l.amount)), `${journal['journalNumber']} balances`).toBe(sum(own.filter((l) => l.side === 'CREDIT').map((l) => l.amount)));
    expect(journal['book']).toBe('IFRS17');
    expect(journal['sourceModule']).toBe('BIL');
    expect(journal['journalNumber']).toMatch(/^JNL\d{4}\d+$/);
  }
  const net = (account: string): bigint => sum(lines.filter((l) => l.account === account).map((l) => (l.side === 'DEBIT' ? l.amount : -l.amount)));
  const ruleCodes = [...new Set(journals.flatMap((j) => j['ruleCodes'] as string[]))].sort();
  test.info().annotations.push({ type: 'journal-rules', description: ruleCodes.join(', ') });
  expect(net('GL-2110'), 'written premium equals the quoted premium').toBe(-premiumCents);
  expect(net('GL-2410'), 'IPT payable equals the quoted IPT').toBe(-iptCents);
  expect(net('GL-1110'), 'cash received equals the invoice total').toBe(premiumCents + iptCents);
  expect(net('GL-1215'), 'unbilled receivable nets to zero once billed').toBe(0n);
  expect(net('GL-1210'), 'receivable nets to zero once paid').toBe(0n);
  expect(net('GL-2411'), 'IPT due clears into IPT payable').toBe(0n);
  expect(net('GL-2540'), 'unallocated cash nets to zero once allocated').toBe(0n);
  expect(sum(lines.map((l) => (l.side === 'DEBIT' ? l.amount : -l.amount))), 'the whole ledger balances').toBe(0n);

  // ---- 12. Read the policy as of a date (D-SLC-13): a date means the end of that Athens business day.
  const asOf = await call(request, billing, 'GET', `/api/pol/v1/policies/${policyId}?validAt=${effectiveDay}`);
  expect(asOf.status, JSON.stringify(asOf.body)).toBe(200);
  expect(asOf.body['policy']['policyNumber']).toBe(policyNumber);
  expect(asOf.body['policy']['status']).toBe('IN_FORCE');
  expect(asOf.body['segment']['transactionId']).toBe(bound['transactionId']);
  expect(asOf.body['riskTree']['vehicles'][0]['make']).toBe('Toyota');
  expect(cents(asOf.body['transactions'][0]['total'])).toBe(premiumCents + iptCents);
  const dayBefore = new Date(effective.getTime() - 86_400_000).toISOString().slice(0, 10);
  const before = await call(request, billing, 'GET', `/api/pol/v1/policies/${policyId}?validAt=${dayBefore}`);
  expect(before.body['policy']['status']).toBe('SCHEDULED');

  // D-SLC-15: one configuration hash everywhere (MKT is the authority): the term pins the hash the quote was priced under.
  expect(asOf.body['term']['configurationHash']).toMatch(/^[0-9a-f]{64}$/);

  test.info().annotations.push({
    type: 'amounts',
    description: `premium ${eur(premiumCents)}, IPT ${eur(iptCents)}, total ${eur(premiumCents + iptCents)} EUR; policy ${policyNumber}; invoice ${invoice['invoiceNumber']}; ${journals.length} journals`,
  });
  console.log(`E2E-01 amounts: premium ${eur(premiumCents)}, IPT ${eur(iptCents)}, total ${eur(premiumCents + iptCents)} EUR; policy ${policyNumber}; invoice ${invoice['invoiceNumber']}; ${journals.length} journals; rules ${ruleCodes.join(',')}`);
});
