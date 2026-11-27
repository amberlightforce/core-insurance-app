import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../design-system';
import type { MartRun } from './martApi';

/** COMPLETED awaits sign-off (info); SIGNED_OFF is success. */
export function RunPill({ run }: { run: Pick<MartRun, 'status'> }) {
  const { t } = useTranslation('reports');
  const signed = run.status === 'SIGNED_OFF';
  return (
    <StatusPill
      semantic={signed ? 'success' : 'info'}
      text={t(`run.status.${signed ? 'SIGNED_OFF' : 'COMPLETED'}`)}
      announceChanges={false}
    />
  );
}
