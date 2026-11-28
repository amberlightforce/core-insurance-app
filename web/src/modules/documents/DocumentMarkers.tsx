import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../design-system';
import styles from './documents.module.css';
import { documentSemantic, lifecycleState, type DocumentView } from './api';

/** Status pill of a document, localised when the code is known. */
export function DocumentStatusPill({ status }: { status: string }) {
  const { t } = useTranslation('documents');
  return (
    <StatusPill
      semantic={documentSemantic(status)}
      text={t(`statuses.${status}`, { defaultValue: status })}
      announceChanges={false}
    />
  );
}

/** Hidden / superseded / withdrawn / internal markers of a library row. */
export function DocumentMarkers({
  document,
  bare = false,
}: {
  document: DocumentView;
  /** Library rows: nothing at all (no dash) when there is no marker. */
  bare?: boolean;
}) {
  const { t } = useTranslation('documents');
  const state = lifecycleState(document);
  const any = document.hidden === true || state !== 'normal' || document.customerVisible === false;
  if (!any) return bare ? null : <span className={styles.mono}>—</span>;
  return (
    <span className={styles.markers}>
      {document.hidden ? (
        <StatusPill semantic="read-only" text={t('markers.hidden')} announceChanges={false} />
      ) : null}
      {state === 'superseded' ? (
        <StatusPill semantic="warning" text={t('markers.superseded')} announceChanges={false} />
      ) : null}
      {state === 'withdrawn' ? (
        <StatusPill semantic="error" text={t('markers.withdrawn')} announceChanges={false} />
      ) : null}
      {document.customerVisible === false ? (
        <StatusPill semantic="info" text={t('markers.internal')} announceChanges={false} />
      ) : null}
    </span>
  );
}
