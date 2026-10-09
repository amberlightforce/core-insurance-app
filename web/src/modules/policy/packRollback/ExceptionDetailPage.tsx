import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { PackRollbackExceptionView } from '../../../api/types';
import {
  Banner,
  Button,
  Dialog,
  KeyValueList,
  Select,
  TextField,
  announce,
} from '../../../design-system';
import { problemOf } from '../../staff/problem';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useCanReview, useException, useReviewException } from './api';
import { useKindLabel } from './kinds';
import { ExceptionStatusPill, HashText } from './parts';

type Outcome = 'NO_ACTION' | 'CORRECTION_REQUIRED';
const reasonMax = 500;

function ReviewDialog({
  exception,
  onClose,
}: {
  exception: PackRollbackExceptionView;
  onClose: () => void;
}) {
  const { t } = useTranslation('policy');
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useReviewException();
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const missingOutcome = outcome === null;
  const missingReason = reason.trim() === '';

  const problem = mutation.isError ? problemOf(mutation.error) : null;
  const error = problem
    ? problem.status === 403
      ? t('packRollback.review.forbidden')
      : problem.status === 409
        ? t('packRollback.review.conflict')
        : (problem.title ?? t('packRollback.review.failed'))
    : undefined;

  const submit = () => {
    setTried(true);
    if (missingOutcome || missingReason) return;
    const body = { exceptionId: exception.exceptionId, outcome, reason: reason.trim() };
    return mutation
      .mutateAsync({ body, key: keyFor(body) })
      .then(() => {
        release();
        announce(t('packRollback.review.done'));
        onClose();
      })
      .catch(() => {
        // Shown in the dialog banner; the input is kept.
      });
  };

  return (
    <Dialog
      title={t('packRollback.review.title', { policy: exception.policyNumber })}
      size="md"
      isOpen
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      onCancel={onClose}
      closeOnAction={false}
      cancelLabel={t('packRollback.review.cancel')}
      {...(error ? { error } : {})}
      primaryAction={{
        label: t('packRollback.review.submit'),
        variant: 'primary',
        onAction: submit,
      }}
    >
      <div className={styles.stack}>
        <p className="ds-caption">{t('packRollback.review.note')}</p>
        <Select
          label={t('packRollback.review.outcome')}
          isRequired
          options={[
            { id: 'NO_ACTION', label: t('packRollback.outcome.NO_ACTION') },
            { id: 'CORRECTION_REQUIRED', label: t('packRollback.outcome.CORRECTION_REQUIRED') },
          ]}
          value={outcome}
          onChange={(value) => {
            setOutcome(value === 'NO_ACTION' || value === 'CORRECTION_REQUIRED' ? value : null);
          }}
          errorMessage={
            tried && missingOutcome ? t('packRollback.review.outcomeRequired') : undefined
          }
        />
        <TextField
          label={t('packRollback.review.reason')}
          multiline
          minRows={3}
          maxLength={reasonMax}
          showCount
          isRequired
          value={reason}
          onChange={setReason}
          errorMessage={
            tried && missingReason ? t('packRollback.review.reasonRequired') : undefined
          }
        />
      </div>
    </Dialog>
  );
}

function ExceptionDetail({ exception }: { exception: PackRollbackExceptionView }) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const kindLabel = useKindLabel();
  const canReview = useCanReview();
  const [reviewing, setReviewing] = useState(false);
  const open = exception.status === 'OPEN';

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('packRollback.overline')}
        title={t('packRollback.detail.title')}
        recordId={exception.policyNumber}
        subtitle={<ExceptionStatusPill status={exception.status} />}
        facts={[
          {
            id: 'pack',
            label: t('packRollback.fields.pack'),
            value: (
              <span className="ds-mono">
                {exception.pack} {exception.fromVersion} → {exception.toVersion}
              </span>
            ),
          },
          {
            id: 'kind',
            label: t('packRollback.fields.transactionKind'),
            value: kindLabel(exception.transactionKind),
          },
        ]}
        actions={
          <>
            {open ? (
              <Button
                variant="primary"
                {...(!canReview ? { disabledReason: t('packRollback.review.noRole') } : {})}
                onPress={() => {
                  setReviewing(true);
                }}
              >
                {t('packRollback.review.open')}
              </Button>
            ) : null}
            <LinkButton variant="secondary" to={`/policies/${exception.policyId}`}>
              {t('packRollback.detail.openPolicy')}
            </LinkButton>
            <LinkButton variant="secondary" to="/policies/pack-rollback">
              {t('packRollback.detail.back')}
            </LinkButton>
          </>
        }
      />
      <div className={styles.grid}>
        <Section title={t('packRollback.detail.transaction')}>
          <KeyValueList
            aria-label={t('packRollback.detail.transaction')}
            items={[
              {
                id: 'policy',
                label: t('packRollback.fields.policyNumber'),
                value: exception.policyNumber,
                kind: 'mono',
              },
              {
                id: 'kind',
                label: t('packRollback.fields.transactionKind'),
                value: kindLabel(exception.transactionKind),
              },
              {
                id: 'pv',
                label: t('packRollback.fields.productVersion'),
                value: exception.productVersion,
                kind: 'mono',
              },
              {
                id: 'hash',
                label: t('packRollback.fields.configurationHash'),
                value: <HashText value={exception.configurationHash} />,
              },
              {
                id: 'at',
                label: t('packRollback.fields.identifiedAt'),
                value: fmt.dateTime(exception.identifiedAt),
              },
            ]}
          />
        </Section>
        <Section title={t('packRollback.detail.rollback')}>
          <KeyValueList
            aria-label={t('packRollback.detail.rollback')}
            items={[
              {
                id: 'pack',
                label: t('packRollback.fields.pack'),
                value: exception.pack,
                kind: 'mono',
              },
              {
                id: 'move',
                label: t('packRollback.fields.fromTo'),
                value: `${exception.fromVersion} → ${exception.toVersion}`,
                kind: 'mono',
              },
              {
                id: 'wrk',
                label: t('packRollback.fields.wrkActivity'),
                value: t('packRollback.wrk.NOT_CREATED_WRK_NOT_BUILT'),
              },
            ]}
          />
        </Section>
      </div>
      <Banner variant="info" live="none" title={t('packRollback.detail.noCorrectionTitle')}>
        {t('packRollback.detail.noCorrectionBody')}
      </Banner>
      {exception.review ? (
        <Section title={t('packRollback.detail.review')}>
          <KeyValueList
            aria-label={t('packRollback.detail.review')}
            items={[
              {
                id: 'outcome',
                label: t('packRollback.review.outcome'),
                value: t(`packRollback.outcome.${exception.review.outcome}`),
              },
              {
                id: 'reason',
                label: t('packRollback.review.reason'),
                value: exception.review.reason,
              },
              { id: 'by', label: t('packRollback.review.by'), value: exception.review.reviewedBy },
              {
                id: 'when',
                label: t('packRollback.review.at'),
                value: fmt.dateTime(exception.review.reviewedAt),
              },
            ]}
          />
        </Section>
      ) : null}
      {reviewing ? (
        <ReviewDialog
          exception={exception}
          onClose={() => {
            setReviewing(false);
          }}
        />
      ) : null}
    </div>
  );
}

export function ExceptionDetailPage() {
  const { t } = useTranslation('policy');
  const { exceptionId = '' } = useParams();
  const query = useException(exceptionId);
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('packRollback.detail.notFound')}>
        {(exception) => <ExceptionDetail exception={exception} />}
      </QueryView>
    </div>
  );
}
