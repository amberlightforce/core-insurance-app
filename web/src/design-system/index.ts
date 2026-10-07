/**
 * Aegean design system: the public surface for feature code ("consume, don't restyle"; see README.md).
 * Import `./styles.css` once at the app root and wrap the app in `DesignSystemProvider`.
 */
export { DesignSystemProvider } from './DesignSystemProvider';
export type { DesignSystemProviderProps } from './DesignSystemProvider';
export { ariaLocaleFor } from './locale';
export * from './preferences';
export * from './icons';
export * from './tokens';
export { announce, clearAnnouncements } from './a11y/announce';
export type { Politeness } from './a11y/announce';
export { cx, defined } from './utils/cx';

export * from './components/AiSuggestion';
export * from './components/AuthorityMeter';
export * from './components/Avatar';
export * from './components/Banner';
export * from './components/Breadcrumbs';
export * from './components/Button';
export * from './components/Card';
export * from './components/Checkbox';
export * from './components/Combobox';
export * from './components/CommandPalette';
export * from './components/Comments';
export * from './components/CurrencyField';
export * from './components/DataTable';
export * from './components/DatePicker';
export * from './components/Dialog';
export * from './components/DocumentViewer';
export * from './components/Drawer';
export * from './components/ErrorSummary';
export * from './components/FileUpload';
export * from './components/IdentifierField';
export * from './components/Kbd';
export * from './components/KeyValueList';
export * from './components/KpiTile';
export * from './components/Menu';
export * from './components/MultiCombobox';
export * from './components/Notifications';
export * from './components/Pagination';
export * from './components/PercentField';
export * from './components/Popover';
export * from './components/Progress';
export * from './components/RadioGroup';
export * from './components/SegmentedControl';
export * from './components/Select';
export * from './components/ShortcutOverlay';
export * from './components/SideSheet';
export * from './components/Skeleton';
export * from './components/Spinner';
export * from './components/StatementView';
export * from './components/States';
export * from './components/StatusPill';
export * from './components/Stepper';
export * from './components/Switch';
export * from './components/Tabs';
export * from './components/Tags';
export * from './components/TextField';
export * from './components/Timeline';
export * from './components/Toast';
export * from './components/Tooltip';

export * from './patterns/ApprovalPanel';
export * from './patterns/FormLayout';
export * from './patterns/Wizard';
export * from './patterns/Workbench';
