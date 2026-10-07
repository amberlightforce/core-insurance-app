import { announce } from '../../a11y/announce';
import { durations } from '../../tokens';

export type ToastFamily = 'success' | 'info' | 'warning' | 'error' | 'progress';

export interface ToastAction {
  /** Short verb, e.g. «Αναίρεση» or «Προβολή». */
  label: string;
  onAction: () => void;
}

export interface ToastOptions {
  title: string;
  /** Caption, clamped to two lines. Batch summaries («Εκδόθηκαν 48 ασφαλιστήρια · 2 απέτυχαν») go here. */
  description?: string;
  action?: ToastAction;
  /** Overrides the family duration in ms; `null` persists until dismissed. */
  duration?: number | null;
}

export interface ToastRecord {
  id: string;
  family: ToastFamily;
  title: string;
  description?: string;
  action?: ToastAction;
  /** Auto-dismiss after this many ms of visible, unpaused time; `null` persists. */
  duration: number | null;
  /** Progress toasts: 0–1, or `null` while indeterminate. */
  progress?: number | null;
  /** Bumped when a toast changes family so its timer and hairline restart. */
  revision: number;
}

/**
 * Durations (Part 2 §4.21): success 4 s, any toast with an action 6 s, info 5 s, warning 8 s; errors
 * and running progress toasts persist.
 */
export function durationFor(family: ToastFamily, hasAction: boolean): number | null {
  if (family === 'error' || family === 'progress') return null;
  if (hasAction) return durations.toastWithAction;
  if (family === 'success') return durations.toastSuccess;
  if (family === 'info') return durations.toastInfo;
  return durations.toastWarning;
}

type Listener = () => void;

let toasts: ToastRecord[] = [];
const listeners = new Set<Listener>();
let counter = 0;

function emit(): void {
  for (const listener of listeners) listener();
}

function message(record: Pick<ToastRecord, 'title' | 'description'>): string {
  return record.description ? `${record.title}. ${record.description}` : record.title;
}

/** Success, info and progress toasts are announced politely; warning and error toasts are `role=alert`. */
function announceRecord(record: ToastRecord): void {
  if (record.family === 'success' || record.family === 'info' || record.family === 'progress') {
    announce(message(record), 'polite');
  }
}

function add(family: ToastFamily, options: ToastOptions): string {
  counter += 1;
  const id = `toast-${String(counter)}`;
  const record: ToastRecord = {
    id,
    family,
    title: options.title,
    duration:
      options.duration !== undefined
        ? options.duration
        : durationFor(family, Boolean(options.action)),
    revision: 0,
    ...(options.description ? { description: options.description } : {}),
    ...(options.action ? { action: options.action } : {}),
  };
  toasts = [...toasts, record];
  announceRecord(record);
  emit();
  return id;
}

function update(id: string, patch: Partial<Omit<ToastRecord, 'id'>>): void {
  toasts = toasts.map((record) => (record.id === id ? { ...record, ...patch } : record));
  emit();
}

function dismiss(id: string): void {
  toasts = toasts.filter((record) => record.id !== id);
  emit();
}

export interface ProgressToastOptions {
  title: string;
  description?: string;
  /** 0–1; omit for indeterminate. */
  value?: number;
}

export interface ProgressToastHandle {
  id: string;
  update: (patch: { value?: number | null; description?: string; title?: string }) => void;
  /** Resolves the job into a success toast (normal success timing). */
  success: (options: ToastOptions) => void;
  /** Resolves the job into a persistent error toast. */
  error: (options: ToastOptions) => void;
  dismiss: () => void;
}

function resolveProgress(id: string, family: 'success' | 'error', options: ToastOptions): void {
  const current = toasts.find((record) => record.id === id);
  if (!current) {
    add(family, options);
    return;
  }
  const next: ToastRecord = {
    id,
    family,
    title: options.title,
    duration:
      options.duration !== undefined
        ? options.duration
        : durationFor(family, Boolean(options.action)),
    revision: current.revision + 1,
    ...(options.description ? { description: options.description } : {}),
    ...(options.action ? { action: options.action } : {}),
  };
  toasts = toasts.map((record) => (record.id === id ? next : record));
  announceRecord(next);
  emit();
}

/**
 * Toast queue API (Part 2 §4.21). Render one `<Toaster/>` in the shell; call these anywhere.
 * Toasts confirm what the user did (with undo); they are not for errors that need action.
 */
export const toast = {
  success: (options: ToastOptions) => add('success', options),
  info: (options: ToastOptions) => add('info', options),
  warning: (options: ToastOptions) => add('warning', options),
  error: (options: ToastOptions) => add('error', options),
  /** Background job with a `gradient.tide` bar that later resolves to success or error. */
  progress: (options: ProgressToastOptions): ProgressToastHandle => {
    const id = add('progress', {
      title: options.title,
      ...(options.description ? { description: options.description } : {}),
    });
    update(id, { progress: options.value ?? null });
    return {
      id,
      update: (patch) => {
        update(id, {
          ...(patch.value !== undefined ? { progress: patch.value } : {}),
          ...(patch.description !== undefined ? { description: patch.description } : {}),
          ...(patch.title !== undefined ? { title: patch.title } : {}),
        });
      },
      success: (resolved) => {
        resolveProgress(id, 'success', resolved);
      },
      error: (resolved) => {
        resolveProgress(id, 'error', resolved);
      },
      dismiss: () => {
        dismiss(id);
      },
    };
  },
  dismiss,
  /** Removes every toast (tests, sign-out). */
  clear: () => {
    toasts = [];
    emit();
  },
};

export const toastStore = {
  subscribe: (listener: Listener): (() => void) => {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
  getSnapshot: (): ToastRecord[] => toasts,
};

/** Visible toasts at once; later ones queue until a slot frees (Part 2 §4.21). */
export const maxVisibleToasts = 3;
