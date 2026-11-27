import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import {
  Banner,
  Button,
  Dialog,
  KeyValueList,
  Select,
  TextField,
  announce,
} from '../../design-system';
import { useDevSession } from '../../dev-auth/devAuth';
import { Section } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import { QueryView } from '../staff/QueryView';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import {
  saveExport,
  useAdjust,
  useCompare,
  useExportRun,
  useRunDetail,
  useSignOff,
  type MartDetail,
  type MartFigure,
  type MartRun,
  type MartRunSummary,
} from './martApi';
import local from './reports.module.css';
import { RunPill } from './RunPill';

/** The api stores the actor id ("dev:dpo"); the session carries the canonical ActorKey ("USER:dev:dpo"). */
function sameActor(actorKey: string, createdBy: string): boolean {
  const bare = (value: string): string => value.replace(/^USER:/, '');
  return actorKey !== '' && bare(actorKey) === bare(createdBy);
}

const amountPattern = /^-?\d+(\.\d{1,4})?$/;

function FigureValue({ figure, value }: { figure: MartFigure; value: number | string }) {
  const fmt = useFormat();
  return figure.unit === 'EUR' ? (
    <>{fmt.money({ amount: String(value), currency: 'EUR' })}</>
  ) : (
    <>
      {String(value)} {figure.unit}
    </>
  );
}

function AdjustDialog({
  run,
  figure,
  onClose,
}: {
  run: MartRun;
  figure: MartFigure;
  onClose: () => void;
}) {
  const { t } = useTranslation('reports');
  const { keyFor, release } = useIdempotencyKey();
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const [evidence, setEvidence] = useState('');
  const [tried, setTried] = useState(false);
  const adjust = useAdjust(() => {
    release();
    announce(t('adjust.done'));
    onClose();
  });
  const amountOk = amountPattern.test(amount.trim());
  const valid = amountOk && reason.trim() !== '' && evidence.trim() !== '';
  return (
    <Dialog
      title={t('adjust.title')}
      isOpen
      closeOnAction={false}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      isBusy={adjust.isPending}
      primaryAction={{
        label: t('adjust.submit'),
        onAction: () => {
          setTried(true);
          if (!valid) return;
          const request = {
            runId: run.runId,
            figureKey: figure.key,
            amount: { amount: amount.trim(), currency: 'EUR' },
            reason: reason.trim(),
            evidence: evidence.trim(),
          };
          adjust.mutate({ request, key: keyFor(request) });
        },
      }}
    >
      <div className={local.formGrid}>
        <p className={styles.muted}>
          {figure.metric} · {figure.productCode || '—'} · {figure.month || '—'}
        </p>
        <TextField
          label={t('adjust.amount')}
          helperText={t('adjust.amountHelp')}
          value={amount}
          onChange={setAmount}
          inputMode="decimal"
          errorMessage={tried && !amountOk ? t('adjust.amountInvalid') : undefined}
        />
        <TextField
          label={t('adjust.reason')}
          multiline
          maxLength={1000}
          value={reason}
          onChange={setReason}
          errorMessage={tried && reason.trim() === '' ? t('adjust.required') : undefined}
        />
        <TextField
          label={t('adjust.evidence')}
          helperText={t('adjust.evidenceHelp')}
          maxLength={500}
          value={evidence}
          onChange={setEvidence}
          errorMessage={tried && evidence.trim() === '' ? t('adjust.required') : undefined}
        />
        {adjust.isError ? <ProblemBanner error={adjust.error} title={t('adjust.failed')} /> : null}
      </div>
    </Dialog>
  );
}

function Compare({ run, others }: { run: MartRun; others: MartRunSummary[] }) {
  const { t } = useTranslation('reports');
  const fmt = useFormat();
  const [other, setOther] = useState<string | null>(null);
  const compare = useCompare(other ? run.runId : null, other);
  return (
    <Section title={t('compare.title')} family="ochre" headingLevel={3}>
      {others.length === 0 ? (
        <p className={styles.muted}>{t('compare.none')}</p>
      ) : (
        <div className={styles.stack}>
          <Select
            label={t('compare.against')}
            options={others.map((r) => ({
              id: r.runId,
              label: `${fmt.dateTime(r.createdAt)} · ${r.period || t('run.allPeriods')}`,
            }))}
            value={other}
            onChange={setOther}
          />
          {other ? (
            <QueryView query={compare}>
              {({ figures, diff }) => (
                <>
                  <p className={styles.muted}>
                    {t('compare.summary', { changed: figures.changed, compared: figures.compared })}
                  </p>
                  {diff.length > 0 ? (
                    <div className={styles.scroll}>
                      <table className={styles.table}>
                        <caption>{t('compare.title')}</caption>
                        <thead>
                          <tr>
                            <th scope="col">{t('figures.key')}</th>
                            <th scope="col" className="num">
                              {t('compare.a')}
                            </th>
                            <th scope="col" className="num">
                              {t('compare.b')}
                            </th>
                            <th scope="col" className="num">
                              {t('compare.delta')}
                            </th>
                            <th scope="col">{t('compare.status')}</th>
                          </tr>
                        </thead>
                        <tbody>
                          {diff.map((d) => (
                            <tr key={d.key}>
                              <td>{d.key}</td>
                              <td className={styles.money}>{d.a ?? '—'}</td>
                              <td className={styles.money}>{d.b ?? '—'}</td>
                              <td className={styles.money}>{d.delta}</td>
                              <td>{t(`compare.state.${d.status}`)}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  ) : null}
                </>
              )}
            </QueryView>
          ) : null}
        </div>
      )}
    </Section>
  );
}

function Detail({ detail, history }: { detail: MartDetail; history: MartRunSummary[] }) {
  const { t } = useTranslation('reports');
  const fmt = useFormat();
  const session = useDevSession();
  const actorKey = session?.user.actorKey ?? (session ? `USER:${session.user.id}` : '');
  const { run, figures } = detail;
  const { keyFor, release } = useIdempotencyKey();
  const signOff = useSignOff();
  const exporter = useExportRun();
  const [adjusting, setAdjusting] = useState<MartFigure | null>(null);
  const adjustments = run.adjustments ?? [];
  const isMaker = sameActor(actorKey, run.createdBy);
  const isAdjuster = adjustments.some((a) => sameActor(actorKey, a.createdBy));
  const signed = run.status === 'SIGNED_OFF';
  const signReason = isMaker
    ? t('run.signOffMaker')
    : isAdjuster
      ? t('run.signOffAdjuster')
      : signed
        ? t('run.alreadySigned')
        : undefined;
  return (
    <div className={styles.stack}>
      <Section
        title={t('run.of', {
          mart: t(`catalogue.marts.${run.mart}.name`, { defaultValue: run.mart }),
        })}
        family={signed ? 'success' : 'info'}
        headingLevel={3}
        meta={t('catalogue.notRegulatory')}
        actions={<RunPill run={run} />}
      >
        <KeyValueList
          aria-label={t('run.title')}
          items={[
            { id: 'period', label: t('run.period'), value: run.period || t('run.allPeriods') },
            { id: 'known', label: t('run.knownAt'), value: fmt.dateTime(run.knownAt) },
            {
              id: 'created',
              label: t('run.createdBy'),
              value: `${run.createdBy} · ${fmt.dateTime(run.createdAt)}`,
            },
            { id: 'hash', label: t('run.snapshotHash'), value: run.snapshotHash, kind: 'mono' },
            {
              id: 'signoff',
              label: t('run.signedBy'),
              value: run.signOff
                ? `${run.signOff.signedBy} · ${fmt.dateTime(run.signOff.signedAt)}`
                : null,
            },
          ]}
        />
        {run.signOff && !run.signOff.valid ? (
          <Banner variant="warning" live="none" title={t('run.signOffInvalid')} />
        ) : null}
        {signOff.isError ? (
          <ProblemBanner error={signOff.error} title={t('run.signOffFailed')} />
        ) : null}
        {exporter.isError ? (
          <ProblemBanner error={exporter.error} title={t('run.exportFailed')} />
        ) : null}
        <div className={styles.actions}>
          <Button
            variant="primary"
            isLoading={signOff.isPending}
            {...(signReason ? { disabledReason: signReason } : {})}
            onPress={() => {
              signOff.mutate(
                { runId: run.runId, key: keyFor({ signOff: run.runId, n: adjustments.length }) },
                {
                  onSuccess: () => {
                    release();
                    announce(t('run.signedOff'));
                  },
                },
              );
            }}
          >
            {t('run.signOff')}
          </Button>
          {(['CSV', 'JSON'] as const).map((format) => (
            <Button
              key={format}
              variant="secondary"
              isLoading={exporter.isPending && exporter.variables.format === format}
              onPress={() => {
                exporter.mutate(
                  { runId: run.runId, format, key: keyFor({ export: run.runId, format }) },
                  { onSuccess: saveExport },
                );
              }}
            >
              {t(`run.export${format}`)}
            </Button>
          ))}
        </div>
      </Section>
      <Section title={t('figures.title')} family="teal" headingLevel={3} count={figures.length}>
        {figures.length === 0 ? (
          <p className={styles.muted}>{t('figures.empty')}</p>
        ) : (
          <div className={styles.scroll}>
            <table className={styles.table}>
              <caption>{t('figures.title')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('figures.metric')}</th>
                  <th scope="col">{t('figures.product')}</th>
                  <th scope="col">{t('figures.month')}</th>
                  <th scope="col">{t('figures.dimension')}</th>
                  <th scope="col" className="num">
                    {t('figures.value')}
                  </th>
                  <th scope="col" className="num">
                    {t('figures.adjusted')}
                  </th>
                  <th scope="col">{t('figures.actions')}</th>
                </tr>
              </thead>
              <tbody>
                {figures.map((f) => (
                  <tr key={f.key}>
                    <td>{f.metric}</td>
                    <td>{f.productCode || '—'}</td>
                    <td>{f.month || '—'}</td>
                    <td>{f.dimension || '—'}</td>
                    <td className={styles.money}>
                      <FigureValue figure={f} value={f.value} />
                    </td>
                    <td className={styles.money}>
                      <FigureValue figure={f} value={f.adjustedValue} />
                    </td>
                    <td>
                      {f.unit === 'EUR' ? (
                        <Button
                          size="sm"
                          variant="ghost"
                          aria-label={t('figures.adjustFor', { key: f.key })}
                          onPress={() => {
                            setAdjusting(f);
                          }}
                        >
                          {t('figures.adjust')}
                        </Button>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Section>
      {adjustments.length > 0 ? (
        <Section title={t('adjust.list')} family="plum" headingLevel={3} count={adjustments.length}>
          <div className={styles.scroll}>
            <table className={styles.table}>
              <caption>{t('adjust.list')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('figures.key')}</th>
                  <th scope="col" className="num">
                    {t('adjust.amount')}
                  </th>
                  <th scope="col">{t('adjust.reason')}</th>
                  <th scope="col">{t('adjust.evidence')}</th>
                  <th scope="col">{t('adjust.by')}</th>
                </tr>
              </thead>
              <tbody>
                {adjustments.map((a) => (
                  <tr key={a.adjustmentId}>
                    <td>{a.figureKey}</td>
                    <td className={styles.money}>
                      {fmt.money({ amount: String(a.amount), currency: a.currency })}
                    </td>
                    <td>{a.reason}</td>
                    <td>{a.evidence}</td>
                    <td>{a.createdBy}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Section>
      ) : null}
      <Compare
        run={run}
        others={history.filter((r) => r.mart === run.mart && r.runId !== run.runId)}
      />
      {adjusting ? (
        <AdjustDialog
          run={run}
          figure={adjusting}
          onClose={() => {
            setAdjusting(null);
          }}
        />
      ) : null}
    </div>
  );
}

/** The selected run: header, figures with adjusted values, adjustments, sign-off, export and compare. */
export function RunDetail({ runId, history }: { runId: string; history: MartRunSummary[] }) {
  const query = useRunDetail(runId);
  return (
    <QueryView query={query}>{(detail) => <Detail detail={detail} history={history} />}</QueryView>
  );
}
