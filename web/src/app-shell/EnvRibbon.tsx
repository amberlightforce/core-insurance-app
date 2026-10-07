import { useTranslation } from 'react-i18next';

import { toGreekUpper } from '../format/greek';
import styles from './AppShell.module.css';
import type { Environment } from './navigation';

/**
 * Environment ribbon (Part 1 §3.4): non-production only, solid (never glass, never dismissible, unaffected by
 * reduce transparency), all caps via toGreekUpper(). A shifted clock adds the system date in bold.
 */
export function EnvRibbon({ environment }: { environment: NonNullable<Environment> }) {
  const { t, i18n } = useTranslation('shell');
  const text = t(environment.kind === 'uat' ? 'envRibbon.uat' : 'envRibbon.dev', {
    env: environment.name,
  });
  const locale = i18n.language === 'en' ? 'en-GB' : 'el-GR';
  return (
    <div className={styles.envRibbon} data-kind={environment.kind} role="note">
      <span>{toGreekUpper(text, locale)}</span>
      {environment.shiftedDate ? (
        <strong>
          {' · '}
          {t('envRibbon.shiftedClock', { date: environment.shiftedDate })}
        </strong>
      ) : null}
    </div>
  );
}
