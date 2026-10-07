import { X } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import { Toolbar } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { formatInteger } from '../../../format/numbers';
import { useReducedMotion, useRegionFormat } from '../../preferences/context';
import { durations, springs } from '../../tokens';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import styles from './BulkActionBar.module.css';
import type { DataTableBulkAction } from './types';

/** Seconds for Motion: reduced-motion fade 120 ms; exit 200 ms with ease.exit (MI-24). */
const fade = durations.feedbackMd / 1000;
const exit = durations.transitionSm / 1000;
const exitEase = [0.3, 0, 0.8, 0.15] as const;

export interface BulkActionBarProps {
  /** Number of selected records; the bar shows while it is above zero. */
  count: number;
  selectedIds: readonly string[];
  /** At most four ghost actions are shown (Part 2 §4.35). */
  actions: readonly DataTableBulkAction[];
  onClear: () => void;
}

/**
 * Floating bulk bar (MI-24): material.float, radius.xl, 48 px, centred above the sheet bottom:
 * count | ≤ 4 ghost actions | ×. Rises in with spring.smooth; opacity only under reduced motion.
 */
export function BulkActionBar({ count, selectedIds, actions, onClear }: BulkActionBarProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const reduced = useReducedMotion();
  const visible = count > 0;
  return (
    <AnimatePresence>
      {visible ? (
        <motion.div
          key="bulk-bar"
          className={styles.wrap}
          initial={reduced ? { opacity: 0 } : { opacity: 0, y: 16 }}
          animate={
            reduced
              ? { opacity: 1, transition: { duration: fade } }
              : { opacity: 1, y: 0, transition: springs.smooth }
          }
          exit={
            reduced
              ? { opacity: 0, transition: { duration: fade } }
              : { opacity: 0, y: 16, transition: { duration: exit, ease: exitEase } }
          }
        >
          <Toolbar
            aria-label={t('bulkActionBar.label')}
            className={cx(styles.bar)}
            data-material="float"
          >
            <span className={styles.count} role="status">
              {t('bulkActionBar.count', { count, formatted: formatInteger(count, region) })}
            </span>
            <span className={styles.divider} aria-hidden="true" />
            {actions.slice(0, 4).map((action) => (
              <Button
                key={action.id}
                variant="ghost"
                size="sm"
                {...(action.icon ? { icon: action.icon } : {})}
                onPress={() => {
                  action.onAction([...selectedIds]);
                }}
              >
                {action.label}
              </Button>
            ))}
            <Button
              variant="ghost"
              size="sm"
              icon={X}
              label={t('bulkActionBar.clear')}
              onPress={onClear}
            />
          </Toolbar>
        </motion.div>
      ) : null}
    </AnimatePresence>
  );
}
