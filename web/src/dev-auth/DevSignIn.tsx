import {
  ArrowRight,
  Banknote,
  FileText,
  Landmark,
  Layers,
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
  type RefObject,
} from 'react';
import {
  Autocomplete,
  Button as AriaButton,
  Input,
  ListBox,
  ListBoxItem,
  SearchField,
  Text,
  useFilter,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { displayName, humanizeRole, roleKeys } from '../app-shell/user';
import { landingPathFor } from '../auth/roles';
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

/**
 * One highlight pill that glides between persona rows (Framer-style shared hover): it follows the pointer, and
 * the keyboard's virtually focused row when the pointer is elsewhere. Translate only; re-measured on scroll.
 */
function useRowHighlight(wrapRef: RefObject<HTMLDivElement | null>) {
  const target = useRef<HTMLElement | null>(null);
  const hovering = useRef(false);
  const [box, setBox] = useState<{ y: number; h: number } | null>(null);

  const moveTo = useCallback(
    (row: HTMLElement | null) => {
      const wrap = wrapRef.current;
      target.current = row;
      if (!wrap || !row?.isConnected) {
        setBox(null);
        return;
      }
      const outer = wrap.getBoundingClientRect();
      const inner = row.getBoundingClientRect();
      setBox({ y: inner.top - outer.top, h: inner.height });
    },
    [wrapRef],
  );

  const focusedRow = useCallback(
    () => wrapRef.current?.querySelector<HTMLElement>('[role="option"][data-focused]') ?? null,
    [wrapRef],
  );

  const followFocus = useCallback(() => {
    requestAnimationFrame(() => {
      if (!hovering.current) moveTo(focusedRow());
    });
  }, [focusedRow, moveTo]);

  useEffect(() => {
    const wrap = wrapRef.current;
    if (!wrap) return;
    const onScroll = () => {
      moveTo(target.current);
    };
    wrap.addEventListener('scroll', onScroll, { capture: true, passive: true });
    return () => {
      wrap.removeEventListener('scroll', onScroll, { capture: true });
    };
  });

  return {
    visible: box !== null,
    style: box
      ? ({
          transform: `translateY(${String(box.y)}px)`,
          blockSize: `${String(box.h)}px`,
        } as CSSProperties)
      : undefined,
    followFocus,
    onPointerOver: (event: ReactPointerEvent<HTMLDivElement>) => {
      if (event.pointerType === 'touch') return;
      hovering.current = true;
      const row = (event.target as HTMLElement).closest<HTMLElement>('[role="option"]');
      if (row && row !== target.current) moveTo(row);
    },
    onPointerLeave: () => {
      hovering.current = false;
      moveTo(focusedRow());
    },
    onKeyUp: () => {
      hovering.current = false;
      followFocus();
    },
  };
}

const modules: { key: string; icon: LucideIcon }[] = [
  { key: 'policies', icon: FileText },
  { key: 'underwriting', icon: ShieldCheck },
  { key: 'billing', icon: Banknote },
  { key: 'finance', icon: Landmark },
  { key: 'claims', icon: LifeBuoy },
  { key: 'recovery', icon: Undo2 },
  { key: 'reinsurance', icon: Layers },
  { key: 'parties', icon: Users },
];

/** Stagger index for the entrance choreography. */
function enter(index: number): CSSProperties {
  return { '--_i': index } as CSSProperties;
}

/**
 * Development-only sign-in page (D-SLC-03): pick a configured synthetic user, obtain a token from the api's dev
 * scheme. A full-bleed two-panel page outside the shell: a brand showcase (aurora, staggered headline, module
 * marquee) and a searchable persona list with one highlight pill gliding between rows. Clicking a persona (or
 * Enter on the highlighted one) signs in and goes straight to home. Reduced motion stills it all.
 */
export function DevSignIn() {
  const { t } = useTranslation(['dev-auth', 'shell']);
  const navigate = useNavigate();
  const [load, setLoad] = useState<Load>({ kind: 'loading' });
  const [lastUser] = useState<string | null>(() => readLastUser());
  const [session, setSession] = useState<DevSession | null>(() => readSession());
  const [pending, setPending] = useState<string | null>(null);
  const [failed, setFailed] = useState(0);
  const [query, setQuery] = useState('');
  // Sent here by a 401: the previous token is no longer valid.
  const [expired] = useState(() => new URLSearchParams(window.location.search).has('expired'));
  const { contains } = useFilter({ sensitivity: 'base' });
  const page = usePointerVars<HTMLDivElement>();
  const card = usePointerVars<HTMLDivElement>();
  const searchRef = useRef<HTMLInputElement>(null);
  const listWrapRef = useRef<HTMLDivElement>(null);
  const highlight = useRowHighlight(listWrapRef);

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

  const roleName = (role: string) => {
    const key = (roleKeys as Record<string, string | undefined>)[role];
    return key ? t(`shell:${key}`) : humanizeRole(role);
  };

  /** Clicking a persona (or Enter on the highlighted one) signs in and goes straight to home. */
  const signInAs = (userId: string) => {
    if (pending) return;
    setPending(userId);
    setFailed(0);
    signIn(userId)
      .then((session) => {
        rememberUser(userId);
        // A customer lands in the portal, staff on the home screen (BO-CHN-12).
        void navigate(landingPathFor(session.user.roles), { replace: true });
      })
      .catch(() => {
        setPending(null);
        setFailed((count) => count + 1);
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
  } else {
    content = (
      <>
        <div className={cx(styles.picker)} onKeyUp={highlight.onKeyUp}>
          <Autocomplete
            inputValue={query}
            onInputChange={(value) => {
              setQuery(value);
              highlight.followFocus();
            }}
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
            <div
              ref={listWrapRef}
              className={cx(styles.listWrap)}
              data-enter=""
              data-busy={pending ? '' : undefined}
              style={enter(5)}
              onPointerOver={highlight.onPointerOver}
              onPointerLeave={highlight.onPointerLeave}
            >
              <span
                className={cx(styles.highlight)}
                style={highlight.style}
                data-visible={highlight.visible || undefined}
                aria-hidden="true"
              />
              <ListBox
                aria-label={t('userLabel')}
                className={cx(styles.list)}
                items={users}
                onAction={(key) => {
                  signInAs(String(key));
                }}
                renderEmptyState={() => (
                  <p className={cx(styles.noMatch)}>{t('noMatch', { query })}</p>
                )}
              >
                {(user) => {
                  const name = displayName(user.name);
                  const [firstRole, ...more] = user.roles.map(roleName);
                  const isCurrent = session?.user.id === user.id;
                  const isLast = !session && lastUser === user.id;
                  return (
                    <ListBoxItem
                      id={user.id}
                      textValue={`${name} ${user.roles.join(' ')}`}
                      className={cx(styles.option)}
                      data-pending={pending === user.id || undefined}
                    >
                      <Avatar name={name} size="md" decorative />
                      <span className={cx(styles.optionText)}>
                        <Text slot="label" className={cx(styles.optionName)}>
                          <span className={cx(styles.nameText)}>{name}</span>
                          {isCurrent || isLast ? (
                            <span className={cx(styles.tag)}>
                              {isCurrent ? t('current') : t('lastUsed')}
                            </span>
                          ) : null}
                        </Text>
                        <Text slot="description" className={cx(styles.optionRoles)}>
                          <span className={cx(styles.roleText)}>{firstRole}</span>
                          {more.length > 0 ? (
                            <span className={cx(styles.more)}>
                              {t('moreRoles', { count: more.length })}
                            </span>
                          ) : null}
                        </Text>
                      </span>
                      <span className={cx(styles.go)} aria-hidden="true">
                        {pending === user.id ? (
                          <span className={cx(styles.spinner)} />
                        ) : (
                          <Icon icon={ArrowRight} size={16} />
                        )}
                      </span>
                    </ListBoxItem>
                  );
                }}
              </ListBox>
            </div>
          </Autocomplete>
        </div>
        {pending ? (
          <p className={cx(styles.visuallyHidden)} role="status">
            {t('signingIn')}
          </p>
        ) : null}
        {failed > 0 ? (
          <p key={failed} className={cx(styles.error)} role="alert">
            {t('signInError')}
          </p>
        ) : null}
        <div className={cx(styles.footerRow)} data-enter="" style={enter(6)}>
          <p className={cx(styles.hint)}>
            <Kbd shortcut="Enter" /> {t('enterHint')}
          </p>
          {session ? (
            <AriaButton
              className={cx(styles.switch)}
              onPress={() => {
                clearSession();
                setSession(null);
              }}
            >
              {t('signOut')}
            </AriaButton>
          ) : null}
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
              {t('eyebrow')}
            </p>
            <h1 className={cx(styles.title)} data-enter="" style={enter(2)}>
              {t('title')}
            </h1>
            <p className={cx(styles.intro)} data-enter="" style={enter(3)}>
              {t('intro')}
            </p>
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
