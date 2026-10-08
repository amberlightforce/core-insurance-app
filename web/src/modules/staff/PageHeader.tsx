import type { ReactNode } from 'react';

import type { StatusFamily } from '../../design-system';
import styles from './staff.module.css';

export interface PageFact {
  id: string;
  label: ReactNode;
  value: ReactNode;
}

export interface PageHeaderProps {
  title: ReactNode;
  /** Small line above the title (module, record kind). */
  overline?: ReactNode;
  /** Status pills and tags next to the title (record) or under it (landing). */
  subtitle?: ReactNode;
  actions?: ReactNode;
  /**
   * - `record` (default): the v3 mockup record header strip (`.chead`) bleeding to the edges of the page sheet:
   *   overline, 20 px title, mono business id, pills, a facts row, actions top right.
   * - `landing`: a module landing «canopy» on the canvas (`.canopy`): soft sea blooms behind the title, the
   *   lead text and the actions; the page's sections then sit as cards directly on the canvas.
   */
  variant?: 'record' | 'landing';
  /** Business number shown in mono next to the title (record). */
  recordId?: string;
  /** Key facts under the title (record), e.g. «Έναρξη 15/10/2026». */
  facts?: PageFact[];
  /** Lead text under the title (landing). */
  description?: ReactNode;
  /** Extra content under the header strip, still inside it (a stage strip, an «as of» picker…). */
  children?: ReactNode;
}

/** The one h1 of a staff screen with its actions (record header strip or landing canopy). */
export function PageHeader({
  title,
  overline,
  subtitle,
  actions,
  variant = 'record',
  recordId,
  facts,
  description,
  children,
}: PageHeaderProps) {
  if (variant === 'landing') {
    return (
      <header className={styles.canopy} data-variant="landing">
        <div className={styles.canopyImagery} aria-hidden="true">
          <i />
          <i />
          <i />
        </div>
        <div className={styles.canopyText}>
          {overline ? <p className={styles.overline}>{overline}</p> : null}
          <h1 className={styles.landingTitle}>{title}</h1>
          {description ? <p className={styles.lead}>{description}</p> : null}
          {subtitle ? <div className={styles.tags}>{subtitle}</div> : null}
        </div>
        {actions ? <div className={styles.canopyActions}>{actions}</div> : null}
        {children ? <div className={styles.canopyExtra}>{children}</div> : null}
      </header>
    );
  }
  return (
    <header className={styles.header} data-variant="record">
      <div className={styles.headerRow}>
        <div className={styles.headerText}>
          {overline ? <p className={styles.overline}>{overline}</p> : null}
          <div className={styles.titleRow}>
            <h1 className={styles.title}>{title}</h1>
            {recordId ? <span className={styles.recordId}>{recordId}</span> : null}
            {subtitle ? <div className={styles.tags}>{subtitle}</div> : null}
          </div>
          {facts && facts.length > 0 ? (
            <dl className={styles.facts}>
              {facts.map((fact) => (
                <div key={fact.id} className={styles.fact}>
                  <dt>{fact.label}</dt>
                  <dd>{fact.value}</dd>
                </div>
              ))}
            </dl>
          ) : null}
        </div>
        {actions ? <div className={styles.actions}>{actions}</div> : null}
      </div>
      {children ? <div className={styles.headerExtra}>{children}</div> : null}
    </header>
  );
}

export interface SectionProps {
  title: string;
  children: ReactNode;
  actions?: ReactNode;
  headingLevel?: 2 | 3;
  /** Status family of the 8 px dot before the title (v3 mockup work-left cards). */
  family?: StatusFamily;
  /** A count shown large at the end of the header (v3 mockup `.wl .count`). */
  count?: ReactNode;
  /** Small text at the end of the header (e.g. «κάλυψη × δικαιούχος»). */
  meta?: ReactNode;
}

/** A titled, opaque content card (record bodies sit on opaque sheets, never glass). */
export function Section({
  title,
  children,
  actions,
  headingLevel = 2,
  family,
  count,
  meta,
}: SectionProps) {
  const Heading = headingLevel === 2 ? 'h2' : 'h3';
  return (
    <section className={styles.section} aria-label={title}>
      <div className={styles.sectionHeader}>
        <div className={styles.sectionHeading}>
          {family ? <span className={styles.dot} data-family={family} aria-hidden="true" /> : null}
          <Heading className={styles.sectionTitle}>{title}</Heading>
        </div>
        {meta !== undefined ? <span className={styles.sectionMeta}>{meta}</span> : null}
        {count !== undefined ? <span className={styles.sectionCount}>{count}</span> : null}
        {actions ? <div className={styles.actions}>{actions}</div> : null}
      </div>
      {children}
    </section>
  );
}
