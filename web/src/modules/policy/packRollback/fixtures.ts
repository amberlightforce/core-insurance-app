import type { PackRollbackExceptionView } from '../../../api/types';

export const exceptionId = '018f8000-0000-7000-8000-000000000091';
export const exception: PackRollbackExceptionView = {
  exceptionId,
  activationId: '018f8000-0000-7000-8000-000000000082',
  pack: 'GR',
  fromVersion: '0.2.0',
  toVersion: '0.1.0',
  policyId: '018f8000-0000-7000-8000-000000000092',
  policyNumber: 'SYNTHETIC-2026-0001',
  termId: '018f8000-0000-7000-8000-000000000093',
  transactionId: '018f8000-0000-7000-8000-000000000094',
  transactionKind: 'ISSUANCE',
  configurationHash: 'a'.repeat(64),
  productVersion: '1.0',
  identifiedAt: '2026-10-09T09:00:00Z',
  status: 'OPEN',
  wrkActivity: 'NOT_CREATED_WRK_NOT_BUILT',
  review: null,
};
