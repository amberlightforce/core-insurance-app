import {
  Download,
  Ellipsis,
  ExternalLink,
  FileWarning,
  Lock,
  Maximize,
  Minus,
  MoveHorizontal,
  Plus,
  Printer,
  RotateCw,
  Search,
} from 'lucide-react';
import {
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
  type ReactNode,
} from 'react';
import {
  Button as AriaButton,
  Input,
  Menu,
  MenuItem,
  MenuTrigger,
  Popover,
  SearchField,
  Toolbar,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import { formatBytes } from '../../../format/numbers';
import { Spinner } from '../Spinner';
import { ErrorState } from '../States';
import styles from './DocumentViewer.module.css';
import { clampPage, rotate, zoomIn, zoomOut, type FitMode, type Rotation } from './viewerModel';

export interface DocumentViewState {
  zoom: number;
  rotation: Rotation;
  fit: FitMode;
}

export interface DocumentViewerProps {
  title: string;
  /** Version pill, e.g. «v3». */
  version?: string;
  /** Language pill: «ΕΛ · δεσμευτικό» / «EN · ενημερωτικό». */
  language?: { code: 'el' | 'en'; binding: boolean };
  pageCount: number;
  /** Page images (one URL per page); used when no `renderPage` is given. */
  pageImages?: string[];
  /**
   * Pluggable renderer (pdf.js canvas + text layer, once `pdfjs-dist` is wired in). Receives the 1-based page
   * number and the view state; the viewer handles layout, zoom, rotation and navigation.
   */
  renderPage?: (page: number, view: DocumentViewState) => ReactNode;
  /** Width / height of a page; A4 portrait by default. */
  pageAspect?: number;
  page?: number;
  defaultPage?: number;
  onPageChange?: (page: number) => void;
  state?: 'loading' | 'ready' | 'error' | 'unsupported';
  /** Metadata for the unsupported state and the right panel. */
  file?: { name: string; type?: string; size?: number; hash?: string };
  /** Text preview for unsupported types (e.g. the body of a .msg). */
  textPreview?: string;
  onDownload?: () => void;
  onPrint?: () => void;
  onOpenNewWindow?: () => void;
  /** Search inside the document; the renderer highlights matches. */
  onSearch?: (query: string) => void;
  moreActions?: { id: string; label: string; onAction: () => void }[];
  legalHold?: boolean;
  supersededBy?: { version: string; onOpen: () => void };
  showThumbnails?: boolean;
  rightPanel?: ReactNode;
  rightPanelLabel?: string;
}

const A4 = 1 / Math.SQRT2;

function isTextInput(target: EventTarget): boolean {
  return target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement;
}

/**
 * Document viewer shell (Part 2 §4.31): 40 px toolbar, sunken canvas with page slots, optional thumbnail rail
 * and right panel, keyboard navigation captured inside the viewer (PgUp/PgDn, Home/End, Ctrl/⌘ +/−,
 * Ctrl/⌘+F, R). Renders `<img>` pages or a pluggable renderer.
 */
export function DocumentViewer({
  title,
  version,
  language,
  pageCount,
  pageImages,
  renderPage,
  pageAspect = A4,
  page: controlledPage,
  defaultPage = 1,
  onPageChange,
  state = 'ready',
  file,
  textPreview,
  onDownload,
  onPrint,
  onOpenNewWindow,
  onSearch,
  moreActions,
  legalHold = false,
  supersededBy,
  showThumbnails = false,
  rightPanel,
  rightPanelLabel,
}: DocumentViewerProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const [uncontrolledPage, setUncontrolledPage] = useState(defaultPage);
  const page = clampPage(controlledPage ?? uncontrolledPage, pageCount);
  const [zoom, setZoom] = useState(100);
  const [fit, setFit] = useState<FitMode>('fit-width');
  const [rotation, setRotation] = useState<Rotation>(0);
  const [searchOpen, setSearchOpen] = useState(false);
  const pageRefs = useRef(new Map<number, HTMLElement>());
  const searchRef = useRef<HTMLInputElement>(null);
  const view: DocumentViewState = { zoom, rotation, fit };

  const goTo = (next: number) => {
    const target = clampPage(next, pageCount);
    setUncontrolledPage(target);
    onPageChange?.(target);
    pageRefs.current.get(target)?.scrollIntoView({ block: 'start' });
  };

  const changeZoom = (next: number) => {
    setFit('custom');
    setZoom(next);
  };

  // Track the page in view while scrolling (where IntersectionObserver exists).
  useEffect(() => {
    if (state !== 'ready' || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver(
      (entries) => {
        const visible = entries
          .filter((e) => e.isIntersecting)
          .sort((a, b) => b.intersectionRatio - a.intersectionRatio)[0];
        const n = Number(visible?.target.getAttribute('data-page'));
        if (n > 0) {
          setUncontrolledPage(n);
          onPageChange?.(n);
        }
      },
      { threshold: [0.5] },
    );
    for (const el of pageRefs.current.values()) observer.observe(el);
    return () => {
      observer.disconnect();
    };
  }, [state, pageCount, onPageChange]);

  useEffect(() => {
    if (searchOpen) searchRef.current?.focus();
  }, [searchOpen]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const mod = event.ctrlKey || event.metaKey;
    if (mod && (event.key === '+' || event.key === '=')) {
      event.preventDefault();
      changeZoom(zoomIn(zoom));
      return;
    }
    if (mod && event.key === '-') {
      event.preventDefault();
      changeZoom(zoomOut(zoom));
      return;
    }
    if (mod && event.key.toLowerCase() === 'f' && onSearch) {
      event.preventDefault();
      setSearchOpen(true);
      return;
    }
    if (isTextInput(event.target) || mod || event.altKey) return;
    switch (event.key) {
      case 'PageDown':
        event.preventDefault();
        goTo(page + 1);
        break;
      case 'PageUp':
        event.preventDefault();
        goTo(page - 1);
        break;
      case 'Home':
        event.preventDefault();
        goTo(1);
        break;
      case 'End':
        event.preventDefault();
        goTo(pageCount);
        break;
      case 'r':
      case 'R':
        event.preventDefault();
        setRotation((r) => rotate(r));
        break;
    }
  };

  const languageLabel = !language
    ? null
    : language.code === 'el'
      ? language.binding
        ? t('documentViewer.elBinding')
        : t('documentViewer.elInformative')
      : language.binding
        ? t('documentViewer.enBinding')
        : t('documentViewer.enInformative');

  const pageStyle = {
    '--_aspect': String(rotation % 180 === 0 ? pageAspect : 1 / pageAspect),
    '--_zoom': String(zoom / 100),
  } as CSSProperties;

  const renderSlot = (n: number): ReactNode => {
    if (renderPage) return renderPage(n, view);
    const src = pageImages?.[n - 1];
    if (src) {
      return (
        <img
          className={styles.pageImage}
          src={src}
          alt={t('documentViewer.pageAlt', { page: n, total: pageCount })}
          data-rotation={rotation}
        />
      );
    }
    return null;
  };

  let canvas: ReactNode;
  if (state === 'loading') {
    canvas = (
      <div className={styles.pages} aria-busy="true" data-fit={fit} style={pageStyle}>
        <span className={styles.loadingSpinner}>
          <Spinner size={24} delayed={false} label={t('documentViewer.loading')} />
        </span>
        {Array.from({ length: Math.min(Math.max(pageCount, 1), 3) }, (_, i) => (
          <div key={i} className={styles.page} data-placeholder="true" />
        ))}
      </div>
    );
  } else if (state === 'error') {
    canvas = (
      <div className={styles.message}>
        <ErrorState
          message={t('documentViewer.error')}
          {...(onDownload
            ? {
                actions: (
                  <Button variant="secondary" size="sm" icon={Download} onPress={onDownload}>
                    {t('documentViewer.downloadFile')}
                  </Button>
                ),
              }
            : {})}
        />
      </div>
    );
  } else if (state === 'unsupported') {
    canvas = (
      <div className={styles.message}>
        <div className={styles.unsupported}>
          <span className={styles.unsupportedIcon}>
            <Icon icon={FileWarning} size={24} />
          </span>
          <p className={styles.unsupportedTitle}>{t('documentViewer.unsupported')}</p>
          {file ? (
            <dl className={styles.metadata}>
              <dt>{t('documentViewer.fileName')}</dt>
              <dd>{file.name}</dd>
              {file.type ? (
                <>
                  <dt>{t('documentViewer.fileType')}</dt>
                  <dd>{file.type}</dd>
                </>
              ) : null}
              {file.size !== undefined ? (
                <>
                  <dt>{t('documentViewer.fileSize')}</dt>
                  <dd>{formatBytes(file.size, region)}</dd>
                </>
              ) : null}
              {file.hash ? (
                <>
                  <dt>{t('documentViewer.fileHash')}</dt>
                  <dd className={styles.mono}>{file.hash}</dd>
                </>
              ) : null}
            </dl>
          ) : null}
          {onDownload ? (
            <Button variant="primary" size="sm" icon={Download} onPress={onDownload}>
              {t('documentViewer.downloadFile')}
            </Button>
          ) : null}
          {textPreview ? (
            <pre
              className={styles.textPreview}
              aria-label={t('documentViewer.textPreview')}
              tabIndex={0}
            >
              {textPreview}
            </pre>
          ) : null}
        </div>
      </div>
    );
  } else {
    canvas = (
      <div className={styles.pages} data-fit={fit} style={pageStyle}>
        {Array.from({ length: pageCount }, (_, i) => i + 1).map((n) => (
          <div
            key={n}
            ref={(el) => {
              if (el) pageRefs.current.set(n, el);
              else pageRefs.current.delete(n);
            }}
            className={styles.page}
            data-page={n}
            data-current={n === page || undefined}
            data-rotation={rotation}
          >
            {renderSlot(n)}
          </div>
        ))}
      </div>
    );
  }

  const ready = state === 'ready';

  return (
    <div className={styles.viewer} onKeyDown={onKeyDown}>
      <Toolbar className={cx(styles.toolbar)} aria-label={t('documentViewer.toolbar')}>
        <span className={styles.titleGroup}>
          <span className={styles.title}>{title}</span>
          {version ? <span className={styles.pill}>{version}</span> : null}
          {languageLabel ? (
            <span
              className={styles.pill}
              data-binding={language?.binding === true ? true : undefined}
            >
              {languageLabel}
            </span>
          ) : null}
        </span>
        {ready ? (
          <>
            <span className={styles.pageIndicator}>
              <span aria-hidden="true">
                {t('documentViewer.pageShort', { page, total: pageCount })}
              </span>
              <span className="ds-visually-hidden">
                {t('documentViewer.pageOf', { page, total: pageCount })}
              </span>
            </span>
            <span className={styles.group}>
              <Button
                variant="ghost"
                size="sm"
                icon={Minus}
                label={t('documentViewer.zoomOut')}
                shortcut="Mod+-"
                onPress={() => {
                  changeZoom(zoomOut(zoom));
                }}
              />
              <span className={styles.zoom}>
                {fit === 'custom'
                  ? t('documentViewer.zoomValue', { value: zoom })
                  : fit === 'fit-width'
                    ? t('documentViewer.fitWidthShort')
                    : t('documentViewer.fitPageShort')}
              </span>
              <Button
                variant="ghost"
                size="sm"
                icon={Plus}
                label={t('documentViewer.zoomIn')}
                shortcut="Mod+="
                onPress={() => {
                  changeZoom(zoomIn(zoom));
                }}
              />
              <Button
                variant="ghost"
                size="sm"
                icon={MoveHorizontal}
                label={t('documentViewer.fitWidth')}
                isPressed={fit === 'fit-width'}
                onPress={() => {
                  setFit('fit-width');
                }}
              />
              <Button
                variant="ghost"
                size="sm"
                icon={Maximize}
                label={t('documentViewer.fitPage')}
                isPressed={fit === 'fit-page'}
                onPress={() => {
                  setFit('fit-page');
                }}
              />
              <Button
                variant="ghost"
                size="sm"
                icon={RotateCw}
                label={t('documentViewer.rotate')}
                shortcut="R"
                onPress={() => {
                  setRotation((r) => rotate(r));
                }}
              />
            </span>
          </>
        ) : null}
        <span className={styles.group}>
          {onSearch && ready ? (
            <Button
              variant="ghost"
              size="sm"
              icon={Search}
              label={t('documentViewer.search')}
              shortcut="Mod+F"
              isPressed={searchOpen}
              onPress={() => {
                setSearchOpen((open) => !open);
              }}
            />
          ) : null}
          {onDownload ? (
            <Button
              variant="ghost"
              size="sm"
              icon={Download}
              label={t('documentViewer.download')}
              onPress={onDownload}
            />
          ) : null}
          {onPrint && ready ? (
            <Button
              variant="ghost"
              size="sm"
              icon={Printer}
              label={t('documentViewer.print')}
              onPress={onPrint}
            />
          ) : null}
          {onOpenNewWindow ? (
            <Button
              variant="ghost"
              size="sm"
              icon={ExternalLink}
              label={t('documentViewer.newWindow')}
              onPress={onOpenNewWindow}
            />
          ) : null}
          {moreActions && moreActions.length > 0 ? (
            <MenuTrigger>
              <Button variant="ghost" size="sm" icon={Ellipsis} label={t('documentViewer.more')} />
              <Popover
                className={cx(styles.menuPopover)}
                placement="bottom end"
                data-material="popover"
              >
                <Menu
                  className={cx(styles.menu)}
                  aria-label={t('documentViewer.more')}
                  onAction={(key) => {
                    moreActions.find((a) => a.id === key)?.onAction();
                  }}
                >
                  {moreActions.map((action) => (
                    <MenuItem key={action.id} id={action.id} className={cx(styles.menuItem)}>
                      {action.label}
                    </MenuItem>
                  ))}
                </Menu>
              </Popover>
            </MenuTrigger>
          ) : null}
        </span>
      </Toolbar>

      {searchOpen && onSearch ? (
        <SearchField
          className={cx(styles.search)}
          aria-label={t('documentViewer.searchLabel')}
          onSubmit={onSearch}
          onClear={() => {
            setSearchOpen(false);
          }}
          onKeyDown={(event) => {
            if (event.key === 'Escape') setSearchOpen(false);
          }}
        >
          <Input
            ref={searchRef}
            className={cx(styles.searchInput)}
            placeholder={t('documentViewer.searchPlaceholder')}
          />
        </SearchField>
      ) : null}

      {legalHold ? (
        <div className={styles.banner} data-tone="warning" role="status">
          <Icon icon={Lock} size={14} />
          {t('documentViewer.legalHold')}
        </div>
      ) : null}
      {supersededBy ? (
        <div className={styles.banner} data-tone="info" role="status">
          <span>{t('documentViewer.superseded', { version: supersededBy.version })}</span>
          <Button variant="link" size="sm" onPress={supersededBy.onOpen}>
            {t('documentViewer.open')}
          </Button>
        </div>
      ) : null}

      <div className={styles.body}>
        {showThumbnails && ready ? (
          <nav className={styles.thumbnails} aria-label={t('documentViewer.thumbnails')}>
            <ol className={styles.thumbList}>
              {Array.from({ length: pageCount }, (_, i) => i + 1).map((n) => (
                <li key={n}>
                  <AriaButton
                    className={cx(styles.thumb)}
                    {...(n === page ? { 'aria-current': 'page' as const } : {})}
                    onPress={() => {
                      goTo(n);
                    }}
                  >
                    {pageImages?.[n - 1] ? (
                      <img className={styles.thumbImage} src={pageImages[n - 1]} alt="" />
                    ) : (
                      <span className={styles.thumbBlank} aria-hidden="true" />
                    )}
                    <span className={styles.thumbLabel}>
                      {t('documentViewer.pageNumber', { page: n })}
                    </span>
                  </AriaButton>
                </li>
              ))}
            </ol>
          </nav>
        ) : null}
        <div className={styles.canvas} role="region" aria-label={title} tabIndex={0}>
          {canvas}
        </div>
        {rightPanel ? (
          <aside className={styles.panel} aria-label={rightPanelLabel ?? t('documentViewer.panel')}>
            {rightPanel}
          </aside>
        ) : null}
      </div>
    </div>
  );
}
