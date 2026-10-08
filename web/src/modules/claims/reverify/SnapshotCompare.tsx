import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import { Banner, KeyValueList } from '../../../design-system';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useSnapshot, type SnapshotResponse } from './api';
import file from './reverify.module.css';

function selectedCodes(snapshot: SnapshotResponse): string[] {
  return (snapshot.content?.coverages ?? []).filter((c) => c.selected).map((c) => c.coverageCode);
}

function SnapshotColumn({
  heading,
  snapshot,
  other,
}: {
  heading: string;
  snapshot: SnapshotResponse;
  other: string[];
}) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const content = snapshot.content;
  const codes = selectedCodes(snapshot);
  const vehicle = content?.vehicles[0];
  return (
    <section className={file.column} aria-label={heading}>
      <h3 className="ds-h3">{heading}</h3>
      {content ? (
        <>
          <KeyValueList
            aria-label={heading}
            items={[
              {
                id: 'known',
                label: t('reverify.compare.knownAt'),
                value: fmt.dateTime(snapshot.knownAt),
              },
              {
                id: 'vehicle',
                label: t('reverify.compare.vehicle'),
                value: vehicle
                  ? [vehicle.make, vehicle.model, vehicle.firstRegistrationYear]
                      .filter(Boolean)
                      .join(' ')
                  : '—',
              },
              {
                id: 'value',
                label: t('reverify.compare.vehicleValue'),
                value: fmt.money(vehicle?.value),
              },
              {
                id: 'version',
                label: t('reverify.compare.productVersion'),
                value: String(content.productVersion),
                kind: 'mono',
              },
            ]}
          />
          <p className="ds-caption">{t('reverify.compare.coverages')}</p>
          {codes.length ? (
            <ul className={file.codes} aria-label={t('reverify.compare.coverages')}>
              {codes.map((code) => (
                <li key={code}>
                  {other.includes(code) ? (
                    <span className="ds-mono">{code}</span>
                  ) : (
                    <>
                      <span className={`ds-mono ${file.changed}`}>{code}</span>{' '}
                      <span className="ds-caption">{t('reverify.compare.onlyHere')}</span>
                    </>
                  )}
                </li>
              ))}
            </ul>
          ) : (
            <p className={styles.muted}>{t('reverify.compare.noCoverages')}</p>
          )}
        </>
      ) : (
        <Banner variant="warning" live="none" title={t('reverify.compare.notInForce')} />
      )}
    </section>
  );
}

/** Old and new snapshot side by side: vehicle and selected coverages, differences marked (REQ-CLM-058 subset). */
export function SnapshotCompare({ oldRef, newRef }: { oldRef: string; newRef: string }) {
  const { t } = useTranslation('claims');
  const before = useSnapshot(oldRef, true);
  const after = useSnapshot(newRef, true);
  // QueryView renders loading, error (retry, trace id) and the no-permission (403) state.
  const query = useMemo(
    () => ({
      data: before.data && after.data ? { before: before.data, after: after.data } : undefined,
      isPending: before.isPending || after.isPending,
      isError: before.isError || after.isError,
      error: before.error ?? after.error,
      refetch: () => Promise.all([before.refetch(), after.refetch()]),
    }),
    [before, after],
  );
  return (
    <QueryView query={query}>
      {({ before: b, after: a }) => (
        <div className={file.compare}>
          <SnapshotColumn
            heading={t('reverify.compare.current')}
            snapshot={b}
            other={selectedCodes(a)}
          />
          <SnapshotColumn
            heading={t('reverify.compare.new')}
            snapshot={a}
            other={selectedCodes(b)}
          />
        </div>
      )}
    </QueryView>
  );
}
