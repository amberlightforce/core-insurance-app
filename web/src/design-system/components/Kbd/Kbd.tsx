import { Keyboard } from 'react-aria-components';

import { cx } from '../../utils/cx';
import styles from './Kbd.module.css';
import { formatShortcut } from './shortcut';

export interface KbdProps {
  /** Keys joined with `+`; `Mod` becomes ⌘ or Ctrl. Example: `Mod+Enter`. */
  shortcut: string;
  /** `inverse` inside tooltips; `subtle` inside buttons (opacity .55). */
  tone?: 'default' | 'inverse' | 'subtle';
}

/** Keyboard shortcut chip (Part 2 §4.34): mono, sunken, 2 px bottom border. */
export function Kbd({ shortcut, tone = 'default' }: KbdProps) {
  const keys = formatShortcut(shortcut);
  return (
    <Keyboard className={cx(styles.kbd, tone !== 'default' && styles[tone])}>
      {keys.map((key, index) => (
        <kbd key={`${key}-${String(index)}`} className={styles.key}>
          {key}
        </kbd>
      ))}
    </Keyboard>
  );
}
