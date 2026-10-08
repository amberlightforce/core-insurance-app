import { Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../design-system/icons';
import { cx } from '../../design-system/utils/cx';
import styles from './ClaimFile.module.css';
import type { Stage } from './stages';

const stateKeys = {
  done: 'file.stageState.done',
  current: 'file.stageState.current',
  todo: 'file.stageState.todo',
  untracked: 'file.stageState.untracked',
} as const;

/**
 * The claim file's stage strip (v3 mockup `.strip`): 22 px nodes — done green with a check, current primary with
 * a halo, later stages hollow, untracked stages dashed — joined by tide-gradient connectors. A list with each
 * stage's state in words, so the colours are never the only signal.
 */
export function StageStrip({ stages }: { stages: readonly Stage[] }) {
  const { t } = useTranslation('claims');
  // Connectors are filled up to the furthest stage reached (an untracked stage in between does not break the line).
  const reached = stages.reduce(
    (last, stage, index) => (stage.state === 'done' || stage.state === 'current' ? index : last),
    0,
  );
  return (
    <ol className={cx(styles.strip)} aria-label={t('file.stages')}>
      {stages.map((stage, index) => {
        return (
          <li
            key={stage.id}
            className={cx(styles.stage)}
            data-state={stage.state}
            {...(stage.state === 'current' ? { 'aria-current': 'step' as const } : {})}
          >
            {index > 0 ? (
              <span
                className={cx(styles.connector)}
                data-filled={index <= reached || undefined}
                aria-hidden="true"
              />
            ) : null}
            <span className={cx(styles.node)} aria-hidden="true">
              {stage.state === 'done' ? <Icon icon={Check} size={12} /> : null}
            </span>
            <span className={cx(styles.stageLabel)}>{t(`file.stage.${stage.id}`)}</span>
            <span className="ds-visually-hidden">{t(stateKeys[stage.state])}</span>
          </li>
        );
      })}
    </ol>
  );
}
