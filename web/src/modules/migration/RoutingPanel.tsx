import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import { Button, Select, StatusPill, TextField, toast } from '../../design-system';
import {
  overrideRoute,
  resolveRoute,
  routeStates,
  type RouteState,
  type RoutingResolveResponse,
} from './api';
import styles from './Migration.module.css';
import { ProblemBanner } from './ProblemBanner';

interface Lookup extends RoutingResolveResponse {
  asOf: string;
}

const isoDate = /^\d{4}-\d{2}-\d{2}$/;

/** the contract's master state words (the in-process names) mapped to the stored route states */
const stateOf = (masterState: RoutingResolveResponse['masterState']): RouteState =>
  masterState === 'CoreMaster'
    ? 'CORE'
    : masterState === 'Transitioning'
      ? 'TRANSITIONING'
      : 'NONE';

/**
 * Routing: which system is master for an object on a date. Lookups accumulate into a timeline (sorted by date)
 * so a cut-over can be walked date by date; an override sets a master state over [from, to).
 */
export function RoutingPanel() {
  const { t } = useTranslation('migration');
  const { keyFor, release } = useIdempotencyKey();
  const [objectType, setObjectType] = useState('Policy');
  const [key, setKey] = useState('');
  const [date, setDate] = useState('');
  const [lookups, setLookups] = useState<Lookup[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [tried, setTried] = useState(false);
  const [state, setState] = useState<RouteState>('TRANSITIONING');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [reason, setReason] = useState('');
  const [overriding, setOverriding] = useState(false);

  const dateBad = date !== '' && !isoDate.test(date);
  const resolve = async () => {
    setTried(true);
    if (key.trim() === '' || dateBad) return;
    setBusy(true);
    setError(null);
    try {
      const result = await resolveRoute(objectType.trim(), key.trim(), date);
      const asOf = date || new Date().toISOString().slice(0, 10);
      setLookups((prev) =>
        [...prev.filter((l) => l.asOf !== asOf), { ...result, asOf }].sort((a, b) =>
          a.asOf.localeCompare(b.asOf),
        ),
      );
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const override = async () => {
    setTried(true);
    if (
      key.trim() === '' ||
      !isoDate.test(from) ||
      (to !== '' && !isoDate.test(to)) ||
      !reason.trim()
    )
      return;
    const body = {
      objectType: objectType.trim(),
      key: key.trim(),
      masterState: state,
      from,
      ...(to ? { to } : {}),
      reason: reason.trim(),
    };
    setBusy(true);
    setError(null);
    try {
      await overrideRoute(body, keyFor(body));
      release();
      toast.success({ title: t('routing.overridden'), description: key.trim() });
      setOverriding(false);
      setReason('');
      setDate(from);
      setBusy(false);
      const result = await resolveRoute(body.objectType, body.key, from);
      setLookups((prev) =>
        [...prev.filter((l) => l.asOf !== from), { ...result, asOf: from }].sort((a, b) =>
          a.asOf.localeCompare(b.asOf),
        ),
      );
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.stack}>
      <p className="ds-caption">{t('routing.help')}</p>
      <div className={styles.inline}>
        <TextField label={t('routing.objectType')} value={objectType} onChange={setObjectType} />
        <TextField
          label={t('routing.key')}
          value={key}
          onChange={setKey}
          errorMessage={tried && key.trim() === '' ? t('routing.keyRequired') : undefined}
        />
        <TextField
          label={t('routing.date')}
          placeholder="2026-01-31"
          value={date}
          onChange={setDate}
          helperText={t('routing.dateHelp')}
          errorMessage={tried && dateBad ? t('routing.dateInvalid') : undefined}
        />
        <Button variant="primary" isLoading={busy && !overriding} onPress={() => void resolve()}>
          {t('routing.resolve')}
        </Button>
        <Button
          variant="secondary"
          onPress={() => {
            setOverriding((v) => !v);
          }}
        >
          {t('routing.overrideOpen')}
        </Button>
      </div>
      {overriding ? (
        <form
          className={styles.inline}
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            void override();
          }}
        >
          <Select
            label={t('routing.state')}
            options={routeStates.map((s) => ({ id: s, label: t(`routing.states.${s}`) }))}
            value={state}
            onChange={(value) => {
              if (value) setState(value as RouteState);
            }}
          />
          <TextField
            label={t('routing.from')}
            placeholder="2026-01-01"
            value={from}
            onChange={setFrom}
            errorMessage={tried && !isoDate.test(from) ? t('routing.dateInvalid') : undefined}
          />
          <TextField
            label={t('routing.to')}
            placeholder="2026-06-01"
            value={to}
            onChange={setTo}
            errorMessage={
              tried && to !== '' && !isoDate.test(to) ? t('routing.dateInvalid') : undefined
            }
          />
          <TextField
            label={t('routing.reason')}
            value={reason}
            onChange={setReason}
            errorMessage={tried && !reason.trim() ? t('routing.reasonRequired') : undefined}
          />
          <Button type="submit" variant="commit" isLoading={busy && overriding}>
            {t('routing.override')}
          </Button>
        </form>
      ) : null}
      {error ? <ProblemBanner error={error} /> : null}
      {lookups.length > 0 ? (
        <ol className={styles.timeline} aria-label={t('routing.timeline')}>
          {lookups.map((l) => (
            <li key={l.asOf}>
              <span className="ds-mono">{l.asOf}</span>
              <StatusPill
                semantic={
                  stateOf(l.masterState) === 'CORE'
                    ? 'success'
                    : stateOf(l.masterState) === 'TRANSITIONING'
                      ? 'info'
                      : 'disabled'
                }
                text={t(`routing.states.${stateOf(l.masterState)}`)}
                announceChanges={false}
              />
              {l.validFrom ? (
                <span className="ds-caption">
                  {t('routing.period', { from: l.validFrom, to: l.validTo ?? t('routing.open') })}
                </span>
              ) : (
                <span className="ds-caption">{t('routing.noRoute')}</span>
              )}
              {l.reason ? <span className="ds-caption">{l.reason}</span> : null}
            </li>
          ))}
        </ol>
      ) : null}
    </div>
  );
}
