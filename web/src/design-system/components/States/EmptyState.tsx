import type { ReactNode } from 'react';
import { Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { cx } from '../../utils/cx';
import { Button } from '../Button';
import { IllCalmHorizon, IllEmptyArch } from './illustrations';
import { Heading, type HeadingLevel } from './Heading';
import styles from './States.module.css';

export interface ActiveFilter {
  label: string;
  value: string;
}

interface EmptyBase {
  headingLevel?: HeadingLevel;
}

/** First-use empty: illustration 240×180 + headline + one sentence + primary action + help link. */
export interface FirstUseEmptyProps extends EmptyBase {
  kind: 'first-use';
  headline: string;
  description?: string;
  /** The primary action (usually a `Button variant="primary"`). */
  action?: ReactNode;
  helpHref?: string;
  helpLabel?: string;
  /** Replace the ILL-01 placeholder (e.g. ILL-07 for AI empties). */
  illustration?: ReactNode;
}

/** Done-empty (work cleared): small Laurel illustration + a celebratory line. SM-07 is the caller's. */
export interface DoneEmptyProps extends EmptyBase {
  kind: 'done';
  headline?: string;
  description?: string;
}

/** Filtered-empty: no illustration; lists the active filters + «Εκκαθάριση φίλτρων». */
export interface FilteredEmptyProps extends EmptyBase {
  kind: 'filtered';
  filters: ActiveFilter[];
  onClearFilters?: () => void;
}

export type EmptyStateProps = FirstUseEmptyProps | DoneEmptyProps | FilteredEmptyProps;

function listFormat(language: string, items: string[]): string {
  return new Intl.ListFormat(language, { style: 'short', type: 'unit' }).format(items);
}

/** Empty states from the canonical table (DESIGN-B A.11). */
export function EmptyState(props: EmptyStateProps) {
  const { t, i18n } = useTranslation('ds');
  const level = props.headingLevel ?? 2;

  if (props.kind === 'filtered') {
    const pairs = props.filters.map((f) =>
      t('emptyState.filterPair', { label: f.label, value: f.value }),
    );
    const headline =
      pairs.length > 0
        ? t('emptyState.filteredHeadline', {
            filters: listFormat(i18n.language === 'en' ? 'en-GB' : 'el-GR', pairs),
          })
        : t('emptyState.filteredNoFilters');
    return (
      <div className={styles.empty} data-kind="filtered" role="status">
        <Heading level={level} className={styles.headline}>
          {headline}
        </Heading>
        {props.onClearFilters && pairs.length > 0 ? (
          <div className={styles.actions}>
            <Button variant="secondary" size="sm" onPress={props.onClearFilters}>
              {t('emptyState.clearFilters')}
            </Button>
          </div>
        ) : null}
      </div>
    );
  }

  if (props.kind === 'done') {
    return (
      <div className={styles.empty} data-kind="done" role="status">
        <IllCalmHorizon size="sm" />
        <Heading level={level} className={styles.headline}>
          {props.headline ?? t('emptyState.doneHeadline')}
        </Heading>
        {props.description ? <p className={styles.description}>{props.description}</p> : null}
      </div>
    );
  }

  return (
    <div className={styles.empty} data-kind="first-use">
      {props.illustration ?? <IllEmptyArch size="md" />}
      <Heading level={level} className={styles.headline}>
        {props.headline}
      </Heading>
      {props.description ? <p className={styles.description}>{props.description}</p> : null}
      {props.action || props.helpHref ? (
        <div className={styles.actions}>
          {props.action}
          {props.helpHref ? (
            <Link className={cx(styles.link)} href={props.helpHref}>
              {props.helpLabel ?? t('emptyState.help')}
            </Link>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
