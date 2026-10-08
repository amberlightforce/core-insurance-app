import { ArrowRight, LogIn } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { displayName } from '../app-shell/user';
import { Avatar } from '../design-system/components/Avatar';
import { Banner } from '../design-system/components/Banner';
import { Button } from '../design-system/components/Button';
import { Select } from '../design-system/components/Select';
import { EmptyState, ErrorState, LoadingState } from '../design-system/components/States';
import { Icon } from '../design-system/icons';
import { cx } from '../design-system/utils/cx';
import {
  clearSession,
  type DevSession,
  type DevUser,
  fetchDevUsers,
  readSession,
  signIn,
} from './devAuth';
import styles from './DevSignIn.module.css';

type Load =
  | { kind: 'loading' }
  | { kind: 'unavailable' }
  | { kind: 'error' }
  | { kind: 'ready'; users: DevUser[] };

/**
 * Development-only sign-in page (D-SLC-03): pick a configured synthetic user, obtain a token from the api's dev
 * scheme. Shows an empty state when the api does not offer dev sign-in. A centred card on the ambient canvas,
 * the way the production sign-in will look.
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

  let content: ReactNode;
  if (load.kind === 'loading') {
    content = <LoadingState />;
  } else if (load.kind === 'error') {
    content = (
      <ErrorState
        message={t('loadError')}
        onRetry={() => {
          setLoad({ kind: 'loading' });
          loadUsers();
        }}
      />
    );
  } else if (load.kind === 'unavailable' || load.users.length === 0) {
    content = (
      <EmptyState
        kind="first-use"
        headingLevel={2}
        headline={t('unavailableTitle')}
        description={t('unavailableBody')}
      />
    );
  } else if (session) {
    content = (
      <>
        <div className={cx(styles.who)}>
          <Avatar name={displayName(session.user.name)} size="lg" decorative />
          <p role="status" className={cx(styles.status)}>
            {t('signedInAs', { name: session.user.name, roles: session.user.roles.join(', ') })}
          </p>
        </div>
        <div className={cx(styles.actions)}>
          <Link href="/" className={cx(styles.continue)}>
            {t('continue')}
            <Icon icon={ArrowRight} size={16} />
          </Link>
          <Button
            variant="secondary"
            onPress={() => {
              clearSession();
              setSession(null);
            }}
          >
            {t('signOut')}
          </Button>
        </div>
      </>
    );
  } else {
    content = (
      <>
        <Select
          label={t('userLabel')}
          options={userOptions}
          value={selected}
          onChange={setSelected}
          isRequired
          errorMessage={failed ? t('signInError') : undefined}
        />
        <Button
          variant="commit"
          size="lg"
          icon={LogIn}
          onPress={submit}
          isDisabled={!selected || busy}
        >
          {t('signIn')}
        </Button>
      </>
    );
  }

  return (
    <div className={cx(styles.auth)}>
      <div className={cx(styles.card)}>
        <div className={cx(styles.brand)}>
          <span className={cx(styles.logo)} aria-hidden="true">
            Α
          </span>
          <span className={cx(styles.product)}>{t('product')}</span>
        </div>
        <div className={cx(styles.heading)}>
          <h1 className={cx(styles.title)}>{t('title')}</h1>
          <p className={cx(styles.intro)}>{t('intro')}</p>
        </div>
        {expired && !session ? (
          <Banner variant="warning" title={t('expiredTitle')}>
            {t('expiredBody')}
          </Banner>
        ) : null}
        {content}
      </div>
      <p className={cx(styles.note)}>{t('note')}</p>
    </div>
  );
}
