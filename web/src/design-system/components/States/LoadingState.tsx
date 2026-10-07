import type { CSSProperties, ReactNode } from 'react';
import { ProgressBar } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { durations } from '../../tokens';
import { cx } from '../../utils/cx';
import { SkeletonBlock } from './SkeletonBlock';
import styles from './States.module.css';
import { useElapsed } from './useElapsed';

export interface LoadingStage {
  current: number;
  total: number;
  /** What is happening, e.g. «Δημιουργία αναφοράς…». */
  label?: string;
}

export interface LoadingStateProps {
  /**
   * Skeleton slot: blocks that match the final layout (MI-33). Defaults to three text lines.
   */
  children?: ReactNode;
  /**
   * Long-running work (> 2 s): stage text, progress and an optional estimate. When `stage` is given the
   * component switches from the skeleton to the long state after 2 s, unless `kind` forces one.
   */
  stage?: LoadingStage;
  /** Already-formatted estimate, e.g. «περίπου 1 λεπτό». */
  estimate?: string;
  kind?: 'normal' | 'long';
  /** Optional action in the long state («Ειδοποίηση όταν ολοκληρωθεί»: the user can leave). */
  action?: ReactNode;
  /** Skip the 150 ms no-flash delay (stories, tests, or when the caller already waited). */
  immediate?: boolean;
}

const LONG_AFTER_MS = 2000;

/**
 * Loading (DESIGN-B A.11): fast (< 150 ms) shows nothing, normal shows skeletons with `aria-busy` and a hidden
 * «Φόρτωση…», long (> 2 s) shows the stage text with a progress bar.
 */
export function LoadingState({
  children,
  stage,
  estimate,
  kind,
  action,
  immediate = false,
}: LoadingStateProps) {
  const { t } = useTranslation('ds');
  const visible = useElapsed(immediate ? 0 : durations.delaySkeleton);
  const longElapsed = useElapsed(LONG_AFTER_MS, stage !== undefined && kind === undefined);
  const isLong = kind === 'long' || (kind === undefined && stage !== undefined && longElapsed);

  if (isLong && stage) {
    const stageText =
      stage.label === undefined
        ? t('loadingState.stageNoLabel', { current: stage.current, total: stage.total })
        : t('loadingState.stage', {
            label: stage.label,
            current: stage.current,
            total: stage.total,
          });
    return (
      <ProgressBar
        className={cx(styles.long)}
        value={stage.current}
        minValue={0}
        maxValue={stage.total}
        valueLabel={stageText}
        aria-label={stage.label ?? t('loadingState.label')}
      >
        {({ percentage }) => (
          <>
            <span className={styles.stageText}>{stageText}</span>
            <span className={styles.track} aria-hidden="true">
              <span
                className={styles.fill}
                style={{ '--_value': String((percentage ?? 0) / 100) } as CSSProperties}
              />
            </span>
            {estimate ? (
              <span className={styles.estimate}>{t('loadingState.estimate', { estimate })}</span>
            ) : null}
            {action}
          </>
        )}
      </ProgressBar>
    );
  }

  return (
    <div className={styles.loading} aria-busy="true" data-hidden={visible ? undefined : true}>
      <span className="ds-visually-hidden">{t('loadingState.label')}</span>
      {visible
        ? (children ?? (
            <>
              <SkeletonBlock width="72%" />
              <SkeletonBlock width="88%" />
              <SkeletonBlock width="60%" />
            </>
          ))
        : null}
    </div>
  );
}
