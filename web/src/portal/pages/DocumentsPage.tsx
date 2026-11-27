import { useInfiniteQuery } from '@tanstack/react-query';
import { Download } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router';

import { Button, EmptyState, LoadingState, SkeletonText, announce } from '../../design-system';
import { PortalHero, PortalPage, PortalProblem, PortalSection } from '../ui/PortalUi';
import { usePortalFormat, usePortalTitle } from '../ui/hooks';
import { useCodeLabel } from '../ui/labels';
import rows from '../ui/pages.module.css';
import styles from '../ui/portal.module.css';
import { downloadDocument, listDocuments, portalKeys, type PortalDocument } from './portalApi';

/** Saves the streamed bytes through a temporary link: the file never goes through a URL with the token in it. */
function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

function DocumentRow({ doc }: { doc: PortalDocument }) {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const label = useCodeLabel();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  const download = async () => {
    setBusy(true);
    setFailed(false);
    try {
      const { blob } = await downloadDocument(doc.documentId);
      saveBlob(blob, `${doc.documentNumber}.pdf`);
      announce(t('docs.downloaded', { number: doc.documentNumber }));
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <li className={rows.row}>
      <div className={rows.rowMain}>
        <p className={rows.rowTitle}>
          {label('documentType', doc.documentType)} ·{' '}
          <span className={rows.mono}>{doc.documentNumber}</span>
        </p>
        <p className={rows.rowMeta}>{t('docs.created', { date: fmt.date(doc.createdAt) })}</p>
      </div>
      <div className={rows.rowSide}>
        <Button
          variant="secondary"
          size="sm"
          icon={Download}
          isLoading={busy}
          aria-label={t('docs.download', { number: doc.documentNumber })}
          onPress={() => {
            void download();
          }}
        >
          {t('docs.downloadShort')}
        </Button>
      </div>
      {failed ? (
        <div className={rows.fullRow}>
          <p role="alert" className={rows.rowMeta}>
            {t('docs.downloadFailed')}
          </p>
        </div>
      ) : null}
    </li>
  );
}

/** `/portal/documents[?policy=id]`: the customer's documents, each downloadable. */
export function DocumentsPage() {
  const { t } = useTranslation('portal');
  usePortalTitle(t('nav.documents'));
  const [params] = useSearchParams();
  const policyId = params.get('policy');
  const query = useInfiniteQuery({
    queryKey: [...portalKeys.documents, policyId ?? 'all'],
    queryFn: ({ pageParam }) => listDocuments(pageParam, policyId ?? undefined),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <PortalPage>
      <PortalHero overline={t('docs.overline')} title={t('docs.title')} lead={t('docs.lead')} />
      {policyId ? (
        <div>
          <Link className={styles.textLink} to="/portal/documents">
            {t('docs.showAll')}
          </Link>
        </div>
      ) : null}
      {query.isPending ? (
        <LoadingState immediate>
          <SkeletonText lines={4} />
        </LoadingState>
      ) : query.isError ? (
        <PortalProblem
          error={query.error}
          title={t('docs.loadFailed')}
          onRetry={() => {
            void query.refetch();
          }}
        />
      ) : items.length === 0 ? (
        <EmptyState
          kind="first-use"
          headingLevel={2}
          headline={t('docs.empty')}
          description={t('docs.emptyBody')}
        />
      ) : (
        <PortalSection title={t('docs.listTitle', { count: items.length })} family="info">
          <ul className={rows.rows}>
            {items.map((d) => (
              <DocumentRow key={d.documentId} doc={d} />
            ))}
          </ul>
          {query.hasNextPage ? (
            <div>
              <Button
                variant="secondary"
                isLoading={query.isFetchingNextPage}
                onPress={() => {
                  void query.fetchNextPage();
                }}
              >
                {t('states.loadMore')}
              </Button>
            </div>
          ) : null}
        </PortalSection>
      )}
    </PortalPage>
  );
}
