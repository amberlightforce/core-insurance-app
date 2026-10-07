import { Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { cx, defined } from '../../utils/cx';
import styles from './AiSuggestion.module.css';

export interface AiCitationProps {
  /** Source number shown as a superscript «[n]». */
  n: number;
  /** Source title for the accessible name: «Πηγή 1: Πραγματογνωμοσύνη 03/10/2026». */
  source?: string;
  href?: string;
  /** Opens the document viewer at the cited page. */
  onPress?: () => void;
  /** Hover/focus highlight of the source elsewhere on the page. */
  onHoverChange?: (isHovered: boolean) => void;
}

/** Superscript [n] citation link inside an AI suggestion (Part 2 §4.37). */
export function AiCitation({ n, source, href, onPress, onHoverChange }: AiCitationProps) {
  const { t } = useTranslation('ds');
  const label = source
    ? t('aiSuggestion.citationNamed', { n, source })
    : t('aiSuggestion.citation', { n });
  return (
    <sup className={styles.citationSup}>
      <Link
        className={cx(styles.citation)}
        aria-label={label}
        {...defined({ href, onPress, onHoverChange })}
        {...(onHoverChange
          ? {
              onFocus: () => {
                onHoverChange(true);
              },
              onBlur: () => {
                onHoverChange(false);
              },
            }
          : {})}
      >
        [{n}]
      </Link>
    </sup>
  );
}
