import { useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import { Banner, KeyValueList } from '../../../design-system';
import { cx } from '../../../design-system/utils/cx';
import { QueryView } from '../../staff/QueryView';
import { useFormat } from '../../staff/useFormat';
import { useSnapshot, type SnapshotResponse } from './api';
import file from './reverify.module.css';

function selectedCodes(snapshot: SnapshotResponse): string[] {
  return (snapshot.content?.coverages ?? []).filter((c) => c.selected).map((c) => c.coverageCode);
}

function vehicleLabel(snapshot: SnapshotResponse): string {
  const v = snapshot.content?.vehicles[0];
  return v ? [v.make, v.model, v.firstRegistrationYear].filter(Boolean).join(' ') : '—';
}

function SnapshotColumn({
  heading,
  snapshot,
  other,
  highlight,
  allCodes,
}: {
  heading: string;
  snapshot: SnapshotResponse;
  other: SnapshotResponse;
  /** Mark what differs from the other version (the new column). */
  highlight: boolean;
  allCodes: string[];
}) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const content = snapshot.content;
  const vehicle = content?.vehicles[0];
  const here = selectedCodes(snapshot);
  const there = selectedCodes(other);

  const mark = (differs: boolean, value: ReactNode): ReactNode =>
    highlight && differs ? (
      <>
        <span className={file.changed}>{value}</span>{' '}
        <span className="ds-caption">({t('reverify.changed')})</span>
      </>
    ) : (
      value
    );

  const otherVehicle = other.content?.vehicles[0];
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
                value: mark(vehicleLabel(snapshot) !== vehicleLabel(other), vehicleLabel(snapshot)),
              },
              {
                id: 'value',
                label: t('reverify.compare.vehicleValue'),
                value: mark(
                  vehicle?.value?.amount !== otherVehicle?.value?.amount,
                  fmt.money(vehicle?.value),
                ),
              },
              {
                id: 'version',
                label: t('reverify.compare.productVersion'),
                value: mark(
                  content.productVersion !== other.content?.productVersion,
                  content.productVersion,
                ),
              },
            ]}
          />
          <p className="ds-caption">{t('reverify.compare.coverages')}</p>
          <ul className={file.codes} aria-label={t('reverify.compare.coverages')}>
            {allCodes.map((code) => {
              const name = t(`reverify.coverageNames.${code}`, { defaultValue: code });
              if (!here.includes(code)) {
                return (
                  <li key={code} className={file.missing}>
                    {name}: {t('reverify.notIncluded')}
                  </li>
                );
              }
              return <li key={code}>{mark(!there.includes(code), name)}</li>;
            })}
          </ul>
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
      {({ before: b, after: a }) => {
        const allCodes = [...new Set([...selectedCodes(b), ...selectedCodes(a)])];
        return (
          <div className={cx(file.compare)}>
            <SnapshotColumn
              heading={t('reverify.compare.current')}
              snapshot={b}
              other={a}
              highlight={false}
              allCodes={allCodes}
            />
            <SnapshotColumn
              heading={t('reverify.compare.new')}
              snapshot={a}
              other={b}
              highlight
              allCodes={allCodes}
            />
          </div>
        );
      }}
    </QueryView>
  );
}
