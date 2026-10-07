import { Text } from 'react-aria-components';

import { cx } from '../../utils/cx';
import styles from './Combobox.module.css';
import { Highlight } from './Highlight';
import type { ComboboxOption, OptionHit } from './options';

export interface OptionContentProps<T extends ComboboxOption> {
  hit: OptionHit<T>;
}

/**
 * One option: primary text with match highlighting, a `type.caption` secondary line and optional right
 * meta (mono id or a slot). The primary text is the option's label and the caption its description.
 */
export function OptionContent<T extends ComboboxOption>({ hit }: OptionContentProps<T>) {
  const { item } = hit;
  return (
    <>
      <span className={styles.optionText}>
        <Text slot="label" className={cx(styles.optionLabel)}>
          <Highlight text={item.label} ranges={hit.labelRanges} />
        </Text>
        {item.caption ? (
          <Text slot="description" className={cx(styles.optionCaption)}>
            <Highlight text={item.caption} ranges={hit.captionRanges} />
          </Text>
        ) : null}
      </span>
      {item.metaSlot !== undefined ? (
        <span className={styles.optionMeta}>{item.metaSlot}</span>
      ) : item.meta ? (
        <span className={cx(styles.optionMeta, styles.optionMono)}>
          <Highlight text={item.meta} ranges={hit.metaRanges} />
        </span>
      ) : null}
    </>
  );
}
