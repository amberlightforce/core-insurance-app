import { ArrowRight, Info, UserCheck } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import { AuthorityMeter } from '../../components/AuthorityMeter';
import { Avatar } from '../../components/Avatar';
import { Banner } from '../../components/Banner';
import { Button } from '../../components/Button';
import { Dialog } from '../../components/Dialog';
import { TextField } from '../../components/TextField';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences';
import { cx } from '../../utils/cx';
import { amountInWords } from '../../../format/amount-in-words';
import { formatDateTime } from '../../../format/dates';
import { formatMoney } from '../../../format/numbers';
import styles from './ApprovalPanel.module.css';

export interface ApprovalChange {
  id: string;
  label: string;
  /** Formatted old value. */
  from: string;
  /** Formatted new value. */
  to: string;
}

export interface ApprovalPerson {
  id: string;
  name: string;
  role?: string;
}

export interface ApprovalPanelProps {
  /** What is being approved: «Πληρωμή αποζημίωσης · ΖΗΜ-2026-000318». */
  title: string;
  maker: ApprovalPerson;
  submittedAt: Date | string;
  justification: string;
  /** Checker group, e.g. «Ομάδα Πληρωμών Β». */
  checkerGroup: string;
  /** The signed-in user; when they are the maker the decision bar is replaced (maker ≠ checker). */
  currentUserId: string;
  changes?: ApprovalChange[];
  /** Amount in large tabular type with the amount in words (exact decimal string). */
  amount?: { value: string; currency?: string };
  /** The checker's authority limit for this kind of decision; drives the meter and escalation. */
  authorityLimit?: string;
  /** Payments: «Έγκριση» is the commit CTA (one per view). */
  isPayment?: boolean;
  evidence?: ReactNode;
  history?: ReactNode;
  onApprove: () => unknown;
  onReject: (reason: string) => unknown;
  onReturn: (reason: string) => unknown;
  /** Shown instead of «Έγκριση» when the amount exceeds the checker's authority. */
  onEscalate?: () => unknown;
}

type ReasonMode = 'reject' | 'return' | null;

function exceedsLimit(amount: string | undefined, limit: string | undefined): boolean {
  if (amount === undefined || limit === undefined) return false;
  return Number(amount) > Number(limit);
}

/**
 * Maker-checker approval card (Part 3 §5.14): maker, time, justification, a neutral diff of what changes,
 * the amount in large tabular figures with the amount in words (announced politely), the checker's authority
 * meter, evidence and recent history, and the decision bar «Απόρριψη…» · «Επιστροφή για διόρθωση…» ·
 * «Έγκριση». Reject and return require a reason. Without authority the primary becomes «Προώθηση σε
 * ανώτερο». The maker can never be the checker: for the maker the decision bar is replaced by an info message.
 * Money never animates.
 */
export function ApprovalPanel({
  title,
  maker,
  submittedAt,
  justification,
  checkerGroup,
  currentUserId,
  changes = [],
  amount,
  authorityLimit,
  isPayment = false,
  evidence,
  history,
  onApprove,
  onReject,
  onReturn,
  onEscalate,
}: ApprovalPanelProps) {
  const { t, i18n } = useTranslation('ds');
  const region = useRegionFormat();
  const [reasonMode, setReasonMode] = useState<ReasonMode>(null);
  const [reason, setReason] = useState('');
  const isOwnAction = maker.id === currentUserId;
  const overLimit = exceedsLimit(amount?.value, authorityLimit);
  const currency = amount?.currency ?? 'EUR';
  const language = i18n.language === 'en' ? 'en' : 'el';

  const closeReason = () => {
    setReasonMode(null);
    setReason('');
  };

  return (
    <section className={cx(styles.panel)} aria-label={t('approval.region')}>
      <header className={cx(styles.header)}>
        <h2 className="ds-heading-3">{title}</h2>
        <p className={cx(styles.pending)}>
          <Icon icon={UserCheck} size={14} />
          {t('approval.pending', {
            group: checkerGroup,
            time: formatDateTime(submittedAt, region),
          })}
        </p>
      </header>

      <dl className={cx(styles.facts)}>
        <div className={cx(styles.fact)}>
          <dt>{t('approval.maker')}</dt>
          <dd className={cx(styles.maker)}>
            <Avatar name={maker.name} size="sm" decorative />
            <span>
              {maker.name}
              {maker.role ? <span className={cx(styles.muted)}> · {maker.role}</span> : null}
            </span>
          </dd>
        </div>
        <div className={cx(styles.fact)}>
          <dt>{t('approval.justification')}</dt>
          <dd>{justification}</dd>
        </div>
      </dl>

      {amount ? (
        <div className={cx(styles.amountBlock)}>
          <span className={cx(styles.label)}>{t('approval.amount')}</span>
          <span className={cx(styles.amount, 'ds-num')}>
            {formatMoney(amount.value, { currency, region })}
          </span>
          <span className={cx(styles.words)} aria-live="polite">
            <span className="ds-visually-hidden">{t('approval.amountInWords')}: </span>
            {amountInWords(amount.value, { currency, language, capitalize: true })}
          </span>
        </div>
      ) : null}

      {changes.length > 0 ? (
        <div className={cx(styles.changes)}>
          <h3 className="ds-heading-4">{t('approval.changes')}</h3>
          <ul>
            {changes.map((change) => (
              <li key={change.id} className={cx(styles.change)}>
                <span className={cx(styles.changeLabel)}>{change.label}</span>
                <span className={cx(styles.changeValues)}>
                  <span className="ds-visually-hidden">{t('approval.from')} </span>
                  <del className={cx(styles.old)}>{change.from}</del>
                  <Icon icon={ArrowRight} size={12} />
                  <span className="ds-visually-hidden"> {t('approval.to')} </span>
                  <ins className={cx(styles.new)}>{change.to}</ins>
                </span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {amount && authorityLimit !== undefined ? (
        <AuthorityMeter
          label={t('approval.authority')}
          value={amount.value}
          limit={authorityLimit}
          format="money"
          size="decision"
        />
      ) : null}

      {evidence ? (
        <div className={cx(styles.block)}>
          <h3 className="ds-heading-4">{t('approval.evidence')}</h3>
          {evidence}
        </div>
      ) : null}
      {history ? (
        <div className={cx(styles.block)}>
          <h3 className="ds-heading-4">{t('approval.history')}</h3>
          {history}
        </div>
      ) : null}

      {isOwnAction ? (
        <Banner variant="info" title={t('approval.ownAction')} live="none">
          {t('approval.ownActionBody', { group: checkerGroup })}
        </Banner>
      ) : (
        <div className={cx(styles.decisionBar)} role="group" aria-label={t('approval.decisions')}>
          {overLimit ? (
            <span className={cx(styles.limitNote)}>
              <Icon icon={Info} size={14} />
              {t('approval.noAuthority')}
            </span>
          ) : null}
          <Button
            variant="danger-ghost"
            onPress={() => {
              setReasonMode('reject');
            }}
          >
            {t('approval.reject')}
          </Button>
          <Button
            variant="secondary"
            onPress={() => {
              setReasonMode('return');
            }}
          >
            {t('approval.returnForChanges')}
          </Button>
          {overLimit && onEscalate ? (
            <Button variant="primary" icon={UserCheck} onPress={() => void onEscalate()}>
              {t('approval.escalate')}
            </Button>
          ) : (
            <Button
              variant={isPayment ? 'commit' : 'primary'}
              shortcut="Mod+Enter"
              {...(overLimit ? { disabledReason: t('approval.noAuthority') } : {})}
              onPress={() => void onApprove()}
            >
              {t('approval.approve')}
            </Button>
          )}
        </div>
      )}

      <Dialog
        title={reasonMode === 'return' ? t('approval.returnTitle') : t('approval.rejectTitle')}
        isOpen={reasonMode !== null}
        onOpenChange={(open) => {
          if (!open) closeReason();
        }}
        size="sm"
        primaryAction={{
          label:
            reasonMode === 'return' ? t('approval.confirmReturn') : t('approval.confirmReject'),
          variant: reasonMode === 'return' ? 'primary' : 'danger',
          ...(reason.trim() ? {} : { disabledReason: t('approval.reasonRequired') }),
          onAction: async () => {
            if (reasonMode === 'return') await onReturn(reason.trim());
            else await onReject(reason.trim());
            closeReason();
          },
        }}
      >
        <TextField
          label={t('approval.reasonLabel')}
          multiline
          isRequired
          value={reason}
          onChange={setReason}
        />
      </Dialog>
    </section>
  );
}
