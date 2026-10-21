import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';

import { isApiError } from '../../../api/client';
import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Banner,
  Button,
  EmptyState,
  StatusPill,
  TextField,
  announce,
  identifierColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  isAllocationError,
  isStaleError,
  monthPeriod,
  useApproveNet,
  useFsStatement,
  useImportStatement,
  type FsNetView,
  type FsStatementLine,
  type FsStatementView,
} from './api';

const netSemantic: Record<string, 'info' | 'success' | 'warning' | 'disabled'> = {
  PENDING: 'info',
  APPROVAL_REQUESTED: 'warning',
  REQUESTED: 'info',
  SETTLED: 'success',
  REJECTED: 'warning',
};

/** The exception reason of a line, derived from its match status (NO_CASE / DIFFERENCE). */
function reasonOf(line: FsStatementLine): string | null {
  if (line.matchStatus === 'UNMATCHED') return 'NO_CASE';
  if (line.matchStatus === 'DIFFERENCE') return 'DIFFERENCE';
  return null;
}

function NetCard({
  statement,
  net,
  hasExceptions,
}: {
  statement: FsStatementView;
  net: FsNetView;
  hasExceptions: boolean;
}) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const approve = useApproveNet(statement);
  const { keyFor, release } = useIdempotencyKey();
  const [waiting, setWaiting] = useState(false);
  const payable = net.direction === 'PAYABLE';
  const canApprove = net.status === 'PENDING' && !hasExceptions;
  const handedOver = net.status === 'REQUESTED' || net.status === 'SETTLED';

  return (
    <div className={styles.stack} data-testid={`fs-net-${net.counterpartyCode}`}>
      <p>
        <strong>{net.counterpartyCode}</strong>{' '}
        <StatusPill
          semantic={payable ? 'warning' : 'success'}
          text={payable ? t('fsStatements.net.payable') : t('fsStatements.net.receivable')}
        />{' '}
        <StatusPill
          semantic={netSemantic[net.status] ?? 'info'}
          text={t(`fsStatements.netStatus.${net.status}`)}
        />
      </p>
      <p>
        {t('fsStatements.net.amount')}: <strong>{fmt.money(net.amount)}</strong>
      </p>
      {hasExceptions ? <p className="ds-caption">{t('fsStatements.net.blocked')}</p> : null}
      {net.status === 'APPROVAL_REQUESTED' || waiting ? (
        <p className="ds-caption">{t('fsStatements.net.pendingApproval')}</p>
      ) : null}
      {handedOver ? (
        <p className="ds-caption" data-testid={`fs-bil-${net.counterpartyCode}`}>
          {payable ? t('fsStatements.bil.payable') : t('fsStatements.bil.receivable')}
          {net.bilReferenceId ? ` (${net.bilReferenceId.slice(0, 8)})` : ''}
        </p>
      ) : null}
      {handedOver && payable ? (
        <LinkButton to="/billing/releases">{t('fsStatements.bil.openReleases')}</LinkButton>
      ) : null}
      {canApprove ? (
        <div>
          <Button
            isLoading={approve.isPending}
            onPress={() => {
              approve.mutate(
                { net, key: keyFor({ v: statement.recordVersion, net }) },
                {
                  onSuccess: (r) => {
                    release();
                    setWaiting(r.outcome === 'PENDING_APPROVAL');
                    announce(
                      r.outcome === 'PENDING_APPROVAL'
                        ? t('fsStatements.net.pendingApproval')
                        : t('fsStatements.net.approved'),
                    );
                  },
                },
              );
            }}
          >
            {t('fsStatements.net.approve')}
          </Button>
        </div>
      ) : null}
      {approve.isError ? (
        isStaleError(approve.error) ? (
          <Banner variant="warning" title={t('fsStatements.net.stale')} />
        ) : isAllocationError(approve.error) ? (
          <Banner variant="warning" title={t('fsStatements.net.allocation')} />
        ) : (
          <ProblemBanner error={approve.error} />
        )
      ) : null}
    </div>
  );
}

function StatementBody({ statement }: { statement: FsStatementView }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();

  const columns = useMemo<DataColumn<FsStatementLine>[]>(
    () => [
      identifierColumn<FsStatementLine>('ref', t('fsStatements.lines.ref'), (l) => l.lineRef),
      textColumn<FsStatementLine>(
        'cp',
        t('fsStatements.lines.counterparty'),
        (l) => l.counterpartyCode,
      ),
      textColumn<FsStatementLine>('dir', t('fsStatements.lines.direction'), (l) =>
        t(`fsStatements.direction.${l.direction}`),
      ),
      moneyColumn<FsStatementLine>(
        'amount',
        t('fsStatements.lines.amount'),
        (l) => l.amount.amount,
        { currency: 'EUR' },
      ),
      statusColumn<FsStatementLine>(
        'match',
        t('fsStatements.lines.match'),
        (l) => l.matchStatus,
        (l) => (
          <StatusPill
            semantic={l.matchStatus === 'MATCHED' ? 'success' : 'warning'}
            text={t(`fsStatements.match.${l.matchStatus}`)}
          />
        ),
      ),
      textColumn<FsStatementLine>('reason', t('fsStatements.lines.reason'), (l) => {
        const reason = reasonOf(l);
        if (!reason) return '';
        const text = t(`fsStatements.reason.${reason}`, { defaultValue: reason });
        return l.difference ? `${text}: ${fmt.money(l.difference)}` : text;
      }),
    ],
    [t, fmt],
  );

  const exceptionCps = new Set(
    statement.lines.filter((l) => l.matchStatus !== 'MATCHED').map((l) => l.counterpartyCode),
  );

  return (
    <>
      <Section title={t('fsStatements.linesTitle')}>
        <p>
          <StatusPill
            semantic={statement.reconciliation === 'MATCHED' ? 'success' : 'warning'}
            text={t(`fsStatements.reconciliation.${statement.reconciliation}`)}
          />{' '}
          <span className="ds-caption">
            {t('fsStatements.exceptionCount', { count: statement.exceptionCount })}
          </span>
        </p>
        <SimpleTable<FsStatementLine>
          aria-label={t('fsStatements.linesTitle')}
          columns={columns}
          data={statement.lines}
          getRowId={(l) => l.lineRef}
          getRowLabel={(l) => l.lineRef}
          emptyState={<EmptyState kind="first-use" headline={t('fsStatements.noLines')} />}
        />
      </Section>
      <Section title={t('fsStatements.netsTitle')}>
        {statement.nets.length === 0 ? (
          <p className={styles.muted}>{t('fsStatements.noNets')}</p>
        ) : (
          statement.nets.map((net) => (
            <NetCard
              key={net.counterpartyCode}
              statement={statement}
              net={net}
              hasExceptions={exceptionCps.has(net.counterpartyCode)}
            />
          ))
        )}
      </Section>
    </>
  );
}

/**
 * Friendly Settlement statements (SCR-CLM-09 subset, REQ-CLM-160, -264): import a month from the clearing source,
 * lines with match status and exception reasons, net per counterparty, approve net and the BIL hand-over state.
 * TODO(hardening): FsStatementLine carries only matchStatus; the server's finer reasons (DIRECTION_MISMATCH, COUNTERPARTY_MISMATCH,
 * CASE_ALREADY_MATCHED, AMOUNT_DIFFERENCE, PAYMENT_NOT_APPROVED) need a contract field before the page can show them.
 */
export function FsStatementsPage() {
  const { t } = useTranslation('claims');
  const [params, setParams] = useSearchParams();
  const statementId = params.get('statement');
  const [month, setMonth] = useState('');
  const [invalid, setInvalid] = useState(false);
  const importer = useImportStatement();
  const { keyFor, release } = useIdempotencyKey();
  const query = useFsStatement(statementId);

  const doImport = () => {
    const period = monthPeriod(month);
    setInvalid(period === null);
    if (!period) return;
    importer.mutate(
      { period, key: keyFor(period) },
      {
        onSuccess: (statement) => {
          release();
          setParams({ statement: statement.fsStatementId });
          announce(t('fsStatements.imported'));
        },
      },
    );
  };

  const disabled =
    importer.isError &&
    isApiError(importer.error) &&
    (importer.error.code ?? '').includes('FS-DISABLED');

  return (
    <div className={styles.page}>
      <PageHeader title={t('fsStatements.title')} overline={t('fs.title')} />
      <Section title={t('fsStatements.importTitle')}>
        <div className={styles.stack}>
          <TextField
            label={t('fsStatements.month')}
            placeholder="2026-10"
            value={month}
            onChange={setMonth}
            {...(invalid ? { errorMessage: t('fsStatements.monthInvalid') } : {})}
          />
          <div>
            <Button isLoading={importer.isPending} onPress={doImport}>
              {t('fsStatements.import')}
            </Button>
          </div>
          {disabled ? (
            <Banner variant="warning" title={t('fs.disabled')} />
          ) : importer.isError ? (
            <ProblemBanner error={importer.error} />
          ) : null}
        </div>
      </Section>
      {statementId ? (
        <QueryView query={query} notFoundMessage={t('fsStatements.notFound')}>
          {(statement) => <StatementBody statement={statement} />}
        </QueryView>
      ) : (
        <p className={styles.muted}>{t('fsStatements.pick')}</p>
      )}
    </div>
  );
}
