import { useMemo } from 'react';

import { useRegionFormat } from '../../design-system';
import { formatDate, formatDateTime, formatMoney } from '../../format';

interface MoneyLike {
  amount: string;
  currency: string;
}

/** Greek-first formatters bound to the region-format preference (never the UI language, D-FE-22). */
export function useFormat() {
  const region = useRegionFormat();
  return useMemo(
    () => ({
      region,
      /** «1.234,56 €» from a contract Money (decimal string + currency); a missing value shows «—». */
      money: (value: MoneyLike | null | undefined): string =>
        value ? formatMoney(value.amount, { currency: value.currency, region }) : '—',
      /** A LocalDate («2026-10-07») or an instant → «07/10/2026». */
      date: (value: string | null | undefined): string => (value ? formatDate(value, region) : '—'),
      dateTime: (value: string | null | undefined): string =>
        value ? formatDateTime(value, region) : '—',
    }),
    [region],
  );
}
