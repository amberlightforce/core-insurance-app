import { CircleAlert } from 'lucide-react';
import { useEffect, useId, useRef, type MouseEvent } from 'react';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import styles from './ErrorSummary.module.css';

const FOCUSABLE =
  'input:not([type="hidden"]):not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]), [tabindex]:not([tabindex="-1"])';

export interface ErrorSummaryItem {
  /** Id of the invalid control (the link focuses it). */
  fieldId: string;
  /** The field's error message, which names the field («Συμπληρώστε την ημερομηνία ζημίας.»). */
  message: string;
}

export interface ErrorSummaryProps {
  errors: ErrorSummaryItem[];
  /**
   * The summary takes focus when it appears and whenever this value changes (pass the submit count), so
   * that each failed submit announces it (WCAG 3.3.1).
   */
  focusKey?: string | number;
  /** Heading level of the title (default 2). */
  headingLevel?: 2 | 3 | 4;
  /** Called after a link moved focus to its field. */
  onNavigate?: (fieldId: string) => void;
}

/**
 * Error summary banner (Part 2 §4.2, Part 4 §9.2 Banner): shown on submit failure, it receives focus and
 * lists a link per invalid field; each link moves focus to its field. Danger family, never glass.
 */
export function ErrorSummary({
  errors,
  focusKey,
  headingLevel = 2,
  onNavigate,
}: ErrorSummaryProps) {
  const { t } = useTranslation('ds');
  const titleId = useId();
  const ref = useRef<HTMLElement>(null);
  const count = errors.length;

  useEffect(() => {
    if (count > 0) ref.current?.focus();
  }, [focusKey, count]);

  if (count === 0) return null;
  const Heading = `h${String(headingLevel)}` as 'h2' | 'h3' | 'h4';

  const go = (event: MouseEvent<HTMLAnchorElement>, fieldId: string) => {
    const element = document.getElementById(fieldId);
    // Group ids (radio or checkbox groups) focus their first focusable control.
    const target = element?.matches(FOCUSABLE) ? element : element?.querySelector(FOCUSABLE);
    if (!(target instanceof HTMLElement)) return;
    event.preventDefault();
    target.scrollIntoView({ block: 'center' });
    target.focus();
    onNavigate?.(fieldId);
  };

  return (
    <section ref={ref} className={styles.summary} tabIndex={-1} aria-labelledby={titleId}>
      <Icon icon={CircleAlert} size={20} className={styles.icon} />
      <div className={styles.body}>
        <Heading id={titleId} className={styles.title}>
          {t('errorSummary.title', { count })}
        </Heading>
        <ul className={styles.list}>
          {errors.map((error) => (
            <li key={error.fieldId}>
              <a
                className={styles.link}
                href={`#${error.fieldId}`}
                onClick={(event) => {
                  go(event, error.fieldId);
                }}
              >
                {error.message}
              </a>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
