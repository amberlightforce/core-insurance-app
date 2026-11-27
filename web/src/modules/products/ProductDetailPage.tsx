import { useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useSearchParams } from 'react-router';

import type { ChargeTypeListItem, ProductDescribeResponse, ProductListItem } from '../../api/types';
import {
  EmptyState,
  Select,
  StatusPill,
  Tabs,
  dateColumn,
  identifierColumn,
  statusColumn,
  textColumn,
  useTabSearchParam,
  type DataColumn,
  type TabItem,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useChargeTypes, useDescribe, useProducts } from './api';
import { VersionStatusPill } from './VersionStatusPill';
import { isoToday, versionInForce, type ProductVersion } from './versions';

type Described = ProductDescribeResponse;
type Coverage = Described['coverages'][number];
type Step = Described['orderedSteps'][number];
type Element = Described['elements'][number];
type QuestionSet = Described['questionSets'][number];
type Question = QuestionSet['questions'][number];
interface TermRow {
  id: string;
  coverage: string;
  code: string;
  name: string;
  kind: string;
  valueType: string;
  required: boolean;
  values: string;
  illustrative: boolean;
}

interface Localised {
  el: string;
  en: string;
}

function useName(): (n: Localised) => string {
  const { i18n } = useTranslation('products');
  const el = i18n.language.startsWith('el');
  return (n) => (el ? n.el : n.en);
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className={styles.muted}>{label}</dt>
      <dd>{children}</dd>
    </div>
  );
}

function OverviewTab({ d }: { d: Described }) {
  const { t } = useTranslation('products');
  const name = useName();
  const stepColumns = useMemo<DataColumn<Step>[]>(
    () => [
      identifierColumn<Step>('code', t('overview.step.code'), (s) => s.code, { size: 160 }),
      textColumn<Step>('name', t('overview.step.name'), (s) => name(s.name)),
      textColumn<Step>('kind', t('overview.step.kind'), (s) => s.kind),
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `name` is derived from the language alone.
    [t],
  );
  const elementColumns = useMemo<DataColumn<Element>[]>(
    () => [
      identifierColumn<Element>('code', t('overview.element.code'), (e) => e.code, { size: 160 }),
      textColumn<Element>('name', t('overview.element.name'), (e) => name(e.name)),
      textColumn<Element>('parent', t('overview.element.parent'), (e) => e.parent ?? ''),
      textColumn<Element>(
        'occurs',
        t('overview.element.occurs'),
        (e) => `${String(e.minimum)}..${String(e.maximum)}`,
      ),
      textColumn<Element>('fields', t('overview.element.fields'), (e) => String(e.fields.length)),
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `name` is derived from the language alone.
    [t],
  );
  const open = (to: string | null) => to ?? t('overview.openEnded');
  return (
    <div className={styles.grid}>
      <Section title={t('overview.terms')} family="brand">
        <dl className={styles.stack}>
          <Fact label={t('overview.currency')}>{d.contractCurrency}</Fact>
          <Fact label={t('overview.allowedTerms')}>{d.terms.allowed.join(', ')}</Fact>
          <Fact label={t('overview.defaultTerm')}>{d.terms.default}</Fact>
          <Fact label={t('overview.newBusiness')}>
            {d.windows.newBusiness.from} – {open(d.windows.newBusiness.to)}
          </Fact>
          <Fact label={t('overview.renewal')}>
            {d.windows.renewal.from} – {open(d.windows.renewal.to)}
          </Fact>
          <Fact label={t('overview.channels')}>{d.channels.join(', ')}</Fact>
          <Fact label={t('overview.languages')}>{d.languages.join(', ')}</Fact>
          <Fact label={t('overview.paymentPlans')}>
            {d.paymentPlans.length ? d.paymentPlans.join(', ') : t('overview.none')}
          </Fact>
        </dl>
      </Section>
      <Section title={t('overview.artefact')} family="ai">
        <dl className={styles.stack}>
          <Fact label={t('overview.hash')}>
            <code style={{ overflowWrap: 'anywhere' }}>{d.artefactHash}</code>
          </Fact>
          <Fact label={t('overview.abstract')}>{d.isAbstract ? t('yes') : t('no')}</Fact>
        </dl>
      </Section>
      <Section title={t('overview.steps')} family="teal" count={d.orderedSteps.length}>
        <SimpleTable<Step>
          aria-label={t('overview.steps')}
          columns={stepColumns}
          data={d.orderedSteps}
          getRowId={(s) => s.code}
          emptyState={
            <EmptyState kind="first-use" headingLevel={3} headline={t('overview.noSteps')} />
          }
        />
      </Section>
      <Section title={t('overview.elements')} family="plum" count={d.elements.length}>
        <SimpleTable<Element>
          aria-label={t('overview.elements')}
          columns={elementColumns}
          data={d.elements}
          getRowId={(e) => e.code}
          emptyState={
            <EmptyState kind="first-use" headingLevel={3} headline={t('overview.noElements')} />
          }
        />
      </Section>
    </div>
  );
}

function VersionsTab({ product, selected }: { product: ProductListItem; selected: string }) {
  const { t } = useTranslation('products');
  const inForce = versionInForce(product.versions, isoToday())?.version;
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
      textColumn<ProductVersion>('now', t('versions.now'), (v) =>
        v.version === inForce
          ? t('versions.inForce')
          : v.version === selected
            ? t('versions.selected')
            : '',
      ),
      dateColumn<ProductVersion>('nbFrom', t('columns.newBusinessFrom'), (v) => v.newBusinessFrom),
      dateColumn<ProductVersion>('nbTo', t('columns.newBusinessTo'), (v) => v.newBusinessTo),
      dateColumn<ProductVersion>('rnFrom', t('columns.renewalFrom'), (v) => v.renewalFrom),
      dateColumn<ProductVersion>('rnTo', t('columns.renewalTo'), (v) => v.renewalTo),
      textColumn<ProductVersion>('channels', t('versions.channels'), (v) => v.channels.join(', ')),
      textColumn<ProductVersion>('fallback', t('columns.fallback'), (v) =>
        v.fallbackOfVersion
          ? t('fallbackOf', { version: v.fallbackOfVersion })
          : v.replacesVersion
            ? t('replaces', { version: v.replacesVersion })
            : '',
      ),
    ],
    [t, inForce, selected],
  );
  return (
    <Section title={t('versions.title')} family="info" count={product.versions.length}>
      <SimpleTable<ProductVersion>
        aria-label={t('versionsLabel', { product: product.code })}
        columns={columns}
        data={product.versions}
        getRowId={(v) => v.version}
      />
    </Section>
  );
}

function CoveragesTab({ d }: { d: Described }) {
  const { t } = useTranslation('products');
  const name = useName();
  const columns = useMemo<DataColumn<Coverage>[]>(
    () => [
      identifierColumn<Coverage>('code', t('coverage.code'), (c) => c.code, { size: 160 }),
      textColumn<Coverage>('name', t('coverage.name'), (c) => name(c.name)),
      textColumn<Coverage>('existence', t('coverage.existence'), (c) => c.existence),
      textColumn<Coverage>('element', t('coverage.element'), (c) => c.coveredElement),
      textColumn<Coverage>('terms', t('coverage.terms'), (c) => String(c.terms.length)),
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `name` is derived from the language alone.
    [t],
  );
  const rows = useMemo<TermRow[]>(
    () =>
      d.coverages.flatMap((c) =>
        c.terms.map((term) => ({
          id: `${c.code}/${term.code}`,
          coverage: c.code,
          code: term.code,
          name: name(term.name),
          kind: term.kind,
          valueType: term.valueType,
          required: term.required,
          values: term.options?.length
            ? term.options.map((o) => name(o.name)).join(', ')
            : term.range
              ? `${term.range.minimum}..${term.range.maximum}`
              : '',
          illustrative: term.options?.some((o) => o.illustrative) ?? false,
        })),
      ),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `name` is derived from the language alone.
    [d],
  );
  const termColumns = useMemo<DataColumn<TermRow>[]>(
    () => [
      identifierColumn<TermRow>('coverage', t('coverage.code'), (r) => r.coverage, { size: 160 }),
      identifierColumn<TermRow>('code', t('term.code'), (r) => r.code, { size: 160 }),
      textColumn<TermRow>('name', t('term.name'), (r) => r.name),
      textColumn<TermRow>('kind', t('term.kind'), (r) => r.kind),
      textColumn<TermRow>('type', t('term.valueType'), (r) => r.valueType),
      textColumn<TermRow>('required', t('term.required'), (r) => (r.required ? t('yes') : t('no'))),
      textColumn<TermRow>('values', t('term.values'), (r) =>
        r.illustrative ? `${r.values} (${t('illustrative')})` : r.values,
      ),
    ],
    [t],
  );
  return (
    <div className={styles.stack}>
      <Section title={t('coverage.title')} family="teal" count={d.coverages.length}>
        <SimpleTable<Coverage>
          aria-label={t('coverage.label')}
          columns={columns}
          data={d.coverages}
          getRowId={(c) => c.code}
          emptyState={
            <EmptyState kind="first-use" headingLevel={3} headline={t('coverage.empty')} />
          }
        />
      </Section>
      <Section title={t('term.title')} family="ochre" count={rows.length}>
        <SimpleTable<TermRow>
          aria-label={t('term.title')}
          columns={termColumns}
          data={rows}
          getRowId={(r) => r.id}
          emptyState={<EmptyState kind="first-use" headingLevel={3} headline={t('term.empty')} />}
        />
      </Section>
    </div>
  );
}

function QuestionsTab({ d }: { d: Described }) {
  const { t } = useTranslation('products');
  const name = useName();
  const columns = useMemo<DataColumn<Question>[]>(
    () => [
      identifierColumn<Question>('code', t('question.code'), (q) => q.code, { size: 160 }),
      textColumn<Question>('text', t('question.text'), (q) => name(q.text)),
      textColumn<Question>('type', t('question.type'), (q) => q.answerType),
      textColumn<Question>('required', t('question.required'), (q) =>
        q.required ? t('yes') : t('no'),
      ),
      textColumn<Question>('maps', t('question.maps'), (q) => q.mapsToField ?? ''),
      textColumn<Question>('illustrative', t('question.illustrative'), (q) =>
        q.illustrative ? t('illustrative') : '',
      ),
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `name` is derived from the language alone.
    [t],
  );
  if (d.questionSets.length === 0) {
    return <EmptyState kind="first-use" headingLevel={2} headline={t('question.empty')} />;
  }
  return (
    <div className={styles.stack}>
      {d.questionSets.map((qs) => (
        <Section
          key={qs.code}
          title={name(qs.name)}
          family="plum"
          meta={`${qs.code} · ${qs.type}`}
          count={qs.questions.length}
        >
          <SimpleTable<Question>
            aria-label={name(qs.name)}
            columns={columns}
            data={qs.questions}
            getRowId={(q) => q.code}
          />
        </Section>
      ))}
    </div>
  );
}

function RatingTab({ hash }: { hash: string }) {
  const { t } = useTranslation('products');
  const name = useName();
  const query = useChargeTypes(hash);
  const columns = useMemo<DataColumn<ChargeTypeListItem>[]>(
    () => [
      identifierColumn<ChargeTypeListItem>('code', t('rating.code'), (c) => c.code, { size: 160 }),
      textColumn<ChargeTypeListItem>('name', t('rating.name'), (c) => name(c.name)),
      textColumn<ChargeTypeListItem>('category', t('rating.category'), (c) => c.category),
      textColumn<ChargeTypeListItem>('handling', t('rating.handling'), (c) => c.handling),
      textColumn<ChargeTypeListItem>('coverage', t('rating.coverage'), (c) => c.coverage ?? ''),
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `name` is derived from the language alone.
    [t],
  );
  return (
    <QueryView query={query}>
      {(page) => {
        const charges = page.items as ChargeTypeListItem[];
        const premium = charges.filter((c) => c.category === 'PREMIUM').length;
        return (
          <Section title={t('rating.title')} family="brand" count={charges.length}>
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
          </Section>
        );
      }}
    </QueryView>
  );
}

function ProductView({
  product,
  selected,
}: {
  product: ProductListItem;
  selected: ProductVersion;
}) {
  const { t, i18n } = useTranslation('products');
  const [, setParams] = useSearchParams();
  const [tab, setTab] = useTabSearchParam('tab', 'overview');
  const describe = useDescribe(selected.artefactHash);
  const name = i18n.language.startsWith('el') ? product.name.el : product.name.en;
  const tabItems = useMemo<TabItem[]>(
    () => [
      { id: 'overview', label: t('tabs.overview') },
      { id: 'versions', label: t('tabs.versions'), count: product.versions.length },
      { id: 'coverages', label: t('tabs.coverages') },
      { id: 'questions', label: t('tabs.questions') },
      { id: 'rating', label: t('tabs.rating') },
    ],
    [t, product.versions.length],
  );
  return (
    <div className={styles.page}>
      <PageHeader
        overline={t('overline')}
        title={name}
        recordId={product.code}
        subtitle={
          <>
            <VersionStatusPill status={selected.status} />
            {describe.data?.illustrative ? (
              <StatusPill semantic="warning" text={t('illustrative')} announceChanges={false} />
            ) : null}
            {selected.fallbackOfVersion ? (
              <StatusPill
                semantic="warning"
                text={t('fallbackOf', { version: selected.fallbackOfVersion })}
                announceChanges={false}
              />
            ) : null}
          </>
        }
        facts={[
          { id: 'version', label: t('columns.version'), value: selected.version },
          { id: 'currency', label: t('overview.currency'), value: selected.contractCurrency },
          {
            id: 'hash',
            label: t('overview.hash'),
            value: <code title={selected.artefactHash}>{selected.artefactHash.slice(0, 12)}</code>,
          },
        ]}
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
      <Select
        label={t('versionSelect')}
        options={product.versions.map((v) => ({
          id: v.version,
          label: `${v.version} · ${t(`status.${v.status}`, { defaultValue: v.status })}`,
        }))}
        value={selected.version}
        onChange={(v) => {
          if (v)
            setParams((prev) => {
              const next = new URLSearchParams(prev);
              next.set('version', v);
              return next;
            });
        }}
      />
      <Tabs
        aria-label={t('tabs.label')}
        items={tabItems}
        {...(tab ? { selectedKey: tab } : {})}
        onSelectionChange={setTab}
      >
        {(item) =>
          item.id === 'versions' ? (
            <VersionsTab product={product} selected={selected.version} />
          ) : item.id === 'rating' ? (
            <RatingTab hash={selected.artefactHash} />
          ) : (
            <QueryView query={describe}>
              {(d) =>
                item.id === 'coverages' ? (
                  <CoveragesTab d={d} />
                ) : item.id === 'questions' ? (
                  <QuestionsTab d={d} />
                ) : (
                  <OverviewTab d={d} />
                )
              }
            </QueryView>
          )
        }
      </Tabs>
    </div>
  );
}

/** BO-PFC-10: one product, a version at a time: overview, version timeline, coverages and terms, questions, rating. */
export function ProductDetailPage() {
  const { t } = useTranslation('products');
  const { code } = useParams();
  const [params] = useSearchParams();
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
        if (!selected) {
          return <EmptyState kind="first-use" headingLevel={2} headline={t('emptyTitle')} />;
        }
        return <ProductView product={product} selected={selected} />;
      }}
    </QueryView>
  );
}
