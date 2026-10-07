import type { CSSProperties, ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import { cx } from '../../utils/cx';
import styles from './Skeleton.module.css';
import { useDelayedLoading } from './useDelayedLoading';

export type SkeletonShape = 'line' | 'block' | 'circle';

export interface SkeletonProps {
  /** `line` = one text line (height = line height − 8), `block` = a box, `circle` = an avatar. */
  shape?: SkeletonShape;
  /** CSS width, e.g. `'72%'` or `'120px'`. Lines default to 100 %. */
  width?: string;
  /** CSS height for blocks and circles, e.g. `'96px'`. */
  height?: string;
}

/** One placeholder shape (Part 2 §4.24): `--color-skeleton`, `radius.sm`, MI-33 shimmer. Decorative. */
export function Skeleton({ shape = 'line', width, height }: SkeletonProps) {
  const style: CSSProperties = {};
  if (width !== undefined) style.inlineSize = width;
  if (height !== undefined) style.blockSize = height;
  if (shape === 'circle' && height === undefined && width !== undefined) style.blockSize = width;
  return <span className={styles.skeleton} data-shape={shape} style={style} aria-hidden="true" />;
}

const lineWidths = ['90%', '76%', '84%', '62%', '70%'];

export interface SkeletonTextProps {
  /** Number of lines; widths vary between 60 % and 90 % like real text. */
  lines?: number;
}

/** Paragraph placeholder: lines at 60–90 % width. */
export function SkeletonText({ lines = 3 }: SkeletonTextProps) {
  return (
    <span className={styles.text} aria-hidden="true">
      {Array.from({ length: lines }, (_, index) => (
        <Skeleton key={index} width={lineWidths[index % lineWidths.length] ?? '80%'} />
      ))}
    </span>
  );
}

export interface SkeletonRegionProps {
  isLoading: boolean;
  /** Skeleton layout that matches the final content (blocks, lines). */
  fallback: ReactNode;
  /** The loaded content. */
  children: ReactNode;
  /** Visually hidden busy text; defaults to «Φόρτωση…». */
  label?: string;
  className?: string;
}

/**
 * Loading region (Part 2 §4.24, A.11): shows nothing for the first 150 ms, then the skeleton for at least
 * 300 ms, then the content. The container carries `aria-busy` and a hidden «Φόρτωση…» while loading.
 */
export function SkeletonRegion({
  isLoading,
  fallback,
  children,
  label,
  className,
}: SkeletonRegionProps) {
  const { t } = useTranslation('ds');
  const showSkeleton = useDelayedLoading(isLoading);
  const busy = isLoading || showSkeleton;
  return (
    <div className={cx(styles.region, className)} aria-busy={busy || undefined}>
      {busy ? <span className="ds-visually-hidden">{label ?? t('skeleton.loading')}</span> : null}
      {showSkeleton ? <div className={styles.fallback}>{fallback}</div> : null}
      {!busy ? <div className={styles.content}>{children}</div> : null}
    </div>
  );
}
