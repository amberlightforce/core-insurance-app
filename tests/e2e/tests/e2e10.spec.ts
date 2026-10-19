import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { call, signIn, type Json } from './support/api.js';

// E2E-10 AI kill switch journey (SLICE-PLAN-6): pilot AI features are refused until CMP approves and an admin enables them; the five
// module endpoints then answer with suggestions; a DAT bias breach and a CMP suspension each switch off exactly one feature; the global
// kill switch refuses all five within 60 s while a pending CHN ticket stays decidable by a human. The run script then re-runs the
// E2E-01 and E2E-02a happy paths (this stack's kill switch stays active) to prove that the manual path is unchanged with AI off.
// There is no real AI provider (D-SL6-09): suggestions come from the deterministic fake provider.

const FEATURES = { fnol: 'AI-CLM-01', pty: 'AI-PTY-01', bil: 'AI-BIL-01', uw: 'AI-UW-01', chn: 'AI-CHN-02' } as const;
type Key = keyof typeof FEATURES;
const KEYS = Object.keys(FEATURES) as Key[];
const GATE_DEADLINE_MS = 60_000;

const ENDPOINTS: Record<Key, { path: string; body: unknown }> = {
  fnol: { path: '/api/clm/v1/fnol/assist', body: { text: 'Rear-end collision on 2026-10-03, plate ΥΝΖ 4821, no injuries.' } },
  pty: {
    path: '/api/pty/v1/parties/explain-duplicate',
    body: {
      a: { name: 'Maria Papadopoulou', identifier: '123456789', birthDate: '1980-05-17' },
      b: { name: 'maria papadopoulou', identifier: '123456789', birthDate: '1980-05-17' },
    },
  },
  bil: {
    path: '/api/bil/v1/suspense/suggest-match',
    body: {
      payment: { amount: 120.5, reference: 'INV-0042', payerName: 'Maria' },
      candidates: [{ invoiceNo: 'INV-0041', openAmount: 99 }, { invoiceNo: 'INV-0042', openAmount: 120.5 }],
    },
  },
  uw: { path: '/api/uw/v1/referrals/summarize', body: { referralId: 'R-1', reasons: ['Sum insured above authority'], sumInsured: 750000, product: 'PROPERTY' } },
  chn: { path: '/api/chn/v1/agent/mcp', body: { jsonrpc: '2.0', id: 1, method: 'tools/list' } },
};

test('E2E-10 AI kill switch: approval gate, suggestions, bias breach, register suspension, global kill switch, human path', async ({ request }) => {
  test.setTimeout(600_000);
  const admin = await signIn(request, 'admin');
  const dpo = await signIn(request, 'dpo');

  const invoke = (key: Key) => call(request, admin, 'POST', ENDPOINTS[key].path, ENDPOINTS[key].body);
  const expectRefused = async (key: Key): Promise<void> => {
    const r = await invoke(key);
    expect(r.status, `${key}: ${r.text}`).toBe(503);
    expect(r.text).toContain('PLT-ERR-AI-DISABLED');
  };
  /** Polls one endpoint until it answers with the wanted status (gate cache at most 5 s; the event path must finish inside 60 s). */
  const waitFor = async (key: Key, status: number): Promise<void> => {
    const deadline = Date.now() + GATE_DEADLINE_MS;
    for (;;) {
      const r = await invoke(key);
      if (r.status === status) return;
      if (Date.now() > deadline) throw new Error(`${key} did not reach ${status} within ${GATE_DEADLINE_MS / 1000}s (last ${r.status}: ${r.text})`);
      await new Promise((done) => setTimeout(done, 1000));
    }
  };
  const resolveFeature = async (feature: string): Promise<Json> => {
    const r = await call(request, admin, 'POST', '/api/plt/v1/ai-toggle/resolve', { featureId: feature });
    expect(r.status, r.text).toBe(200);
    return r.body;
  };
  const registerStatus = async (feature: string): Promise<string> => {
    const r = await call(request, dpo, 'GET', `/api/cmp/v1/ai-systems/${feature}`);
    expect(r.status, r.text).toBe(200);
    return String(r.body['status']).toUpperCase();
  };

  // ---- 1. Off by default: all five endpoints refuse with the neutral 503.
  for (const key of KEYS) await expectRefused(key);

  // ---- 2. Unapproved features cannot be enabled (the register is the gate), then the DPO approves them.
  await test.step('enabling an unapproved feature is refused', async () => {
    const r = await call(request, admin, 'POST', '/api/plt/v1/ai-toggle/set', { featureId: FEATURES.fnol, enabled: true });
    expect(r.status, r.text).toBeGreaterThanOrEqual(400);
    expect((await resolveFeature(FEATURES.fnol))['allowed']).toBe(false);
  });
  await test.step('CMP approves the pilot features and an admin enables them', async () => {
    const approved = await call(request, dpo, 'POST', '/api/cmp/v1/dev/ai-systems/approve-all');
    expect(approved.status, approved.text).toBe(200);
    for (const feature of Object.values(FEATURES)) expect(await registerStatus(feature)).toBe('APPROVED');
    for (const feature of Object.values(FEATURES)) {
      const set = await call(request, admin, 'POST', '/api/plt/v1/ai-toggle/set', { featureId: feature, enabled: true });
      expect(set.status, set.text).toBe(200);
    }
    for (const key of KEYS) await waitFor(key, 200);
  });

  // ---- 3. The five endpoints answer with suggestions.
  await test.step('each module endpoint returns a suggestion', async () => {
    const fnol = (await invoke('fnol')).body;
    expect(fnol['suggestion']['cause']).toBe('COLLISION');
    expect(fnol['suggestion']['lossDate']).toBe('2026-10-03');
    expect(fnol['interactionId']).toBeTruthy();
    const pty = (await invoke('pty')).body;
    expect(pty['suggestion']['likelihood']).toBe('LIKELY_DUPLICATE');
    expect(pty['interactionId']).toBeTruthy();
    const bil = (await invoke('bil')).body;
    expect(bil['suggestion']['matches'][0]['invoiceNo']).toBe('INV-0042');
    const uw = (await invoke('uw')).body;
    expect(String(uw['suggestion']['summary'])).toContain('Sum insured above authority');
    const tools = (await invoke('chn')).body;
    expect((tools['result']['tools'] as unknown[]).length).toBeGreaterThan(0);
  });

  // ---- 4. A state-changing agent tool raises a PENDING ticket that only a human can decide.
  const ticket = await call(request, admin, 'POST', '/api/chn/v1/agent/mcp', {
    jsonrpc: '2.0', id: 7, method: 'tools/call', params: { name: 'bind_policy', arguments: { policyId: randomUUID() } },
  });
  expect(ticket.status, ticket.text).toBe(200);
  const ticketId = ticket.body['result']['ticket']['ticketId'] as string;
  expect(ticket.body['result']['ticket']['status']).toBe('PENDING');

  // ---- 5. DAT bias breach (ratio 0.74) switches off AI-CLM-01 only.
  await test.step('a bias breach disables one feature', async () => {
    const model = await call(request, admin, 'POST', '/api/dat/v1/models/register', { featureKey: FEATURES.fnol, version: '1.0' });
    expect(model.status, model.text).toBe(200);
    const inside = await call(request, admin, 'POST', '/api/dat/v1/bias-tests/run', { feature: FEATURES.fnol, metric: 'DISPARATE_IMPACT', ratio: '0.90', sampleSize: 500 });
    expect(inside.status, inside.text).toBe(200);
    expect(inside.body['breached']).toBe(false);
    expect((await invoke('fnol')).status, 'inside the band nothing changes').toBe(200);
    const bad = await call(request, admin, 'POST', '/api/dat/v1/bias-tests/run', { feature: FEATURES.fnol, metric: 'DISPARATE_IMPACT', ratio: '0.74', sampleSize: 500 });
    expect(bad.status, bad.text).toBe(200);
    expect(bad.body['breached']).toBe(true);
    expect(bad.body['modelStatus']).toBe('SUSPENDED');
    await waitFor('fnol', 503);
    expect((await resolveFeature(FEATURES.fnol))['enabled']).toBe(false);
    for (const key of ['pty', 'bil', 'uw', 'chn'] as Key[]) expect((await invoke(key)).status, `${key} is untouched`).toBe(200);
  });

  // ---- 6. CMP suspends AI-PTY-01: AiSystemStatusChanged is consumed by PLT and that feature goes off, the others stay on.
  await test.step('a register suspension disables one feature', async () => {
    const suspended = await call(request, dpo, 'POST', '/api/cmp/v1/ai-systems/suspend', { featureId: FEATURES.pty, reason: 'E2E-10 register suspension' });
    expect(suspended.status, suspended.text).toBe(200);
    expect(await registerStatus(FEATURES.pty)).toBe('SUSPENDED');
    await waitFor('pty', 503);
    expect((await resolveFeature(FEATURES.pty))['enabled']).toBe(false);
    for (const key of ['bil', 'uw', 'chn'] as Key[]) expect((await invoke(key)).status, `${key} is untouched`).toBe(200);
  });

  // ---- 7. Global kill switch: every endpoint refuses within 60 s.
  await test.step('the global kill switch refuses all five endpoints', async () => {
    const kill = await call(request, admin, 'POST', '/api/plt/v1/ai/kill-switch', { action: 'Activate', scope: 'GLOBAL', reason: 'E2E-10 kill switch' });
    expect(kill.status, kill.text).toBe(200);
    for (const key of KEYS) await waitFor(key, 503);
    for (const key of KEYS) await expectRefused(key);
    expect((await resolveFeature(FEATURES.bil))['killSwitchActive']).toBe(true);
  });

  // ---- 8. The manual path is unchanged: a human decides the pending ticket while AI is off.
  await test.step('a human approves the pending ticket with AI off', async () => {
    const got = await call(request, admin, 'GET', `/api/chn/v1/tickets/${ticketId}`);
    expect(got.status, got.text).toBe(200);
    expect(got.body['status']).toBe('PENDING');
    const approved = await call(request, admin, 'POST', '/api/chn/v1/tickets/approve', { ticketId });
    expect(approved.status, approved.text).toBe(200);
    expect(approved.body['status']).toBe('APPROVED');
    const stale = await call(request, admin, 'POST', '/api/chn/v1/tickets/reject', { ticketId });
    expect(stale.status, 'a decided ticket cannot be reversed').toBe(409);
  });
  // E2E-01 and E2E-02a are re-run by tests/e2e/run-e2e10.sh against this stack with the kill switch still active.
});
