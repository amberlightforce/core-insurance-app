import type { ReactNode } from 'react';

import type { MatchRange } from '../../../format/search';
import styles from './Combobox.module.css';

export interface HighlightProps {
  text: string;
  ranges?: readonly MatchRange[] | undefined;
}

/**
 * Text with the matched spans emphasised (600 weight on a `status.brand.bg` underlay, Part 2 §4.4). The marks
 * add no extra speech: the option's name stays its plain text.
 */
export function Highlight({ text, ranges }: HighlightProps) {
  if (!ranges || ranges.length === 0) return <>{text}</>;
  const parts: ReactNode[] = [];
  let cursor = 0;
  ranges.forEach((range, index) => {
    if (range.start > cursor) parts.push(text.slice(cursor, range.start));
    parts.push(
      <mark key={index} className={styles.mark}>
        {text.slice(range.start, range.end)}
      </mark>,
    );
    cursor = range.end;
  });
  if (cursor < text.length) parts.push(text.slice(cursor));
  return <>{parts}</>;
}
