import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import type { ProductListItem } from '../../api/types';
import {
  EmptyState,
  StatusPill,
  dateColumn,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useProducts } from './api';
import { VersionStatusPill } from './VersionStatusPill';
import { isoToday, versionInForce, type ProductVersion } from './versions';

function VersionTable({ product }: { product: ProductListItem }) {
  const { t } = useTranslation('products');
  const navigate = useNavigate();
  const columns = useMemo<DataColumn<ProductVersion>[]>(
    () => [
      identifierColumn<ProductVersion>('version', t('columns.version'), (v) => v.version, {
        size: 90,
      }),
      statusColumn<ProductVersion>(
        'status',
        t('columns.status'),
        (v) => v.status,
        (v) => <VersionStatusPill status={v.status} />,
      ),
      dateColumn<ProductVersion>('nbFrom', t('columns.newBusinessFrom'), (v) => v.newBusinessFrom),
      dateColumn<ProductVersion>('nbTo', t('columns.newBusinessTo'), (v) => v.newBusinessTo),
      dateColumn<ProductVersion>('rnFrom', t('columns.renewalFrom'), (v) => v.renewalFrom),
      dateColumn<ProductVersion>('rnTo', t('columns.renewalTo'), (v) => v.renewalTo),
      textColumn<ProductVersion>('fallback', t('columns.fallback'), (v) =>
        v.fallbackOfVersion
          ? t('fallbackOf', { version: v.fallbackOfVersion })
          : v.replacesVersion
            ? t('replaces', { version: v.replacesVersion })
            : '',
      ),
    ],
    [t],
  );
  return (
    <SimpleTable<ProductVersion>
      aria-label={t('versionsLabel', { product: product.code })}
      columns={columns}
      data={product.versions}
      getRowId={(v) => v.version}
      onOpen={(v) => {
        void navigate(`/products/${product.code}?version=${v.version}`);
      }}
    />
  );
}

function ProductCard({ product }: { product: ProductListItem }) {
  const { t, i18n } = useTranslation('products');
  const current = versionInForce(product.versions, isoToday());
  const name = i18n.language.startsWith('el') ? product.name.el : product.name.en;
  return (
    <Section
      family="brand"
      title={name}
      meta={`${product.code} · ${product.lineCode}`}
      actions={
        <LinkButton variant="secondary" to={`/products/${product.code}`}>
          {t('open', { product: product.code })}
        </LinkButton>
      }
    >
      <p className={styles.muted}>
        {t('summary', {
          jurisdiction: product.jurisdiction,
          type: product.productType,
          customer: product.customerType,
        })}{' '}
        {current ? (
          <StatusPill
            semantic="success"
            text={t('inForce', { version: current.version })}
            announceChanges={false}
          />
        ) : (
          <StatusPill semantic="warning" text={t('noneInForce')} announceChanges={false} />
        )}
      </p>
      <VersionTable product={product} />
    </Section>
  );
}

/** NX-UI-14b: the product catalogue, every product with its versions, windows and fall-back links. */
export function ProductCataloguePage() {
  const { t } = useTranslation('products');
  const query = useProducts();
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('title')}
        description={t('subtitle')}
        actions={
          <>
            <LinkButton variant="secondary" to="/admin/packs">
              {t('links.packs')}
            </LinkButton>
            <LinkButton variant="secondary" to="/policies/pack-rollback">
              {t('links.rollback')}
            </LinkButton>
          </>
        }
      />
      <QueryView query={query}>
        {(page) =>
          page.items.length === 0 ? (
            <EmptyState
              kind="first-use"
              headingLevel={2}
              headline={t('emptyTitle')}
              description={t('emptyBody')}
            />
          ) : (
            <div className={styles.grid}>
              {(page.items as ProductListItem[]).map((p) => (
                <ProductCard key={p.code} product={p} />
              ))}
            </div>
          )
        }
      </QueryView>
    </div>
  );
}
