import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useSearchParams } from 'react-router';

import type { CatalogueCoverage, ChargeTypeListItem, ProductListItem } from '../../api/types';
import {
  EmptyState,
  Select,
  StatusPill,
  identifierColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useChargeTypes, useCoverages, useProducts } from './api';
import { VersionStatusPill } from './VersionStatusPill';
import { isoToday, versionInForce } from './versions';

function CoverageTable({ hash }: { hash: string }) {
  const { t, i18n } = useTranslation('products');
  const query = useCoverages(hash);
  const el = i18n.language.startsWith('el');
  const columns = useMemo<DataColumn<CatalogueCoverage>[]>(
    () => [
      identifierColumn<CatalogueCoverage>('code', t('coverage.code'), (c) => c.code, { size: 160 }),
      textColumn<CatalogueCoverage>('name', t('coverage.name'), (c) =>
        el ? c.name.el : c.name.en,
      ),
      textColumn<CatalogueCoverage>('existence', t('coverage.existence'), (c) => c.existence),
      textColumn<CatalogueCoverage>('element', t('coverage.element'), (c) => c.coveredElement),
      textColumn<CatalogueCoverage>('terms', t('coverage.terms'), (c) => String(c.terms.length)),
    ],
    [t, el],
  );
  return (
    <QueryView query={query}>
      {(data) => (
        <SimpleTable<CatalogueCoverage>
          aria-label={t('coverage.label')}
          columns={columns}
          data={data.coverages ?? []}
          getRowId={(c) => c.code}
          emptyState={
            <EmptyState kind="first-use" headingLevel={3} headline={t('coverage.empty')} />
          }
        />
      )}
    </QueryView>
  );
}

function RatingSummary({ hash }: { hash: string }) {
  const { t, i18n } = useTranslation('products');
  const query = useChargeTypes(hash);
  const el = i18n.language.startsWith('el');
  const columns = useMemo<DataColumn<ChargeTypeListItem>[]>(
    () => [
      identifierColumn<ChargeTypeListItem>('code', t('rating.code'), (c) => c.code, { size: 160 }),
      textColumn<ChargeTypeListItem>('name', t('rating.name'), (c) => (el ? c.name.el : c.name.en)),
      textColumn<ChargeTypeListItem>('category', t('rating.category'), (c) => c.category),
      textColumn<ChargeTypeListItem>('handling', t('rating.handling'), (c) => c.handling),
      textColumn<ChargeTypeListItem>('coverage', t('rating.coverage'), (c) => c.coverage ?? ''),
    ],
    [t, el],
  );
  return (
    <QueryView query={query}>
      {(page) => {
        const charges = page.items as ChargeTypeListItem[];
        const premium = charges.filter((c) => c.category === 'PREMIUM').length;
        return (
          <>
            <p className={styles.muted}>
              {t('rating.summary', { count: charges.length, premium })}
            </p>
            <SimpleTable<ChargeTypeListItem>
              aria-label={t('rating.label')}
              columns={columns}
              data={charges}
              getRowId={(c) => c.code}
              emptyState={
                <EmptyState kind="first-use" headingLevel={3} headline={t('rating.empty')} />
              }
            />
          </>
        );
      }}
    </QueryView>
  );
}

/** NX-UI-14b: one product, a version at a time: windows, coverages and the rating (charge type) summary. */
export function ProductDetailPage() {
  const { t, i18n } = useTranslation('products');
  const { code } = useParams();
  const [params, setParams] = useSearchParams();
  const query = useProducts();
  return (
    <QueryView query={query} notFoundMessage={t('notFound')}>
      {(page) => {
        const product = (page.items as ProductListItem[]).find((p) => p.code === code);
        if (!product) {
          return (
            <EmptyState
              kind="first-use"
              headingLevel={2}
              headline={t('notFound')}
              action={<LinkButton to="/products">{t('back')}</LinkButton>}
            />
          );
        }
        const wanted = params.get('version');
        const selected =
          product.versions.find((v) => v.version === wanted) ??
          versionInForce(product.versions, isoToday()) ??
          product.versions[0];
        const name = i18n.language.startsWith('el') ? product.name.el : product.name.en;
        return (
          <div className={styles.page}>
            <PageHeader
              overline={t('overline')}
              title={name}
              recordId={product.code}
              subtitle={selected ? <VersionStatusPill status={selected.status} /> : undefined}
              actions={
                <>
                  <LinkButton variant="secondary" to="/products">
                    {t('back')}
                  </LinkButton>
                  <LinkButton variant="secondary" to="/admin/packs">
                    {t('links.packs')}
                  </LinkButton>
                  <LinkButton variant="secondary" to="/policies/pack-rollback">
                    {t('links.rollback')}
                  </LinkButton>
                </>
              }
            />
            {selected ? (
              <>
                <Select
                  label={t('versionSelect')}
                  options={product.versions.map((v) => ({
                    id: v.version,
                    label: `${v.version} · ${t(`status.${v.status}`, { defaultValue: v.status })}`,
                  }))}
                  value={selected.version}
                  onChange={(v) => {
                    if (v) setParams({ version: v });
                  }}
                />
                {selected.fallbackOfVersion ? (
                  <StatusPill
                    semantic="warning"
                    text={t('fallbackOf', { version: selected.fallbackOfVersion })}
                    announceChanges={false}
                  />
                ) : null}
                <Section title={t('coverage.title')} family="teal">
                  <CoverageTable hash={selected.artefactHash} />
                </Section>
                <Section title={t('rating.title')} family="brand">
                  <RatingSummary hash={selected.artefactHash} />
                </Section>
              </>
            ) : (
              <EmptyState kind="first-use" headingLevel={2} headline={t('emptyTitle')} />
            )}
          </div>
        );
      }}
    </QueryView>
  );
}
