import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test, type Page } from '@playwright/test';

// E2E-01 happy path through the staff screens in a real browser (Greek UI, role-based locators): dev sign-in, party,
// party search, quote wizard (premium, IPT lines, banners, UW outcome), bind, policy view, invoice, exact payment ->
// PAID, journals balanced. Runs against the stack of tests/e2e/run-e2e01.sh (fresh database, so this is the first
// policy, invoice and party) or any stack with the dev sign-in. Amounts are illustrative test data (D-SLC-04).

const SEED = process.env['E2E_PRODUCT_SEED'] ?? resolve(import.meta.dirname, '../../../src/CoreIns.Modules.Product/Seed/motor-gr.product.json');

// The product is data (D-USR-02): import and lock it as admin (idempotent), as the API-level spec does.
// On a fresh stack this is the API process's first command: its cold start (JIT, EF models, first PFC compile) took 2-7 s
// on an idle machine before and after SL2 alike, and more on a busy Docker host, so it gets its own timeout instead of
// the 15 s actionTimeout meant for UI actions below (which the request fixture inherits).
test.beforeAll(async ({ request }) => {
  test.setTimeout(120_000);
  const signIn = await request.post('/api/plt/v1/dev/sign-in', { data: { userId: 'admin' } });
  const token = ((await signIn.json()) as { accessToken: string }).accessToken;
  const imported = await request.post('/api/pfc/v1/product-versions/import', {
    headers: { authorization: `Bearer ${token}`, 'idempotency-key': crypto.randomUUID() },
    data: { definition: JSON.parse(readFileSync(SEED, 'utf8')), lock: true },
    timeout: 90_000,
  });
  expect(imported.status(), await imported.text()).toBeLessThan(300);
});

/**
 * The page must stay responsive: a render loop keeps the renderer's main thread busy and evaluate() never answers.
 * Called after every navigation and after the screens that once froze (customer search, invoice).
 */
async function alive(page: Page): Promise<void> {
  const answer = await Promise.race([
    page.evaluate(() => 'alive'),
    new Promise<string>((done) => setTimeout(() => done('frozen'), 5000)),
  ]);
  expect(answer, 'the page answers within 5 s (no render loop)').toBe('alive');
}

/** page.goto followed by the responsiveness check. */
async function go(page: Page, url: string): Promise<void> {
  await page.goto(url);
  await alive(page);
}

/** Dev sign-in page: pick the user with the mouse (not the keyboard), sign in. */
async function signInAs(page: Page, name: string): Promise<void> {
  await go(page, '/dev/sign-in');
  const signOut = page.getByRole('button', { name: 'Αποσύνδεση' });
  const picker = page.getByRole('button', { name: /Χρήστης/ });
  await expect(signOut.or(picker)).toBeVisible();
  if (await signOut.isVisible()) await signOut.click();
  await picker.click();
  await page.getByRole('option', { name, exact: true }).click(); // the mouse must select the option (rough edge b)
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Συνδεθήκατε ως' })).toBeVisible();
}

test.use({
  actionTimeout: 15_000,
  contextOptions: { reducedMotion: process.env['E2E_REDUCED_MOTION'] ? 'reduce' : 'no-preference' },
});

test('E2E-01 through the staff screens', async ({ page }) => {
  test.setTimeout(300_000);

  // ---- Dev sign-in as the underwriter; the mouse click on the option must select it.
  await signInAs(page, 'Dev Underwriter (synthetic)');

  // ---- Create the party.
  await go(page, '/parties/new');
  await page.getByRole('textbox', { name: /^Όνομα υποχρεωτικό/ }).fill('Ελένη');
  await page.getByRole('textbox', { name: /^Επώνυμο/ }).fill('Ιωάννου');
  const birth = page.getByRole('group', { name: /Ημερομηνία γέννησης/ });
  await birth.getByRole('spinbutton', { name: /ημέρα/ }).fill(String(1 + Math.floor(Math.random() * 28)).padStart(2, '0'));
  await birth.getByRole('spinbutton', { name: /μήνας/ }).fill('03');
  await birth.getByRole('spinbutton', { name: /έτος/ }).fill(String(1950 + Math.floor(Math.random() * 40)));
  await page.getByRole('textbox', { name: /^Οδός/ }).fill('Σταδίου');
  await page.getByRole('textbox', { name: /^Αριθμός$/ }).fill('10');
  await page.getByRole('textbox', { name: /^Ταχυδρομικός κώδικας/ }).fill('10564');
  await page.getByRole('textbox', { name: /^Πόλη ή περιοχή/ }).fill('Αθήνα');
  await page.getByRole('textbox', { name: 'Email' }).fill('eleni.ioannou@example.org');
  await page.getByRole('button', { name: 'Δημιουργία πελάτη' }).click();
  // A stack that already holds look-alike parties first shows the duplicate suggestions; open the new party from there.
  const openNew = page.getByRole('button', { name: 'Άνοιγμα νέου πελάτη' });
  await expect(openNew.or(page.getByRole('heading', { level: 1, name: /Ιωάννου/ }))).toBeVisible();
  if (await openNew.isVisible()) await openNew.click();
  await expect(page).toHaveURL(/\/parties\/[0-9a-f-]{36}$/);
  const partyId = page.url().split('/').pop() as string;
  await expect(page.getByRole('heading', { level: 1, name: /Ιωάννου/ })).toBeVisible();

  // ---- The customer search must answer, not freeze the tab: results within 5 s.
  await go(page, '/parties');
  await page.getByRole('searchbox').fill('Ιωάννου');
  await page.keyboard.press('Enter');
  await alive(page);
  await expect(page.getByRole('grid', { name: /Αποτελέσματα/ }).getByText('Ελένη Ιωάννου').first()).toBeVisible({ timeout: 5000 });
  // The page is still alive after the results arrive.
  await page.getByRole('searchbox').fill('Ελένη');
  await expect(page.getByRole('searchbox')).toHaveValue('Ελένη');

  // ---- Quote wizard for that party.
  await go(page, `/policies/quotes/new?partyId=${partyId}`);
  const next = page.getByRole('button', { name: /Επόμενο/ });
  await expect(page.getByText('MOTOR-GR')).toBeVisible();
  await expect(next).not.toHaveAttribute('aria-disabled', 'true');
  await next.click();

  await expect(page.getByRole('heading', { level: 2, name: 'Όχημα' })).toBeVisible();
  await page.getByRole('textbox', { name: /Αριθμός κυκλοφορίας/ }).fill('IKX1234');
  await page.getByRole('textbox', { name: /Μάρκα/ }).fill('Toyota');
  await page.getByRole('textbox', { name: /Μοντέλο/ }).fill('Yaris');
  await page.getByRole('textbox', { name: /Έτος πρώτης κυκλοφορίας/ }).fill('2021');
  await page.getByRole('textbox', { name: /Κυβισμός/ }).fill('1400');
  await page.getByRole('textbox', { name: /Ταχυδρομικός κώδικας στάθμευσης/ }).fill('10564');
  await page.getByRole('textbox', { name: /Αξία οχήματος/ }).fill('15000');
  await next.click();

  await expect(page.getByRole('heading', { level: 2, name: 'Οδηγός' })).toBeVisible();
  await page.getByRole('textbox', { name: /Έτος απόκτησης διπλώματος/ }).fill('2010');
  await next.click();

  await expect(page.getByRole('heading', { level: 2, name: 'Καλύψεις' })).toBeVisible();
  await page.getByRole('button', { name: /Όριο σωματικής βλάβης ανά παθόντα/ }).click();
  await page.getByRole('option', { name: '1.300.000 €' }).click();
  await page.getByText('Συμπερίληψη: Ίδιες Ζημιές Οχήματος').click();
  await expect(page.getByRole('checkbox', { name: 'Συμπερίληψη: Ίδιες Ζημιές Οχήματος' })).toBeChecked();
  await page.getByRole('textbox', { name: /Ασφαλισμένο κεφάλαιο/ }).fill('15000');
  await page.getByRole('button', { name: /Απαλλαγή/ }).click();
  await page.getByRole('option').first().click();
  await next.click();

  await expect(page.getByRole('heading', { level: 2, name: 'Ερωτήσεις κινδύνου' })).toBeVisible();
  await page.getByText('Ιδιωτική χρήση', { exact: true }).click();
  await expect(page.getByRole('radio', { name: 'Ιδιωτική χρήση', exact: true })).toBeChecked();
  await page.getByText('Όχι', { exact: true }).click();
  await next.click();

  await expect(page.getByRole('heading', { level: 2, name: 'Ασφάλιστρο' })).toBeVisible();
  await page.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }).click();
  // Banners: illustrative tariff and provisional tax; UW outcome: accept.
  await expect(page.getByText('Ενδεικτικό τιμολόγιο')).toBeVisible();
  await expect(page.getByText('Προσωρινοί φόροι και εισφορές')).toBeVisible();
  await expect(page.getByText('Αποδοχή').first()).toBeVisible();
  const amountOf = async (label: string): Promise<bigint> => {
    const text = (await page.getByRole('region', { name: 'Ανάλυση ασφαλίστρου ανά κάλυψη' }).locator('dt', { hasText: label }).locator('xpath=following-sibling::dd[1]').innerText()).replace(/\s+/g, ' ');
    const match = /^([\d.]+),(\d{2})\s?€$/.exec(text.trim());
    if (!match) throw new Error(`not a euro amount after "${label}": ${text}`);
    return BigInt(match[1]!.replaceAll('.', '') + match[2]!);
  };
  // The totals are consistent with each other: premium + taxes = total, and the IPT is 15 % of the premium (cents).
  const premium = await amountOf('Καθαρά ασφάλιστρα');
  const taxes = await amountOf('Φόροι και εισφορές');
  const total = await amountOf('Συνολικό ποσό');
  test.info().annotations.push({ type: 'quote-totals', description: `premium ${premium}c, taxes ${taxes}c, total ${total}c` });
  expect(premium > 0n).toBe(true);
  expect(premium + taxes).toBe(total);
  expect(taxes * 100n / premium).toBeGreaterThanOrEqual(14n); // 15 % of each cover's premium, rounded to the cent
  expect(taxes * 100n / premium).toBeLessThanOrEqual(15n);
  // The IPT lines (GR-IPT, one per cover) show as provisional.
  await expect(page.getByRole('grid', { name: /Χρεώσεις κάλυψης/ }).first()).toBeVisible();
  await expect(page.getByText('GR-IPT').first()).toBeVisible();
  await expect(page.getByText('Προσωρινό').first()).toBeVisible();

  // ---- Bind with ANNUAL and explicit confirmation.
  await next.click();
  await expect(page.getByRole('heading', { level: 2, name: 'Δέσμευση' })).toBeVisible();
  await expect(page.getByRole('radio', { name: /Ετήσιο \(ANNUAL\)/ })).toBeChecked();
  await page.getByRole('button', { name: 'Δέσμευση', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Επιβεβαίωση δέσμευσης' });
  await dialog.getByText('Επιβεβαιώνω τη δέσμευση της προσφοράς.').click();
  await dialog.getByRole('button', { name: 'Δέσμευση ασφαλιστηρίου' }).click();
  const bound = page.getByText(/Δεσμεύτηκε το ασφαλιστήριο POL\d+/);
  await expect(bound).toBeVisible();
  const policyNumber = /POL\d+/.exec(await bound.innerText())?.[0] as string;
  expect(policyNumber).toBeTruthy();

  // ---- Policy view: a not-yet-started policy opens as of the start of its term, with the covers and a note.
  await page.getByRole('button', { name: 'Άνοιγμα ασφαλιστηρίου' }).click();
  await expect(page.getByRole('heading', { level: 1, name: `Ασφαλιστήριο ${policyNumber}` })).toBeVisible();
  await expect(page).toHaveURL(new RegExp(`/policies/[0-9a-f-]{36}$`));
  await alive(page);
  await expect(page.getByText('Δεν έχει αρχίσει ακόμη')).toBeVisible();
  const covers = page.getByRole('region', { name: 'Καλύψεις' });
  await expect(covers.getByText('MTPL')).toBeVisible();
  await expect(page.getByRole('region', { name: 'Ασφαλιζόμενο όχημα' }).getByText(/ΙΚΧ-?1234/)).toBeVisible();
  const charges = page.getByRole('grid', { name: 'Γραμμές χρέωσης' });
  await expect(charges.getByText('GR-IPT').first()).toBeVisible();
  const policyUrl = page.url();

  // ---- The underwriter may read the invoice (D-SLC-20): the policy page lists it (the worker bills asynchronously).
  await expect
    .poll(
      async () => {
        await go(page, policyUrl);
        const invoice = page.getByRole('grid', { name: 'Τιμολόγια ασφαλιστηρίου' }).getByText(/^INV\d+/).first();
        return invoice
          .waitFor({ timeout: 4000 })
          .then(() => 1)
          .catch(() => 0);
      },
      { timeout: 90_000, intervals: [2000] },
    )
    .toBeGreaterThan(0);
  await expect(page.getByText('Φόρτωση…')).toHaveCount(0);

  // ---- Billing: the invoice, the stub fiscal MARK, the exact payment, PAID.
  await signInAs(page, 'Dev Billing Clerk (synthetic)');
  await go(page, policyUrl);
  await page.getByRole('grid', { name: 'Τιμολόγια ασφαλιστηρίου' }).getByText(/^INV\d+/).first().dblclick();
  await expect(page.getByRole('heading', { level: 1, name: /^Τιμολόγιο INV/ })).toBeVisible();
  await alive(page);
  await expect(page.getByText('GR-IPT').first()).toBeVisible();
  await expect(page.getByText('Προσωρινό').first()).toBeVisible();
  await expect(page.getByText(/STUB-\d+/).first()).toBeVisible({ timeout: 60_000 });
  await page.getByRole('textbox', { name: /Αναφορά τράπεζας/ }).fill('E2E-01-EXACT');
  await page.getByRole('button', { name: 'Καταχώριση πληρωμής' }).click();
  await expect(page.getByText(/Καταχωρίστηκε η απόδειξη RCP\d+/).first()).toBeVisible();
  await expect(page.getByText('Εξοφλήθηκε').first()).toBeVisible({ timeout: 60_000 });

  // ---- Finance: the policy's journals, every one balanced.
  await signInAs(page, 'Dev Finance Accountant (synthetic)');
  await go(page, `/finance/journals/policy/${policyNumber}`);
  await expect(page.getByRole('heading', { level: 1, name: new RegExp(policyNumber) })).toBeVisible();
  await expect(page.getByText('Ισοσκελισμένη').first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText('Μη ισοσκελισμένη')).toHaveCount(0);
  expect(await page.getByText('Ισοσκελισμένη').count()).toBeGreaterThanOrEqual(6);
});

// ---- Rough edges found by hand in a real Chrome, kept as regression checks.

async function createParty(request: import('@playwright/test').APIRequestContext, family: string): Promise<void> {
  const signIn = await request.post('/api/plt/v1/dev/sign-in', { data: { userId: 'underwriter' } });
  const token = ((await signIn.json()) as { accessToken: string }).accessToken;
  const created = await request.post('/api/pty/v1/parties', {
    headers: { authorization: `Bearer ${token}`, 'idempotency-key': crypto.randomUUID() },
    data: {
      partyType: 'PERSON',
      person: { givenNames: 'Σοφία', familyName: family, fatherName: 'Νικόλαος', birthDate: '1979-06-21' },
      identifiers: [],
      addresses: [{ types: ['LEGAL', 'MAILING'], country: 'GR', street: 'Πανεπιστημίου', number: '5', postcode: '10679', locality: 'Αθήνα' }],
      contactPoints: [{ type: 'EMAIL', value: 'sofia.test@example.org', primary: true }],
      reason: 'NEW_CUSTOMER',
    },
  });
  expect(created.status(), await created.text()).toBe(201);
}

test('the quote wizard chooses the policyholder with a single click', async ({ page, request }) => {
  const family = `Επιλογής${Date.now().toString().slice(-6)}`;
  await createParty(request, family);
  await signInAs(page, 'Dev Underwriter (synthetic)');
  await go(page, '/policies/quotes/new');
  await page.getByRole('searchbox').fill(family);
  await page.keyboard.press('Enter');
  await alive(page);
  const result = page.getByRole('grid', { name: /Αποτελέσματα/ }).getByText(new RegExp(family)).first();
  await expect(result).toBeVisible({ timeout: 5000 });
  await result.click(); // one click, not a double click
  await expect(page.getByRole('region', { name: 'Λήπτης ασφάλισης' }).getByText(new RegExp(family))).toBeVisible();
  await expect(page.getByRole('button', { name: /Επόμενο/ })).not.toHaveAttribute('aria-disabled', 'true');
});

test('the dev sign-in select takes a mouse click on an option, every time', async ({ page }) => {
  for (let attempt = 0; attempt < 6; attempt += 1) {
    await go(page, '/dev/sign-in');
    const signOut = page.getByRole('button', { name: 'Αποσύνδεση' });
    const picker = page.getByRole('button', { name: /Χρήστης/ });
    await expect(signOut.or(picker)).toBeVisible();
    if (await signOut.isVisible()) await signOut.click();
    const box = (await picker.boundingBox())!;
    await page.mouse.move(box.x + 20, box.y + 10);
    await page.mouse.down();
    await page.mouse.up();
    const option = page.getByRole('option').nth(attempt % 4);
    const target = (await option.boundingBox())!;
    await page.mouse.move(target.x + target.width / 2, target.y + target.height / 2, { steps: 4 });
    await page.waitForTimeout(40 * attempt);
    await page.mouse.down();
    await page.mouse.up();
    await expect(picker).not.toContainText('Επιλέξτε');
    await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Συνδεθήκατε ως' })).toBeVisible();
  }
});

test('a stale dev token sends the user back to the sign-in page with a message', async ({ page }) => {
  await page.addInitScript(() => {
    sessionStorage.setItem(
      'coreins.devSession',
      JSON.stringify({
        // A well-formed JWT whose signature no longer verifies, as after an api restart with a new signing key.
        accessToken: `${btoa('{"alg":"HS256","typ":"JWT"}')}.${btoa('{"sub":"underwriter","iss":"coreins-dev-local"}')}.c2lnbmF0dXJl`.replaceAll('=', ''),
        expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
        user: { id: 'underwriter', name: 'Underwriter', roles: ['Staff.Underwriter'] },
      }),
    );
  });
  await go(page, '/parties');
  await page.getByRole('searchbox').fill('Παπαδοπούλου');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/dev\/sign-in\?expired=1/);
  await expect(page.getByText('Η σύνδεση έληξε')).toBeVisible();
  await alive(page);
});

test('the favicon is public', async ({ request }) => {
  const response = await request.get('/favicon.ico', { maxRedirects: 0 });
  expect(response.status()).not.toBe(401);
  expect([200, 302]).toContain(response.status());
});
