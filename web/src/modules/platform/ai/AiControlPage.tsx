import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import {
  Banner,
  Button,
  Dialog,
  StatusPill,
  Switch,
  TextField,
  announce,
} from '../../../design-system';
import { newIdempotencyKey } from '../../../api/idempotency';
import { PageHeader, Section } from '../../staff/PageHeader';
import { problemOf } from '../../staff/problem';
import styles from '../../staff/staff.module.css';
import {
  featureIdPattern,
  globalScope,
  knownFeatures,
  useFeatureStates,
  useKillSwitch,
  useSetToggle,
} from './api';

interface LogEntry {
  id: number;
  at: Date;
  action: 'enabled' | 'disabled' | 'killOn' | 'killOff';
  target: string;
  ok: boolean;
}

/** SCR-PLT-AI: AI feature list with status and toggle, the global kill switch (confirmed) and the session log. */
export function AiControlPage() {
  const { t } = useTranslation('platform');
  const [features, setFeatures] = useState<string[]>([...knownFeatures]);
  const [newId, setNewId] = useState('');
  const [idError, setIdError] = useState<string>();
  const [confirm, setConfirm] = useState<'Activate' | 'Clear' | null>(null);
  const [reason, setReason] = useState('');
  const [dialogError, setDialogError] = useState<string>();
  const [log, setLog] = useState<LogEntry[]>([]);
  const [notice, setNotice] = useState<string>();

  const states = useFeatureStates(features);
  const setToggle = useSetToggle();
  const killSwitch = useKillSwitch();

  const record = (action: LogEntry['action'], target: string, ok: boolean) => {
    setLog((prev) => [{ id: prev.length + 1, at: new Date(), action, target, ok }, ...prev]);
  };

  const loaded = states.flatMap((s) => (s.data ? [s.data] : []));
  // TODO(hardening): the server has no read of the GLOBAL scope alone; it is inferred from every feature being killed.
  const globalActive = loaded.length > 0 && loaded.every((s) => s.killSwitchActive);
  const globalKnown = loaded.length > 0;

  const toggle = async (featureId: string, enabled: boolean) => {
    setNotice(undefined);
    try {
      await setToggle.mutateAsync({
        request: { featureId, enabled },
        key: newIdempotencyKey(),
      });
      record(enabled ? 'enabled' : 'disabled', featureId, true);
      announce(t('ai.features.toggled', { feature: featureId }));
    } catch (e) {
      record(enabled ? 'enabled' : 'disabled', featureId, false);
      throw new Error(
        t('ai.features.toggleFailed', { feature: featureId, message: problemOf(e).title ?? '' }),
        { cause: e },
      );
    }
  };

  const submitKill = async () => {
    if (!confirm) return;
    if (reason.trim().length === 0) {
      setDialogError(t('ai.killSwitch.reasonRequired'));
      return;
    }
    setDialogError(undefined);
    try {
      await killSwitch.mutateAsync({
        request: { action: confirm, scope: globalScope, reason: reason.trim() },
        key: newIdempotencyKey(),
      });
      record(confirm === 'Activate' ? 'killOn' : 'killOff', globalScope, true);
      setNotice(t('ai.killSwitch.done'));
      setConfirm(null);
      setReason('');
    } catch (e) {
      record(confirm === 'Activate' ? 'killOn' : 'killOff', globalScope, false);
      setDialogError(problemOf(e).title ?? t('ai.killSwitch.failed'));
    }
  };

  const addFeature = () => {
    const id = newId.trim().toUpperCase();
    if (!featureIdPattern.test(id)) {
      setIdError(t('ai.features.addInvalid'));
      return;
    }
    setIdError(undefined);
    setFeatures((prev) => (prev.includes(id) ? prev : [...prev, id]));
    setNewId('');
  };

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('ai.overline')}
        title={t('ai.title')}
        description={t('ai.description')}
      />

      <Section title={t('ai.killSwitch.title')}>
        {globalActive ? (
          <Banner variant="danger" title={t('ai.killSwitch.activeTitle')}>
            {t('ai.killSwitch.activeBody')}
          </Banner>
        ) : (
          <p className={styles.muted}>
            {globalKnown ? t('ai.killSwitch.inactiveBody') : t('ai.killSwitch.unknownBody')}
          </p>
        )}
        <div className={styles.actions}>
          {globalActive ? (
            <Button
              variant="secondary"
              onPress={() => {
                setConfirm('Clear');
              }}
            >
              {t('ai.killSwitch.clear')}
            </Button>
          ) : (
            <Button
              variant="danger"
              onPress={() => {
                setConfirm('Activate');
              }}
            >
              {t('ai.killSwitch.activate')}
            </Button>
          )}
        </div>
        {notice ? <p role="status">{notice}</p> : null}
      </Section>

      <Section title={t('ai.features.title')}>
        {features.length === 0 ? (
          <p className={styles.muted}>{t('ai.features.empty')}</p>
        ) : (
          <table aria-label={t('ai.features.title')} style={{ width: '100%', textAlign: 'start' }}>
            <thead>
              <tr>
                <th scope="col">{t('ai.features.feature')}</th>
                <th scope="col">{t('ai.features.register')}</th>
                <th scope="col">{t('ai.features.state')}</th>
                <th scope="col">{t('ai.features.toggle')}</th>
              </tr>
            </thead>
            <tbody>
              {features.map((featureId, i) => {
                const query = states[i];
                const state = query?.data;
                return (
                  <tr key={featureId}>
                    <th scope="row" className="ds-mono">
                      {featureId}
                    </th>
                    <td>{state ? (state.registerStatus ?? t('ai.features.noRegister')) : '…'}</td>
                    <td>
                      {query?.isError ? (
                        <StatusPill
                          semantic="breached"
                          text={t('ai.features.loadFailed')}
                          announceChanges={false}
                        />
                      ) : state ? (
                        <StatusPill
                          semantic={state.allowed ? 'success' : 'warning'}
                          text={state.allowed ? t('ai.features.allowed') : t('ai.features.blocked')}
                          announceChanges={false}
                        />
                      ) : (
                        '…'
                      )}
                    </td>
                    <td>
                      <Switch
                        labelText={t('ai.features.toggleLabel', { feature: featureId })}
                        isSelected={state?.enabled ?? false}
                        onChangeAsync={(next) => toggle(featureId, next)}
                        onError={(e) => {
                          setNotice(e instanceof Error ? e.message : undefined);
                        }}
                        {...(state ? {} : { isPending: true })}
                      >
                        {t('ai.features.toggleLabel', { feature: featureId })}
                      </Switch>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
        <div className={styles.actions}>
          <TextField
            label={t('ai.features.addLabel')}
            value={newId}
            onChange={setNewId}
            errorMessage={idError}
            placeholder="AI-XXX-NN"
          />
          <Button variant="secondary" onPress={addFeature}>
            {t('ai.features.add')}
          </Button>
        </div>
      </Section>

      <Section title={t('ai.log.title')}>
        {log.length === 0 ? (
          <p className={styles.muted}>{t('ai.log.empty')}</p>
        ) : (
          <table aria-label={t('ai.log.title')} style={{ width: '100%', textAlign: 'start' }}>
            <thead>
              <tr>
                <th scope="col">{t('ai.log.time')}</th>
                <th scope="col">{t('ai.log.action')}</th>
                <th scope="col">{t('ai.log.target')}</th>
                <th scope="col">{t('ai.log.result')}</th>
              </tr>
            </thead>
            <tbody>
              {log.map((entry) => (
                <tr key={entry.id}>
                  <td>{entry.at.toLocaleTimeString()}</td>
                  <td>{t(`ai.log.${entry.action}`)}</td>
                  <td className="ds-mono">{entry.target}</td>
                  <td>{entry.ok ? t('ai.log.ok') : t('ai.log.error')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Section>

      <Dialog
        title={
          confirm === 'Clear'
            ? t('ai.killSwitch.confirmClearTitle')
            : t('ai.killSwitch.confirmActivateTitle')
        }
        tone={confirm === 'Clear' ? 'warning' : 'danger'}
        isOpen={confirm !== null}
        onOpenChange={(open) => {
          if (!open) setConfirm(null);
        }}
        onCancel={() => {
          setConfirm(null);
        }}
        cancelLabel={t('ai.killSwitch.cancel')}
        closeOnAction={false}
        {...(dialogError ? { error: dialogError } : {})}
        primaryAction={{
          label: confirm === 'Clear' ? t('ai.killSwitch.clear') : t('ai.killSwitch.activate'),
          variant: confirm === 'Clear' ? 'primary' : 'danger',
          onAction: submitKill,
        }}
      >
        <p>
          {confirm === 'Clear'
            ? t('ai.killSwitch.confirmClearBody')
            : t('ai.killSwitch.confirmActivateBody')}
        </p>
        <TextField
          label={t('ai.killSwitch.reason')}
          value={reason}
          onChange={setReason}
          isRequired
        />
      </Dialog>
    </div>
  );
}
