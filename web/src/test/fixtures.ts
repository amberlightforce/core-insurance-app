import type {
  BillingAccountGetResponse,
  CatalogueGetResponse,
  InvoiceGetResponse,
  InvoiceListPage,
  JobBindResponse,
  JobQuoteResponse,
  JournalQueryPage,
  PartyView,
  PolicyGetResponse,
  QuestionSetGetResponse,
} from '../api/types';

/** Synthetic API payloads shaped like the contracts (and the local stack's real answers). */

export const partyId = '01a11800-5123-72da-8f3c-f5bda39e1cb0';
export const policyId = '0f0f0f0f-1111-4222-8333-444444444444';
export const jobId = 'aaaaaaaa-1111-4222-8333-555555555555';
export const accountId = 'bbbbbbbb-1111-4222-8333-666666666666';
export const invoiceId = 'cccccccc-1111-4222-8333-777777777777';
export const hash = '50020ebe2604343834cd2490641a2ed0952417ecb03c691b0b0488dc2a04c50c';

export function party(revealed = false): PartyView {
  return {
    partyId,
    partyNumber: 'P000000021',
    partyType: 'PERSON',
    status: 'PROSPECT',
    legalEntity: 'GR-TEST',
    jurisdiction: 'GR',
    preferredLanguage: 'el',
    recordVersion: 1,
    birthDate: revealed ? '1985-03-12' : null,
    p2Revealed: revealed,
    names: [
      { form: 'NATIVE', script: 'Grek', givenNames: 'Δοκιμή', familyName: 'Διεπαφής' },
      { form: 'LATIN_GENERATED', script: 'Latn', givenNames: 'Dokimi', familyName: 'Diepafis' },
    ],
    identifiers: [
      {
        identifierId: 'id-1',
        scheme: 'AFM',
        value: revealed ? '094014201' : '******201',
        masked: !revealed,
        verificationStatus: 'SELF_DECLARED',
        verificationSource: 'USER_ENTRY',
        validatorVersion: 'GR-ID/1',
        validFrom: '2026-10-07',
      },
    ],
    addresses: [
      {
        addressId: 'addr-1',
        types: ['LEGAL', 'MAILING'],
        primary: true,
        country: 'GR',
        native: { street: 'Λεωφ. Κηφισίας', number: '124', postcode: '11526', locality: 'Αθήνα' },
        latin: { street: 'Leof. Kifisias', number: '124', postcode: '11526', locality: 'Athina' },
        formattedLines: ['Λεωφ. Κηφισίας 124', '11526 Αθήνα'],
        formattedLinesLatin: ['Leof. Kifisias 124', '11526 Athina'],
        validationState: 'VALIDATED',
        validFrom: '2026-10-07',
      },
    ],
    contactPoints: [
      {
        contactPointId: 'cp-1',
        type: 'EMAIL',
        value: 'ui.test@example.org',
        purpose: 'PERSONAL',
        primary: true,
        verificationStatus: 'UNVERIFIED',
      },
    ],
    createdAt: '2026-10-07T20:13:56Z',
  };
}

export const searchItem = {
  partyId,
  partyNumber: 'P000000021',
  partyType: 'PERSON' as const,
  status: 'PROSPECT',
  displayName: 'Δοκιμή Διεπαφής',
  maskedIdentifier: { scheme: 'AFM', maskedValue: '******201' },
  primaryPostcode: '11526',
  primaryLocality: 'Αθήνα',
  matchQuality: 'EXACT' as const,
  similarity: '1.0000',
};

export const resolved = {
  version: '1.0',
  artefactHash: hash,
  resolutionManifest: { artefactHash: hash },
  resolutionHash: 'f8bd9179816de489cc94e0cc38523f978336bafc3c66081b4e3012864c415e48',
};

const text = (el: string, en: string) => ({ el, en });

export const catalogue: CatalogueGetResponse = {
  artefactHash: hash,
  product: 'MOTOR-GR',
  version: '1.0',
  coverages: [
    {
      code: 'MTPL',
      name: text('Αστική ευθύνη αυτοκινήτου', 'Motor third-party liability'),
      existence: 'REQUIRED',
      removal: 'BLOCK',
      coveredElement: 'vehicle',
      terms: [
        {
          code: 'BI_PER_PERSON',
          name: text('Σωματικές βλάβες ανά άτομο', 'Bodily injury per person'),
          kind: 'OPTION_LIST',
          valueType: 'MONEY',
          modelType: 'LIMIT',
          required: true,
          options: [
            { code: 'BI-1300K', value: '1300000', name: text('1.300.000 €', '€1,300,000') },
            { code: 'BI-2000K', value: '2000000', name: text('2.000.000 €', '€2,000,000') },
          ],
        },
      ],
    },
    {
      code: 'OWN-DAMAGE',
      name: text('Ίδιες ζημιές', 'Own damage'),
      existence: 'ELECTABLE',
      removal: 'ALLOWED',
      coveredElement: 'vehicle',
      terms: [
        {
          code: 'SUM_INSURED',
          name: text('Ασφαλιζόμενο ποσό', 'Sum insured'),
          kind: 'DIRECT',
          valueType: 'MONEY',
          modelType: 'SUM_INSURED',
          required: true,
        },
        {
          code: 'DEDUCTIBLE',
          name: text('Απαλλαγή', 'Deductible'),
          kind: 'OPTION_LIST',
          valueType: 'MONEY',
          modelType: 'DEDUCTIBLE',
          required: true,
          options: [
            { code: 'DED-0', value: '0', name: text('Χωρίς απαλλαγή', 'No deductible') },
            { code: 'DED-150', value: '150', name: text('150 €', '€150') },
          ],
        },
      ],
    },
  ],
  elements: [],
  finals: [],
} as unknown as CatalogueGetResponse;

export const questionSet: QuestionSetGetResponse = {
  artefactHash: hash,
  questionSet: {
    code: 'MOTOR-RISK',
    type: 'UNDERWRITING',
    name: text('Ερωτήσεις κινδύνου αυτοκινήτου', 'Motor risk questions'),
    questions: [
      {
        code: 'Q-USAGE',
        text: text('Πώς χρησιμοποιείται το όχημα;', 'How is the vehicle used?'),
        answerType: 'CHOICE',
        required: true,
        customerVisible: true,
        answers: [
          { code: 'PRIVATE', label: text('Ιδιωτική χρήση', 'Private use'), outcome: 'NONE' },
          {
            code: 'BUSINESS',
            label: text('Επαγγελματική χρήση', 'Business use'),
            outcome: 'REFERRAL',
          },
        ],
        mapsToField: 'vehicle.usage',
        illustrative: true,
        displayOrder: 1,
      },
      {
        code: 'Q-HIRE-REWARD',
        text: text('Χρησιμοποιείται για μίσθωση;', 'Is it used for hire or reward?'),
        answerType: 'CHOICE',
        required: true,
        customerVisible: true,
        answers: [
          { code: 'NO', label: text('Όχι', 'No'), outcome: 'NONE' },
          {
            code: 'YES',
            label: text('Ναι', 'Yes'),
            outcome: 'KNOCK_OUT',
            reasonKey: 'pfc.question.hire.out_of_scope',
          },
        ],
        illustrative: true,
        displayOrder: 3,
      },
      {
        code: 'Q-CLAIMS-5Y',
        text: text('Πόσες ζημιές είχατε;', 'How many claims?'),
        answerType: 'INTEGER',
        required: false,
        customerVisible: true,
        mapsToField: 'driver.claimsLast5Years',
        illustrative: true,
        displayOrder: 4,
      },
    ],
  },
} as unknown as QuestionSetGetResponse;

const eur = (amount: string) => ({ amount, currency: 'EUR' });

export function quote(overrides: Partial<JobQuoteResponse> = {}): JobQuoteResponse {
  return {
    jobId,
    quoteId: 'dddddddd-1111-4222-8333-888888888888',
    versionNo: 1,
    state: 'QUOTED',
    decision: 'ACCEPT',
    referred: false,
    bindable: true,
    premium: eur('432.35'),
    taxes: eur('46.85'),
    total: eur('479.20'),
    charges: [
      {
        elementLocator: 'v1',
        coverageCode: 'MTPL',
        chargeType: 'PREM-MTPL',
        chargeCategory: 'PREMIUM',
        annualRate: '312.3456',
        amount: eur('312.35'),
      },
      {
        elementLocator: 'v1',
        coverageCode: 'OWN-DAMAGE',
        chargeType: 'PREM-OD',
        chargeCategory: 'PREMIUM',
        annualRate: '120.005',
        amount: eur('120.00'),
      },
      {
        elementLocator: 'v1',
        coverageCode: 'MTPL',
        chargeType: 'GR-IPT',
        chargeCategory: 'TAX',
        annualRate: '0.15',
        amount: eur('46.85'),
        legalStatus: 'Unverified',
        provisional: true,
      },
    ],
    worksheetId: 'a'.repeat(64),
    issues: [],
    validUntil: '2026-11-06T10:00:00Z',
    ...overrides,
  };
}

export function bound(overrides: Partial<JobBindResponse> = {}): JobBindResponse {
  return {
    jobId,
    state: 'BOUND',
    policyId,
    policyNumber: 'POL000000007',
    termState: 'SCHEDULED',
    gateResults: [
      { gate: 'EFFECTIVE_DATE', passed: true, severity: 'BLOCK' },
      { gate: 'UW_ISSUES', passed: true, severity: 'BLOCK' },
    ],
    ...overrides,
  };
}

export function policy(
  status: 'SCHEDULED' | 'IN_FORCE' | 'EXPIRED' = 'IN_FORCE',
): PolicyGetResponse {
  return {
    policy: {
      policyId,
      policyNumber: 'POL000000007',
      productCode: 'MOTOR-GR',
      policyholderPartyId: partyId,
      legalEntity: 'GR-TEST',
      jurisdiction: 'GR',
      status,
      recordedAt: '2026-10-07T20:20:00Z',
    },
    term: {
      termId: 'term-1',
      termNumber: 1,
      period: { from: '2026-10-08T00:00:00+03:00', to: '2027-10-08T00:00:00+03:00' },
      state: status,
      productVersion: '1.0',
      artefactHash: hash,
      resolutionHash: hash,
      configurationHash: hash,
      currency: 'EUR',
      paymentPlanRef: 'ANNUAL',
      writtenDate: '2026-10-07',
      recordedAt: '2026-10-07T20:20:00Z',
    },
    riskTree: {
      vehicles: [
        {
          locator: 'v1',
          plate: 'ΙΚΧ-1234',
          make: 'Toyota',
          model: 'Yaris',
          firstRegistrationYear: 2021,
        },
      ],
      drivers: [{ locator: 'd1', partyId, driverType: 'MAIN' }],
      coverages: [
        { coverageCode: 'MTPL', selected: true },
        { coverageCode: 'OWN-DAMAGE', selected: true },
        { coverageCode: 'WINDSCREEN', selected: false },
      ],
      questionSets: [],
    },
    transactions: [
      {
        transactionId: 'tx-1',
        kind: 'NEW_BUSINESS',
        sequence: 1,
        effectiveAt: '2026-10-08T00:00:00+03:00',
        recordedAt: '2026-10-07T20:20:00Z',
        premium: eur('432.35'),
        taxes: eur('46.85'),
        total: eur('479.20'),
      },
    ],
    charges: quote().charges.map((c) => ({
      ...c,
      transactionId: 'tx-1',
      chargeId: `${c.chargeType}-id`,
    })),
  } as unknown as PolicyGetResponse;
}

export const invoiceList: InvoiceListPage = {
  items: [
    {
      invoice: invoice().invoice,
      fiscalStatus: invoice().fiscalStatus,
    },
  ],
  nextCursor: null,
  limit: 50,
};

export function invoice(state: 'BILLED' | 'PAID' = 'BILLED'): InvoiceGetResponse {
  const open = state === 'PAID' ? '0.00' : '479.20';
  return {
    invoice: {
      invoiceId,
      invoiceNumber: 'INV000000003',
      kind: 'INVOICE',
      state,
      billingAccountId: accountId,
      policyId,
      policyTermId: 'term-1',
      transactionId: 'tx-1',
      issueDate: '2026-10-07',
      dueDate: '2026-10-21',
      method: 'BANK_TRANSFER',
      total: eur('479.20'),
      paid: eur(state === 'PAID' ? '479.20' : '0.00'),
      open: eur(open),
      totalsByCategory: [
        { category: 'PREMIUM', amount: eur('432.35') },
        { category: 'TAX', amount: eur('46.85') },
      ],
    },
    invoiceItems: [
      {
        invoiceItemId: 'item-1',
        chargeId: 'ch-1',
        transactionId: 'tx-1',
        elementLocator: 'v1',
        coverageCode: 'MTPL',
        chargeType: 'PREM-MTPL',
        chargeCategory: 'PREMIUM',
        validPeriod: { from: '2026-10-08', to: '2027-10-08' },
        amount: eur('312.35'),
        open: eur(state === 'PAID' ? '0.00' : '312.35'),
        state: state === 'PAID' ? 'SETTLED' : 'BILLED',
      },
    ],
    allocations: [],
    fiscalStatus: {
      status: 'REGISTERED',
      series: 'STUB-A',
      number: '000042',
      mark: '400000000042',
      uid: 'STUBUID1',
      documentType: '1.1',
    },
    deliveryStatus: 'NOT_REQUESTED',
  } as unknown as InvoiceGetResponse;
}

export function account(): BillingAccountGetResponse {
  return {
    account: {
      billingAccountId: accountId,
      accountNumber: 'BA000000002',
      payerPartyId: partyId,
      currency: 'EUR',
      status: 'ACTIVE',
      createdAt: '2026-10-07T20:20:00Z',
      terms: [
        {
          policyTermId: 'term-1',
          policyId,
          policyNumber: 'POL000000007',
          termNumber: 1,
          productCode: 'MOTOR-GR',
          planCode: 'ANNUAL',
          billMode: 'DIRECT_BILL',
          termPeriod: { from: '2026-10-08', to: '2027-10-08' },
        },
      ],
    },
    balancesByState: {
      writtenUnbilled: eur('0.00'),
      billed: eur('479.20'),
      overdue: eur('0.00'),
      collected: eur('0.00'),
      unapplied: eur('0.00'),
      credit: eur('0.00'),
    },
  } as unknown as BillingAccountGetResponse;
}

export function journals(unbalanced = false): JournalQueryPage {
  const line = (lineNo: number, accountCode: string, side: 'DEBIT' | 'CREDIT', amount: string) => ({
    lineNo,
    account: accountCode,
    accountName: { el: `Λογαριασμός ${accountCode}`, en: `Account ${accountCode}` },
    side,
    amount: eur(amount),
    functionalAmount: eur(amount),
    ruleCode: 'R-WRITTEN',
    accountOrigin: accountCode === '2300' ? 'TECHNICAL_PLACEHOLDER' : 'PRD09_ILLUSTRATIVE',
    dimensions: {},
  });
  return {
    items: [
      {
        journal: {
          journalId: 'j-1',
          journalNumber: 'J000000001',
          legalEntity: 'GR-TEST',
          jurisdiction: 'GR',
          book: 'GL',
          accountingDate: '2026-10-07',
          businessDate: '2026-10-07',
          period: '2026-10',
          sourceType: 'EVENT',
          sourceModule: 'BIL',
          sourceEventType: 'BillingEntryPosted',
          sourceEventIds: [],
          sourceRef: 'x',
          ruleSetId: 'rs',
          ruleSetVersion: 1,
          ruleCodes: ['R-WRITTEN'],
          postedAt: '2026-10-07T20:21:00Z',
          totals: [eur('479.20')],
          lines: [
            line(1, '1100', 'DEBIT', '479.20'),
            line(2, '4000', 'CREDIT', '432.35'),
            line(3, '2300', 'CREDIT', unbalanced ? '40.00' : '46.85'),
          ],
        },
      },
    ],
    nextCursor: null,
    limit: 50,
  } as unknown as JournalQueryPage;
}
