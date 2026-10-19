import { useTranslation } from 'react-i18next';

import { useFsCases } from './api';

/** Facts-row entry «Φιλικός διακανονισμός: Επιλέξιμη / Μη επιλέξιμη» from the latest stored evaluation. */
export function useFsHeaderFact(claimId: string) {
  const { t } = useTranslation('claims');
  const query = useFsCases(claimId);
  const items = query.data?.items ?? [];
  const eligibility = (items.find((c) => c.role === 'OWN_INSURER') ?? items[0])?.eligibility;
  if (!eligibility || eligibility.result === 'NOT_APPLICABLE') return null;
  return {
    id: 'fs',
    label: t('fs.fact'),
    value: eligibility.result === 'ELIGIBLE' ? t('fs.eligible') : t('fs.notEligible'),
  };
}
