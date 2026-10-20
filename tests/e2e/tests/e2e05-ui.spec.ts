import { expect, test, type Page } from '@playwright/test';
import { signIn, type Json } from './support/api.js';
import { createParty, importProducts, serverNow } from './support/servicing.js';
import { GR1, dayAt, noonAt } from './support/migration.js';
import { go, signInAs } from './support/ui.js';

// E2E-05 through the migration screens in a real browser (Greek UI, role-based locators; SLICE-PLAN-6, D-SL6-05): the maker pastes a
// GR-1 batch (policy, opening receivable, SEPA mandate) at /admin/migration, dry-runs it and sends it for approval; the same user cannot
// approve (the server's 403 is shown); a second migration manager approves and the items are IMPORTED; the same content in a second batch
// dry-runs as SKIPPED_EXISTING; the routing panel walks a cut-over date by date (an overlap is refused with its code); the first batch is
// reversed (dry run, then for real). Only the parties are set up through the API; nothing is read with SQL here (the API-level spec does).
// Runs through tests/e2e/run-e2e05.sh. Dates are offsets from the dev clock's day when the journey starts.

const MAKER = 'Dev Migration Manager (synthetic)';
const CHECKER = 'Dev Migration Manager 2 - approver (synthetic)';

test.use({
  actionTimeout: 15_000,
  contextOptions: { reducedMotion: process.env['E2E_REDUCED_MOTION'] ? 'reduce' : 'no-preference' },
});

/** The batch id from the detail page's URL. */
const batchIdOf = (page: Page): string => /\/admin\/migration\/batches\/([0-9a-f-]{36})$/.exec(page.url())![1]!;

/** Home page: name + pasted JSON items, create, land on the batch page. */
async function createBatchInUi(page: Page, name: string, items: Json[]): Promise<string> {
  await go(page, '/admin/migration');
  await expect(page.getByRole('heading', { level: 1, name: 'Δέσμες μετάπτωσης και δρομολόγηση' })).toBeVisible();
  await page.getByRole('textbox', { name: /^Όνομα δέσμης/ }).fill(name);
  await page.getByRole('textbox', { name: /^Στοιχεία \(JSON\)/ }).fill(JSON.stringify(items));
  await page.getByRole('button', { name: 'Δημιουργία δέσμης' }).click();
  await expect(page).toHaveURL(/\/admin\/migration\/batches\/[0-9a-f-]{36}$/);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
  return batchIdOf(page);
}

test('E2E-05 migration screens: create, dry run, maker cannot approve, checker approves, repeat is skipped, routing, reversal', async ({ page, request }) => {
  test.setTimeout(600_000);
  const underwriter = await signIn(request, 'underwriter');
  const tag = Date.now().toString();
  await importProducts(request);
  const t0 = await serverNow(request);
  const termStart = noonAt(t0, GR1['termStartOffsetDays']);
  const holder = await createParty(request, underwriter, `gr1ui-${tag}`);
  const second = await createParty(request, underwriter, `gr1uib-${tag}`);
  const legacy = `GR1UI-A-${tag}`;
  const invoice = `INV-UI-${tag}`;
  const mandate = `UMRUI${tag}`;

  const items: Json[] = [
    {
      module: 'POL', objectType: 'Policy', legacyId: legacy,
      payload: {
        legacyNumber: legacy, mode: 'OPENING', policyholderPartyId: holder, product: 'MOTOR-GR', currency: 'EUR', termStart, status: 'IN_FORCE', paymentPlan: 'ANNUAL',
        risk: {
          vehicle: { ...GR1['vehicle'], plate: 'ikx-5101' },
          drivers: (GR1['drivers'] as Json[]).map((d, i) => ({ ...d, partyId: i === 0 ? holder : second })),
          coverages: GR1['coverages'],
        },
        charges: GR1['charges'],
      },
    },
    {
      module: 'BIL', objectType: 'Receivable', legacyId: invoice,
      payload: {
        legacyInvoiceNumber: invoice, payerPartyId: holder, policyLegacyNumber: legacy, dueDate: dayAt(t0, GR1['receivable']['dueOffsetDays']),
        originalAmount: GR1['receivable']['outstanding'] + 10, outstanding: GR1['receivable']['outstanding'],
        openItems: (GR1['receivable']['openItems'] as number[]).map((amount) => ({ amount, description: 'premium instalment' })),
      },
    },
    {
      module: 'BIL', objectType: 'Mandate', legacyId: mandate,
      payload: { mandateReference: mandate, payerPartyId: holder, signatureDate: GR1['mandate']['signatureDate'], ibanMasked: GR1['mandate']['ibanMasked'] },
    },
  ];

  const outcomes = page.getByRole('grid', { name: 'Αποτελέσματα στοιχείων' });

  // ---- 1. The maker creates the batch, dry-runs it, asks for the approval.
  await signInAs(page, MAKER);
  const batchId = await createBatchInUi(page, `GR-1 UI ${tag}`, items);
  await expect(page.getByText('Πρόχειρο', { exact: true }).first()).toBeVisible();
  const start = page.getByRole('button', { name: 'Έναρξη δέσμης' });
  await expect(start, 'the real run needs a dry run of the same content first').toBeDisabled();
  await page.getByRole('button', { name: 'Δοκιμαστική εκτέλεση' }).click();
  await expect(page.getByText('Έγινε, ταιριάζει με το περιεχόμενο')).toBeVisible();
  await expect(start).toBeEnabled();
  await start.click();
  await page.getByRole('button', { name: 'Αίτηση έγκρισης έναρξης' }).click();
  await expect(page.getByText('Σε αναμονή έγκρισης', { exact: true }).first()).toBeVisible();

  // ---- 2. The maker cannot approve their own batch: the server's refusal is shown, the batch stays pending.
  await page.getByRole('button', { name: 'Έγκριση', exact: true }).click();
  await page.getByRole('button', { name: 'Έγκριση και εκτέλεση' }).click();
  await expect(page.getByText('Η ενέργεια δεν ολοκληρώθηκε')).toBeVisible();
  await expect(page.getByText('Σε αναμονή έγκρισης', { exact: true }).first()).toBeVisible();

  // ---- 3. A second migration manager approves; the three items are imported.
  await signInAs(page, CHECKER);
  await go(page, `/admin/migration/batches/${batchId}`);
  await page.getByRole('button', { name: 'Έγκριση', exact: true }).click();
  await page.getByRole('textbox', { name: /^Σχόλιο/ }).fill('GR-1 UI e2e');
  await page.getByRole('button', { name: 'Έγκριση και εκτέλεση' }).click();
  await expect(page.getByText('Ολοκληρώθηκε', { exact: true }).first()).toBeVisible({ timeout: 60_000 });
  for (const legacyId of [legacy, invoice, mandate]) {
    await expect(outcomes.getByRole('row').filter({ hasText: legacyId }).getByText('Εισήχθη')).toBeVisible();
  }
  const counts = page.getByRole('list', { name: 'Αποτελέσματα ανά κατάσταση' });
  await expect(counts.getByRole('listitem').filter({ hasText: 'Εισήχθη' })).toContainText('3');

  // ---- 4. The same content again is skipped (idempotent import): the dry run shows SKIPPED_EXISTING for every item.
  await signInAs(page, MAKER);
  await createBatchInUi(page, `GR-1 UI repeat ${tag}`, items);
  await page.getByRole('button', { name: 'Δοκιμαστική εκτέλεση' }).click();
  await expect(page.getByText('Έγινε, ταιριάζει με το περιεχόμενο')).toBeVisible();
  for (const legacyId of [legacy, invoice, mandate]) {
    await expect(outcomes.getByRole('row').filter({ hasText: legacyId }).getByText('Παραλείφθηκε (υπάρχει)')).toBeVisible();
  }

  // ---- 5. Routing: Transitioning from T0-17d, CoreMaster from T0+31d; an overlapping override is refused with its code.
  const transitioningFrom = dayAt(t0, GR1['routing']['transitioningFromOffsetDays']);
  const coreFrom = dayAt(t0, GR1['routing']['coreMasterFromOffsetDays']);
  await go(page, '/admin/migration');
  await page.getByRole('textbox', { name: /^Κλειδί/ }).fill(legacy);
  const timeline = page.getByRole('list', { name: 'Χρονογραμμή δρομολόγησης' });
  const override = async (stateLabel: string | null, from: string, to: string | null): Promise<void> => {
    await page.getByRole('button', { name: 'Αλλαγή δρομολόγησης' }).click();
    if (stateLabel) {
      await page.getByRole('button', { name: /^Κύριο σύστημα/ }).click();
      await page.getByRole('option', { name: stateLabel }).click();
    }
    await page.getByRole('textbox', { name: /^Από/ }).fill(from);
    await page.getByRole('textbox', { name: /^Έως/ }).fill(to ?? '');
    await page.getByRole('textbox', { name: /^Αιτιολογία/ }).fill('GR-1 cut-over');
    await page.getByRole('button', { name: 'Αποθήκευση δρομολόγησης' }).click();
  };
  await override(null, transitioningFrom, coreFrom); // the default state is Transitioning
  await expect(timeline.getByRole('listitem').filter({ hasText: transitioningFrom }).getByText('Σε μετάβαση')).toBeVisible();

  await override('Κύριο το νέο', dayAt(t0, 0), null);
  await expect(page.getByText('ROUTING-OVERLAP')).toBeVisible();
  // The refused override changed nothing: the form is still open for the corrected one.
  await page.getByRole('textbox', { name: /^Από/ }).fill(coreFrom);
  await page.getByRole('button', { name: 'Αποθήκευση δρομολόγησης' }).click();
  await expect(timeline.getByRole('listitem').filter({ hasText: coreFrom }).getByText('Κύριο το νέο')).toBeVisible();

  // Walk the dates: the day before the first route is the legacy master, the last Transitioning day is the day before CoreMaster.
  const lookup = async (date: string, state: string): Promise<void> => {
    await page.getByRole('textbox', { name: /^Ημερομηνία/ }).fill(date);
    await page.getByRole('button', { name: 'Αναζήτηση' }).click();
    await expect(timeline.getByRole('listitem').filter({ hasText: date }).getByText(state, { exact: true })).toBeVisible();
  };
  await lookup(dayAt(t0, GR1['routing']['transitioningFromOffsetDays'] - 1), 'Κύριο το παλιό');
  await lookup(dayAt(t0, GR1['routing']['coreMasterFromOffsetDays'] - 1), 'Σε μετάβαση');
  await lookup(dayAt(t0, 120), 'Κύριο το νέο');

  // ---- 6. Reversal of the first batch: dry run, then for real; the batch ends REVERSED with every item reversed.
  await go(page, `/admin/migration/batches/${batchId}`);
  await page.getByRole('button', { name: 'Δοκιμαστική αντιστροφή' }).click();
  await expect(page.getByText('Η δοκιμαστική αντιστροφή ολοκληρώθηκε').first()).toBeVisible();
  await page.getByRole('button', { name: 'Αντιστροφή δέσμης' }).click();
  await page.getByRole('button', { name: 'Αντιστροφή της δέσμης' }).click();
  await expect(page.getByText('Η δέσμη αντιστράφηκε.')).toBeVisible({ timeout: 60_000 });
  for (const legacyId of [legacy, invoice, mandate]) {
    await expect(outcomes.getByRole('row').filter({ hasText: legacyId }).getByText('Αντιστράφηκε')).toBeVisible();
  }
});
