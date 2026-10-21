import { expect, test } from '@playwright/test';
import { call, cents, eventually, signIn, type Json } from './support/api.js';
import { importProducts, issuePolicy, type Issued } from './support/servicing.js';
import { addDays, advanceToAthens, athensDate, concludedDate, readWithdrawal } from './support/withdrawal.js';
import { go, signInAs } from './support/ui.js';

// E2E-08 through the withdrawal screens in a real browser (Greek UI, role-based locators; SLICE-PLAN-6, D-SL6-02, D-SL6-06): a paid policy
// is withdrawn on the last day of the 14-day window (the dev clock is moved to 23:58:30 Europe/Athens first, so the two UI steps fit
// before midnight): /channels/withdrawal validates the form, declares, shows the receipt with the confirmation token (dev builds return
// it) and confirms; POL voids the term, BIL credits the whole amount paid including IPT; staff find the request in /channels/withdrawals,
// open it and close it as processed. A declaration after midnight is shown as expired (CHN-ERR-RIGHT-EXPIRED) and voids nothing; a role
// without withdrawal access gets the no-permission state. DST, double confirm and late confirm of an open declaration stay in the
// API-level spec. Setup (two paid policies, the clock) goes through the API as there. Runs through tests/e2e/run-e2e08.sh.

const UNDERWRITER = 'Dev Underwriter (synthetic)';
const CLAIMS = 'Dev Claims Handler (synthetic)';
const WINDOW_DAYS = 14; // ILLUSTRATIVE (D-SL6-06), the CHN and POL default

test.use({
  actionTimeout: 15_000,
  contextOptions: { reducedMotion: process.env['E2E_REDUCED_MOTION'] ? 'reduce' : 'no-preference' },
});

test('E2E-08 withdrawal screens: last-day declare and confirm voids once with full refund credit, late declaration is expired, staff close, role guard', async ({ page, request }) => {
  test.setTimeout(900_000);
  const underwriter = await signIn(request, 'underwriter');
  const billing = await signIn(request, 'billing');
  await importProducts(request);

  const termState = async (policy: Issued): Promise<string> => (await call(request, underwriter, 'GET', `/api/pol/v1/terms/${policy.termId}`)).body['term']['state'] as string;
  const creditNotes = async (policy: Issued): Promise<Json[]> => {
    const list = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policy.policyId}`);
    expect(list.status, list.text).toBe(200);
    return (list.body['items'] as Json[]).map((i) => i['invoice'] as Json).filter((i) => i['kind'] === 'CREDIT_NOTE');
  };

  // ---- Setup (API): two paid policies concluded on the same day; the clock moves to the last day of the window.
  const main = await issuePolicy(request, 'e2e08ui-main', { pay: true, plate: 'IKX-0811' });
  const late = await issuePolicy(request, 'e2e08ui-late', { pay: true, plate: 'IKX-0812' });
  const lastDay = addDays(concludedDate(main), WINDOW_DAYS);
  await advanceToAthens(request, lastDay, '23:58:30');

  const fillDeclaration = async (policy: Issued, tag: string): Promise<void> => {
    await page.getByRole('textbox', { name: /^Ονοματεπώνυμο/ }).fill('Μαρία Παπαδοπούλου');
    await page.getByRole('textbox', { name: /^Αναγνωριστικό ασφαλιστηρίου/ }).fill(policy.policyId);
    await page.getByRole('textbox', { name: /^E-mail/ }).fill(`${tag}.withdrawal@example.org`);
    await page.getByRole('textbox', { name: /^Λόγος/ }).fill('Άλλαξα γνώμη');
    await page.getByRole('button', { name: 'Υποβολή δήλωσης' }).click();
  };

  // ---- 1. The form validates before anything is sent.
  await signInAs(page, UNDERWRITER);
  await go(page, '/channels/withdrawal');
  await expect(page.getByRole('heading', { level: 1, name: 'Υπαναχώρηση από σύμβαση από απόσταση' })).toBeVisible();
  await page.getByRole('button', { name: 'Υποβολή δήλωσης' }).click();
  await expect(page.getByText('Το πεδίο είναι υποχρεωτικό.')).toBeVisible();
  await expect(page.getByText('Εισαγάγετε έγκυρο αναγνωριστικό ασφαλιστηρίου.')).toBeVisible();
  await expect(page.getByText('Εισαγάγετε έγκυρη διεύθυνση e-mail.')).toBeVisible();
  expect(await termState(main), 'nothing was sent').not.toBe('VOIDED');

  // ---- 2. Last day: declare, see the receipt with the token, confirm.
  await fillDeclaration(main, 'main');
  await expect(page.getByRole('heading', { name: 'Απόδειξη δήλωσης' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: /^Κωδικός επιβεβαίωσης/ }), 'dev builds return the token').not.toHaveValue('');
  await page.getByRole('button', { name: 'Επιβεβαίωση υπαναχώρησης' }).click();
  const banner = page.getByText(/^Το αίτημα \S+ επιβεβαιώθηκε/);
  await expect(banner).toBeVisible();
  await expect(page.getByText('Η υπαναχώρηση επιβεβαιώθηκε', { exact: true }).first()).toBeVisible();
  const number = /^Το αίτημα (\S+) επιβεβαιώθηκε/.exec((await banner.textContent()) ?? '')![1]!;

  // POL voided the term once, on the last day; BIL credited the whole amount paid including IPT (deduction 0.00).
  await eventually('the term to be voided', async () => ((await termState(main)) === 'VOIDED' ? true : undefined));
  const row = await readWithdrawal(request, underwriter, number);
  expect(athensDate(new Date(row['confirmedAt'])), 'confirmed on the last day').toBe(lastDay);
  const credit = await eventually('the credit note of the withdrawal', async () => (await creditNotes(main))[0]);
  expect(await creditNotes(main), 'one credit note').toHaveLength(1);
  expect(cents(credit['total']), 'refund = total paid, premium plus IPT').toBe(main.total);

  // ---- 3. Staff: find the request, open it, close it as processed.
  await go(page, '/channels/withdrawals');
  await expect(page.getByRole('heading', { level: 1, name: 'Αιτήματα υπαναχώρησης' })).toBeVisible();
  await page.getByRole('textbox', { name: /^Αριθμός αιτήματος/ }).fill(number);
  await page.getByRole('button', { name: 'Αναζήτηση' }).click();
  const list = page.getByRole('grid', { name: 'Αιτήματα' });
  const listed = list.getByRole('row').filter({ hasText: number });
  await expect(listed).toBeVisible();
  await expect(listed).toContainText('Στην επεξεργασία');
  await listed.dblclick();
  await expect(page).toHaveURL(new RegExp(`/channels/withdrawals/${row['withdrawalRequestId']}$`));
  await expect(page.getByRole('heading', { level: 1, name: `Αίτημα ${number}` })).toBeVisible();
  await expect(page.getByText('Στην επεξεργασία', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Ολοκληρώθηκε η επιστροφή' }).click();
  await expect(page.getByText('Ολοκληρώθηκε', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ολοκληρώθηκε η επιστροφή' }), 'a closed request has no outcome bar').toHaveCount(0);
  expect((await readWithdrawal(request, underwriter, number))['status']).toBe('PROCESSED');

  // ---- 4. After midnight the window is over: the declaration is recorded and shown as expired, nothing is voided.
  await advanceToAthens(request, addDays(lastDay, 1), '00:00:05');
  await go(page, '/channels/withdrawal');
  await fillDeclaration(late, 'late');
  await expect(page.getByText('Η προθεσμία υπαναχώρησης έχει λήξει')).toBeVisible();
  const expired = page.getByText(/^Το αίτημα \S+ καταγράφηκε αλλά δεν μπορεί να γίνει δεκτό/);
  await expect(expired).toBeVisible();
  const lateNumber = /^Το αίτημα (\S+) καταγράφηκε/.exec((await expired.textContent()) ?? '')![1]!;
  expect(await termState(late), 'a late declaration voids nothing').not.toBe('VOIDED');
  const lateRow = await readWithdrawal(request, underwriter, lateNumber);
  expect(lateRow['status']).toBe('DECLINED');
  expect(lateRow['declineCode']).toBe('CHN-ERR-RIGHT-EXPIRED');
  expect(await creditNotes(late), 'no credit for a declined declaration').toHaveLength(0);

  // Staff see it as expired, with the code and no outcome bar.
  await go(page, `/channels/withdrawals/${lateRow['withdrawalRequestId']}`);
  await expect(page.getByRole('heading', { level: 1, name: `Αίτημα ${lateNumber}` })).toBeVisible();
  await expect(page.getByText('Εκπρόθεσμη', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('CHN-ERR-RIGHT-EXPIRED')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ολοκληρώθηκε η επιστροφή' })).toHaveCount(0);

  // ---- 5. Role guard: a claims handler has no access to the staff screens.
  await signInAs(page, CLAIMS);
  await go(page, '/channels/withdrawals');
  await expect(page.getByText('Δεν έχετε δικαίωμα')).toBeVisible();
});
