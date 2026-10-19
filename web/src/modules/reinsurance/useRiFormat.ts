import { useMemo } from 'react';

import { formatPercent } from '../../format';
import { useFormat } from '../staff/useFormat';

/** The staff formatters plus the percent of a signed line / placed share («37,50 %»), bound to the region preference. */
export function useRiFormat() {
  const base = useFormat();
  return useMemo(
    () => ({
      ...base,
      percent: (value: string): string => formatPercent(value, { region: base.region }),
    }),
    [base],
  );
}
