import { useId, type ReactNode } from 'react';

import type { FieldWidth } from '../../tokens';
import { cx } from '../../utils/cx';
import styles from './FormLayout.module.css';

export interface FormGridProps {
  children: ReactNode;
  className?: string;
}

/**
 * 12-column form grid (Part 1 §3.1) with semantic field widths. Widths follow the grid's own inline size
 * (container queries), not the viewport: ≥ 1240 / 905–1239 / < 905 px.
 */
export function FormGrid({ children, className }: FormGridProps) {
  return (
    <div className={cx(styles.container, className)}>
      <div className={styles.grid}>{children}</div>
    </div>
  );
}

export interface FormGridItemProps {
  /**
   * `xs` 2/3/6 (postcode, %, currency code) · `sm` 3/4/12 (dates, amounts, ΑΦΜ, plate) · `md` 4/6/12
   * (names, phone, selects) · `lg` 6/12/12 (email, address, IBAN) · `full` 12.
   */
  width?: FieldWidth;
  /** Start a new row even when the previous one has room. */
  newRow?: boolean;
  children: ReactNode;
}

/** One field cell in a FormGrid. Labels stay above fields (rule 2). */
export function FormGridItem({ width = 'md', newRow = false, children }: FormGridItemProps) {
  return (
    <div className={styles.item} data-width={width} data-new-row={newRow || undefined}>
      {children}
    </div>
  );
}

export interface FormSectionProps {
  /** Section heading (sentence case). */
  title: string;
  /** Optional line under the heading. */
  description?: ReactNode;
  /** Heading level (default 3: forms usually sit under the page's h1 and a step's h2). */
  headingLevel?: 2 | 3 | 4;
  /** Optional actions aligned with the heading (for example «Προσθήκη οδηγού»). */
  actions?: ReactNode;
  children: ReactNode;
}

/** A titled group of fields; sections stack with `space.stack.section`. */
export function FormSection({
  title,
  description,
  headingLevel = 3,
  actions,
  children,
}: FormSectionProps) {
  const titleId = useId();
  const Heading = `h${String(headingLevel)}` as 'h2' | 'h3' | 'h4';
  return (
    <section className={styles.section} aria-labelledby={titleId}>
      <div className={styles.header}>
        <div className={styles.headingText}>
          <Heading id={titleId} className={styles.title}>
            {title}
          </Heading>
          {description ? <p className={styles.description}>{description}</p> : null}
        </div>
        {actions ? <div className={styles.actions}>{actions}</div> : null}
      </div>
      {children}
    </section>
  );
}
