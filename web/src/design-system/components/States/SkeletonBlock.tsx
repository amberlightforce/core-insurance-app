import styles from './States.module.css';

export interface SkeletonBlockProps {
  /** CSS inline size, e.g. `'72%'` or `'96px'`. Lines use 60–90 % of the final text width. */
  width?: string;
  /** CSS block size; text lines are the line height \u2212 8 px (e.g. `'12px'` for 20 px body text). */
  height?: string;
  shape?: 'line' | 'block' | 'circle';
}

/**
 * One skeleton block (Part 2 §4.24, MI-33). Decorative: the surrounding region carries `aria-busy` and a
 * hidden «Φόρτωση…» (use `LoadingState`).
 */
export function SkeletonBlock({
  width = '100%',
  height = '12px',
  shape = 'line',
}: SkeletonBlockProps) {
  return (
    <span
      className={styles.skeleton}
      data-shape={shape}
      style={{ inlineSize: width, blockSize: height }}
      aria-hidden="true"
    />
  );
}
