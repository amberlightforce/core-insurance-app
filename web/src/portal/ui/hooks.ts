import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import { announce, useRegionFormat } from '../../design-system';
import { formatDate, formatDateTime } from '../../format';

/** Sets `<title>` («{page} · Πύλη πελάτη») and announces the new page politely (WCAG 2.4.2). */
export function usePortalTitle(page: string): void {
  const { t } = useTranslation('portal');
  useEffect(() => {
    document.title = t('title', { page });
    announce(page);
  }, [page, t]);
}

/** Region-bound formatters (the region-format preference, never the UI language). */
export function usePortalFormat() {
  const region = useRegionFormat();
  return useMemo(
    () => ({
      date: (value: string | null | undefined): string => (value ? formatDate(value, region) : '—'),
      dateTime: (value: string | null | undefined): string =>
        value ? formatDateTime(value, region) : '—',
    }),
    [region],
  );
}
