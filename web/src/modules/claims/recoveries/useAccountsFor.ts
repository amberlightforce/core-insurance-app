import { useMemo } from 'react';

import type { ClaimPayeeAccountView } from '../../../api/types';
import { usePayeeAccounts } from '../api';

/** The claim's captured payee accounts (masked IBANs only), stable between renders. */
export function useAccountsFor(claimId: string): ClaimPayeeAccountView[] {
  const accounts = usePayeeAccounts(claimId);
  return useMemo(() => accounts.data?.items ?? [], [accounts.data]);
}
