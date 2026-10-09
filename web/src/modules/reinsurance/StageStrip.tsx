import { Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../design-system/icons';
import type { ContractStatus } from './api';
import styles from './Reinsurance.module.css';

export type StageId = 'draft' | 'submitted' | 'approved' | 'active' | 'expired';
type StageState = 'done' | 'current' | 'todo';

const order: readonly StageId[] = ['draft', 'submitted', 'approved', 'active', 'expired'];

const reached: Record<ContractStatus, number> = {
  DRAFT: 0,
  PENDING_APPROVAL: 1,
  APPROVED: 2,
  ACTIVE: 3,
  EXPIRED: 5,
  CLOSED: 5,
};

/** The treaty's stages from its status: earlier ones done, the status' own one current, later ones to come. */
export function contractStages(status: ContractStatus): { id: StageId; state: StageState }[] {
  const at = reached[status];
  return order.map((id, index) => ({
    id,
    state: index < at ? 'done' : index === at ? 'current' : 'todo',
  }));
}

const stateKeys = {
  done: 'stage.state.done',
  current: 'stage.state.current',
  todo: 'stage.state.todo',
} as const;

/**
 * The treaty's stage strip (v3 mockup `.strip`): 22 px nodes, done green with a check, current primary with a halo,
 * later stages hollow, joined by tide-gradient connectors. Each stage's state is also in words (visually hidden).
 */
export function StageStrip({ status }: { status: ContractStatus }) {
  const { t } = useTranslation('reinsurance');
  const stages = contractStages(status);
  const furthest = stages.reduce(
    (last, stage, index) => (stage.state === 'todo' ? last : index),
    0,
  );
  return (
    <ol className={styles.strip} aria-label={t('stage.label')}>
      {stages.map((stage, index) => (
        <li
          key={stage.id}
          className={styles.stage}
          data-state={stage.state}
          {...(stage.state === 'current' ? { 'aria-current': 'step' as const } : {})}
        >
          {index > 0 ? (
            <span
              className={styles.connector}
              data-filled={index <= furthest || undefined}
              aria-hidden="true"
            />
          ) : null}
          <span className={styles.node} aria-hidden="true">
            {stage.state === 'done' ? <Icon icon={Check} size={12} /> : null}
          </span>
          <span>{t(`stage.${stage.id}`)}</span>
          <span className="ds-visually-hidden">{t(stateKeys[stage.state])}</span>
        </li>
      ))}
    </ol>
  );
}
