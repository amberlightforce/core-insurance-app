import { useCallback, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Banner } from '../design-system/components/Banner';
import { Button } from '../design-system/components/Button';
import { Card } from '../design-system/components/Card';
import { Select } from '../design-system/components/Select';
import { EmptyState, ErrorState, LoadingState } from '../design-system/components/States';
import {
  clearSession,
  type DevSession,
  type DevUser,
  fetchDevUsers,
  readSession,
  signIn,
} from './devAuth';

type Load =
  | { kind: 'loading' }
  | { kind: 'unavailable' }
  | { kind: 'error' }
  | { kind: 'ready'; users: DevUser[] };

/**
 * Development-only sign-in page (D-SLC-03): pick a configured synthetic user, obtain a token from the api's dev
 * scheme. Shows an empty state when the api does not offer dev sign-in.
 */
export function DevSignIn() {
  const { t } = useTranslation('dev-auth');
  const [load, setLoad] = useState<Load>({ kind: 'loading' });
  const [selected, setSelected] = useState<string | null>(null);
  const [session, setSession] = useState<DevSession | null>(() => readSession());
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState(false);
  // Sent here by a 401: the previous token is no longer valid.
  const [expired] = useState(() => new URLSearchParams(window.location.search).has('expired'));

  // Sets state only when the request settles (never synchronously inside the effect).
  const loadUsers = useCallback((signal?: AbortSignal) => {
    fetchDevUsers(signal)
      .then((result) => {
        setLoad(result.kind === 'ok' ? { kind: 'ready', users: result.users } : result);
      })
      .catch((error: unknown) => {
        if (!(error instanceof DOMException && error.name === 'AbortError'))
          setLoad({ kind: 'error' });
      });
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    loadUsers(controller.signal);
    return () => {
      controller.abort();
    };
  }, [loadUsers]);

  const submit = () => {
    if (!selected) return;
    setBusy(true);
    setFailed(false);
    signIn(selected)
      .then(setSession)
      .catch(() => {
        setFailed(true);
      })
      .finally(() => {
        setBusy(false);
      });
  };

  const userOptions = useMemo(
    () =>
      load.kind === 'ready'
        ? load.users.map((user) => ({
            id: user.id,
            label: user.name,
            description: user.roles.join(', '),
          }))
        : [],
    [load],
  );

  if (load.kind === 'loading') return <LoadingState />;
  if (load.kind === 'error') {
    return (
      <ErrorState
        message={t('loadError')}
        onRetry={() => {
          setLoad({ kind: 'loading' });
          loadUsers();
        }}
      />
    );
  }
  if (load.kind === 'unavailable' || load.users.length === 0) {
    return (
      <EmptyState
        kind="first-use"
        headingLevel={1}
        headline={t('unavailableTitle')}
        description={t('unavailableBody')}
      />
    );
  }

  return (
    <Card title={t('title')} headingLevel={1}>
      {expired && !session ? (
        <Banner variant="warning" title={t('expiredTitle')}>
          {t('expiredBody')}
        </Banner>
      ) : null}
      <p>{t('intro')}</p>
      {session ? (
        <>
          <p role="status">
            {t('signedInAs', { name: session.user.name, roles: session.user.roles.join(', ') })}
          </p>
          <Button
            variant="secondary"
            onPress={() => {
              clearSession();
              setSession(null);
            }}
          >
            {t('signOut')}
          </Button>
        </>
      ) : (
        <>
          <Select
            label={t('userLabel')}
            options={userOptions}
            value={selected}
            onChange={setSelected}
            isRequired
            errorMessage={failed ? t('signInError') : undefined}
          />
          <Button variant="primary" onPress={submit} isDisabled={!selected || busy}>
            {t('signIn')}
          </Button>
        </>
      )}
    </Card>
  );
}
