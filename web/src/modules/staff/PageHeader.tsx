import type { ReactNode } from 'react';

import styles from './staff.module.css';

export interface PageHeaderProps {
  title: ReactNode;
  /** Small line above the title (module, record kind). */
  overline?: ReactNode;
  /** Secondary line under the title (identifiers, status). */
  subtitle?: ReactNode;
  actions?: ReactNode;
}

/** The one h1 of a staff screen with its actions. */
export function PageHeader({ title, overline, subtitle, actions }: PageHeaderProps) {
  return (
    <header className={styles.header}>
      <div className={styles.headerText}>
        {overline ? <p className="ds-overline">{overline}</p> : null}
        <h1 className={`ds-heading-1 ${styles.title ?? ''}`}>{title}</h1>
        {subtitle ? <div className={styles.tags}>{subtitle}</div> : null}
      </div>
      {actions ? <div className={styles.actions}>{actions}</div> : null}
    </header>
  );
}

export interface SectionProps {
  title: string;
  children: ReactNode;
  actions?: ReactNode;
  headingLevel?: 2 | 3;
}

/** A titled, opaque content section (record bodies sit on opaque sheets, never glass). */
export function Section({ title, children, actions, headingLevel = 2 }: SectionProps) {
  const Heading = headingLevel === 2 ? 'h2' : 'h3';
  return (
    <section className={styles.section} aria-label={title}>
      <div className={styles.header}>
        <Heading className={`ds-heading-3 ${styles.sectionTitle ?? ''}`}>{title}</Heading>
        {actions ? <div className={styles.actions}>{actions}</div> : null}
      </div>
      {children}
    </section>
  );
}
