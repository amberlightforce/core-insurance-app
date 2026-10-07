import { useId, type CSSProperties } from 'react';

import { useReducedMotion } from '../../preferences/context';
import styles from './KpiTile.module.css';
import { sparklinePaths } from './sparklinePath';

export interface SparklineProps {
  values: number[];
  /** 32 px in a tile, 64 px in the hero, 20 px in a table cell. */
  height?: 20 | 32 | 64;
  /** Favourability of the trend: `adverse` draws the line in danger and the adverse horizon fill. */
  tone?: 'positive' | 'adverse' | 'neutral';
  /** Summary for `role="img"`, e.g. «Τάση: από 1,02 εκ. € σε 1,28 εκ. €». */
  label: string;
  /**
   * MI-56 draw on first render (stroke-dashoffset). Only for non-money KPIs: D-FE-05 says money never
   * animates. Off under reduced motion.
   */
  draw?: boolean;
}

/**
 * Sparkline (Part 3 §6.5): 1.5 px line in `status.brand.solid` (danger solid when adverse), area in
 * `gradient.horizon.*`, endpoint dot with a sheet-coloured ring. The area fill is the gradient token itself,
 * clipped to the area path.
 */
export function Sparkline({
  values,
  height = 32,
  tone = 'positive',
  label,
  draw = false,
}: SparklineProps) {
  const clipId = useId();
  const reduced = useReducedMotion();
  const paths = sparklinePaths(values, height);
  if (!paths) return null;
  return (
    <span
      className={styles.sparkline}
      data-tone={tone}
      data-draw={(draw && !reduced) || undefined}
      style={{ '--_h': `${String(height)}px` } as CSSProperties}
      role="img"
      aria-label={label}
    >
      <svg
        className={styles.sparkSvg}
        viewBox={`0 0 100 ${String(height)}`}
        preserveAspectRatio="none"
        aria-hidden="true"
        focusable="false"
      >
        <defs>
          <clipPath id={clipId}>
            <path d={paths.area} />
          </clipPath>
        </defs>
        <foreignObject x="0" y="0" width="100" height={height} clipPath={`url(#${clipId})`}>
          <div className={styles.sparkArea} />
        </foreignObject>
        <path className={styles.sparkLine} d={paths.line} pathLength={1} />
      </svg>
      <span
        className={styles.sparkDot}
        style={{ insetBlockStart: `${String((paths.endY / height) * 100)}%` }}
        aria-hidden="true"
      />
    </span>
  );
}
