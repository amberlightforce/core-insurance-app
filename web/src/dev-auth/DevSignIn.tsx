import {
  ArrowRight,
  Banknote,
  Check,
  FileText,
  Landmark,
  LifeBuoy,
  Search,
  ShieldCheck,
  Undo2,
  Users,
  type LucideIcon,
} from 'lucide-react';
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from 'react';
import {
  Autocomplete,
  Button as AriaButton,
  Input,
  Link,
  ListBox,
  ListBoxItem,
  SearchField,
  SelectionIndicator,
  Text,
  useFilter,
  type Key,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { displayName } from '../app-shell/user';
import { Avatar } from '../design-system/components/Avatar';
import { Banner } from '../design-system/components/Banner';
import { Kbd } from '../design-system/components/Kbd';
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

const lastUserKey = 'coreins.devLastUser';

function readLastUser(): string | null {
  try {
    return localStorage.getItem(lastUserKey);
  } catch {
    return null;
  }
}

function rememberUser(id: string): void {
  try {
    localStorage.setItem(lastUserKey, id);
  } catch {
    // Private mode or blocked storage: the preselection is a convenience only.
  }
}

/** `Staff.UnderwritingManager` → `Underwriting Manager`. */
function roleLabel(role: string): string {
  const name = role.split('.').at(-1) ?? role;
  return name.replace(/([a-z])([A-Z])/gu, '$1 $2');
}

/** Persona name without the «Dev » prefix and «(synthetic)» marker. */
function personaName(name: string): string {
  return displayName(name).replace(/^Dev\s+/u, '');
}

/** Pointer position inside an element as `--_mx/--_my` (px) and `--_nx/--_ny` (−1…1), batched per frame. */
function usePointerVars<T extends HTMLElement>() {
  const frame = useRef(0);
  useEffect(
    () => () => {
      cancelAnimationFrame(frame.current);
    },
    [],
  );
  return useMemo(
    () => ({
      onPointerMove: (event: ReactPointerEvent<T>) => {
        const el = event.currentTarget;
        if (event.pointerType === 'touch') return;
        const { clientX, clientY } = event;
        cancelAnimationFrame(frame.current);
        frame.current = requestAnimationFrame(() => {
          const box = el.getBoundingClientRect();
          const x = clientX - box.left;
          const y = clientY - box.top;
          el.style.setProperty('--_mx', `${x.toFixed(1)}px`);
          el.style.setProperty('--_my', `${y.toFixed(1)}px`);
          el.style.setProperty('--_nx', ((x / box.width) * 2 - 1).toFixed(3));
          el.style.setProperty('--_ny', ((y / box.height) * 2 - 1).toFixed(3));
        });
      },
      onPointerLeave: (event: ReactPointerEvent<T>) => {
        cancelAnimationFrame(frame.current);
        event.currentTarget.style.setProperty('--_nx', '0');
        event.currentTarget.style.setProperty('--_ny', '0');
      },
    }),
    [],
  );
}

const modules: { key: string; icon: LucideIcon }[] = [
  { key: 'policies', icon: FileText },
  { key: 'underwriting', icon: ShieldCheck },
  { key: 'billing', icon: Banknote },
  { key: 'finance', icon: Landmark },
  { key: 'claims', icon: LifeBuoy },
  { key: 'recovery', icon: Undo2 },
  { key: 'reinsurance', icon: ShieldCheck },
  { key: 'parties', icon: Users },
];

/** Stagger index for the entrance choreography. */
function enter(index: number): CSSProperties {
  return { '--_i': index } as CSSProperties;
}

/**
 * Development-only sign-in page (D-SLC-03): pick a configured synthetic user, obtain a token from the api's dev
 * scheme. A full-bleed two-panel page outside the shell: a brand showcase (aurora, staggered headline, module
 * marquee) and a searchable persona list with a sliding selection pill, a magnetic sign-in button and a success
 * state. Enter signs in as the highlighted user; the last-used persona is preselected. Reduced motion stills it all.
 */
export function DevSignIn() {
  const { t } = useTranslation('dev-auth');
  const [load, setLoad] = useState<Load>({ kind: 'loading' });
  const [selected, setSelected] = useState<string | null>(() => readLastUser());
  const [session, setSession] = useState<DevSession | null>(() => readSession());
  const [failed, setFailed] = useState(0);
  const [busy, setBusy] = useState(false);
  const [query, setQuery] = useState('');
  // Sent here by a 401: the previous token is no longer valid.
  const [expired] = useState(() => new URLSearchParams(window.location.search).has('expired'));
  const { contains } = useFilter({ sensitivity: 'base' });
  const page = usePointerVars<HTMLDivElement>();
  const card = usePointerVars<HTMLDivElement>();
  const cta = usePointerVars<HTMLButtonElement>();
  const searchRef = useRef<HTMLInputElement>(null);

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

  // «/» jumps to the search field from anywhere on the page.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const typing = target?.closest('input, textarea, [contenteditable="true"]');
      if (event.key === '/' && !typing && !event.metaKey && !event.ctrlKey) {
        event.preventDefault();
        searchRef.current?.focus();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
    };
  }, []);

  const users = useMemo(() => (load.kind === 'ready' ? load.users : []), [load]);
  const selectedUser = users.find((user) => user.id === selected) ?? null;

  const submit = (userId: string | null = selected) => {
    if (!userId || busy) return;
    setSelected(userId);
    setBusy(true);
    signIn(userId)
      .then((next) => {
        rememberUser(userId);
        setFailed(0);
        setSession(next);
      })
      .catch(() => {
        setFailed((count) => count + 1);
      })
      .finally(() => {
        setBusy(false);
      });
  };

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
    const name = personaName(session.user.name);
    content = (
      <div className={cx(styles.success)}>
        <div className={cx(styles.successMark)} aria-hidden="true">
          <span className={cx(styles.burst)} />
          <span className={cx(styles.burst, styles.burstLate)} />
          <Avatar name={name} size="xl" decorative />
          <span className={cx(styles.badge)}>
            <svg viewBox="0 0 24 24" className={cx(styles.checkSvg)}>
              <path d="M5 12.5l4.5 4.5L19 7.5" />
            </svg>
          </span>
        </div>
        <p role="status" className={cx(styles.status)}>
          <span className={cx(styles.statusLead)}>{t('welcome', { name })}</span>
          <span className={cx(styles.visuallyHidden)}>
            {t('signedInAs', { name: session.user.name, roles: session.user.roles.join(', ') })}
          </span>
        </p>
        <ul className={cx(styles.roleList)} aria-label={t('rolesLabel')}>
          {session.user.roles.map((role, index) => (
            <li key={role} className={cx(styles.role)} style={enter(index)}>
              {roleLabel(role)}
            </li>
          ))}
        </ul>
        <Link href="/" className={cx(styles.cta)} autoFocus>
          <span className={cx(styles.ctaShine)} aria-hidden="true" />
          <span className={cx(styles.ctaLabel)}>{t('continue')}</span>
          <Icon icon={ArrowRight} size={16} className={cx(styles.ctaArrow)} />
        </Link>
        <AriaButton
          className={cx(styles.switch)}
          onPress={() => {
            clearSession();
            setSession(null);
          }}
        >
          {t('signOut')}
        </AriaButton>
      </div>
    );
  } else {
    const ctaLabel = selectedUser
      ? t('signInAs', { name: personaName(selectedUser.name) })
      : t('signIn');
    content = (
      <>
        <Autocomplete
          inputValue={query}
          onInputChange={setQuery}
          filter={(text) => contains(text, query)}
        >
          <SearchField
            className={cx(styles.search)}
            aria-label={t('searchLabel')}
            data-enter=""
            style={enter(4)}
          >
            <Icon icon={Search} size={16} className={cx(styles.searchIcon)} />
            <Input ref={searchRef} className={cx(styles.searchInput)} placeholder={t('search')} />
            <span className={cx(styles.searchKbd)} aria-hidden="true">
              <Kbd shortcut="/" />
            </span>
          </SearchField>
          <div className={cx(styles.listWrap)} data-enter="" style={enter(5)}>
            <ListBox
              aria-label={t('userLabel')}
              className={cx(styles.list)}
              items={users}
              selectionMode="single"
              selectionBehavior="replace"
              disallowEmptySelection
              selectedKeys={selected ? [selected] : []}
              onSelectionChange={(keys) => {
                if (keys === 'all') return;
                const [first] = [...keys] as Key[];
                setFailed(0);
                setSelected(first === undefined ? null : String(first));
              }}
              onAction={(key) => {
                submit(String(key));
              }}
              renderEmptyState={() => (
                <p className={cx(styles.noMatch)}>{t('noMatch', { query })}</p>
              )}
            >
              {(user) => {
                const name = personaName(user.name);
                const [firstRole, ...more] = user.roles.map(roleLabel);
                return (
                  <ListBoxItem
                    id={user.id}
                    textValue={`${name} ${user.roles.join(' ')}`}
                    className={cx(styles.option)}
                  >
                    <SelectionIndicator className={cx(styles.pill)} />
                    <Avatar name={name} size="md" decorative />
                    <span className={cx(styles.optionText)}>
                      <Text slot="label" className={cx(styles.optionName)}>
                        {name}
                      </Text>
                      <Text slot="description" className={cx(styles.optionRoles)}>
                        {firstRole}
                        {more.length > 0 ? (
                          <span className={cx(styles.more)}>
                            {t('moreRoles', { count: more.length })}
                          </span>
                        ) : null}
                      </Text>
                    </span>
                    <span className={cx(styles.tick)} aria-hidden="true">
                      <Icon icon={Check} size={14} />
                    </span>
                  </ListBoxItem>
                );
              }}
            </ListBox>
          </div>
        </Autocomplete>
        {failed > 0 ? (
          <p key={failed} className={cx(styles.error)} role="alert">
            {t('signInError')}
          </p>
        ) : null}
        <div className={cx(styles.ctaRow)} data-enter="" style={enter(6)}>
          <AriaButton
            className={cx(styles.cta)}
            onPointerMove={cta.onPointerMove}
            onPointerLeave={cta.onPointerLeave}
            onPress={() => {
              submit();
            }}
            isDisabled={!selected}
            isPending={busy}
            aria-label={busy ? t('signingIn') : ctaLabel}
          >
            <span className={cx(styles.ctaShine)} aria-hidden="true" />
            {busy ? <span className={cx(styles.ctaSpinner)} aria-hidden="true" /> : null}
            <span key={ctaLabel} className={cx(styles.ctaLabel)}>
              {busy ? t('signingIn') : ctaLabel}
            </span>
            <Icon icon={ArrowRight} size={16} className={cx(styles.ctaArrow)} />
          </AriaButton>
          <p className={cx(styles.hint)}>
            <Kbd shortcut="Enter" /> {t('enterHint')}
          </p>
        </div>
      </>
    );
  }

  return (
    <div
      className={cx(styles.page)}
      onPointerMove={page.onPointerMove}
      onPointerLeave={page.onPointerLeave}
    >
      <section className={cx(styles.showcase)} aria-labelledby="dev-sign-in-pitch">
        <div className={cx(styles.aurora)} aria-hidden="true">
          <span className={cx(styles.blob, styles.blobA)} />
          <span className={cx(styles.blob, styles.blobB)} />
          <span className={cx(styles.blob, styles.blobC)} />
          <span className={cx(styles.grid)} />
        </div>
        <div className={cx(styles.brand)} data-enter="" style={enter(0)}>
          <span className={cx(styles.logo)} aria-hidden="true">
            Α
          </span>
          <span className={cx(styles.product)}>{t('product')}</span>
        </div>
        <div className={cx(styles.pitch)}>
          <span className={cx(styles.env)} data-enter="" style={enter(1)}>
            <span className={cx(styles.envDot)} aria-hidden="true" />
            {t('env')}
          </span>
          <h2 id="dev-sign-in-pitch" className={cx(styles.headline)}>
            {t('headline')
              .split(' ')
              .map((word, index) => (
                <span
                  key={`${word}-${String(index)}`}
                  className={cx(styles.word)}
                  style={enter(index + 2)}
                >
                  {word}{' '}
                </span>
              ))}
            <span className={cx(styles.word, styles.accent)} style={enter(12)}>
              {t('headlineAccent')}
            </span>
          </h2>
          <p className={cx(styles.lede)} data-enter="" style={enter(6)}>
            {t('lede')}
          </p>
        </div>
        <div className={cx(styles.marquee)} data-enter="" style={enter(8)}>
          <ul className={cx(styles.track)} aria-label={t('modulesLabel')}>
            {[...modules, ...modules].map((module, index) => (
              <li
                key={`${module.key}-${String(index)}`}
                className={cx(styles.chip)}
                aria-hidden={index >= modules.length || undefined}
              >
                <Icon icon={module.icon} size={14} />
                {t(`modules.${module.key}`)}
              </li>
            ))}
          </ul>
        </div>
      </section>

      <main className={cx(styles.panel)}>
        <div
          className={cx(styles.card)}
          data-shake={failed > 0 ? failed % 2 : undefined}
          onPointerMove={card.onPointerMove}
          onPointerLeave={card.onPointerLeave}
        >
          <span className={cx(styles.spotlight)} aria-hidden="true" />
          <div className={cx(styles.cardBrand)}>
            <span className={cx(styles.logo)} aria-hidden="true">
              Α
            </span>
            <span className={cx(styles.productDark)}>{t('product')}</span>
          </div>
          <div className={cx(styles.heading)}>
            <p className={cx(styles.eyebrow)} data-enter="" style={enter(1)}>
              {session ? t('eyebrowDone') : t('eyebrow')}
            </p>
            <h1 className={cx(styles.title)} data-enter="" style={enter(2)}>
              {t('title')}
            </h1>
            {session ? null : (
              <p className={cx(styles.intro)} data-enter="" style={enter(3)}>
                {t('intro')}
              </p>
            )}
          </div>
          {expired && !session ? (
            <Banner variant="warning" title={t('expiredTitle')}>
              {t('expiredBody')}
            </Banner>
          ) : null}
          {content}
        </div>
        <p className={cx(styles.note)} data-enter="" style={enter(8)}>
          {t('note')}
        </p>
      </main>
    </div>
  );
}
