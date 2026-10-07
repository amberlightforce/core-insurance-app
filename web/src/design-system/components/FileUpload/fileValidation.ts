/** Client-side checks for FileUpload (Part 2 §4.30). The server re-validates; these only give fast feedback. */

export const DEFAULT_MAX_SIZE = 25 * 1024 * 1024;
export const DEFAULT_MAX_FILES = 20;

export type RejectionCode = 'type' | 'size' | 'limit' | 'empty';

export interface Rejection {
  file: File;
  code: RejectionCode;
  /** Lower-case extension with the dot, e.g. `.exe` (empty when the name has none). */
  extension: string;
}

export function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(dot).toLowerCase() : '';
}

/** `accept` entries are extensions (`.pdf`), exact MIME types (`application/pdf`) or wildcards (`image/*`). */
export function isAccepted(
  file: Pick<File, 'name' | 'type'>,
  accept: readonly string[] | undefined,
): boolean {
  if (!accept || accept.length === 0) return true;
  const ext = extensionOf(file.name);
  const type = file.type.toLowerCase();
  return accept.some((raw) => {
    const rule = raw.trim().toLowerCase();
    if (rule.startsWith('.')) return ext === rule;
    if (rule.endsWith('/*')) return type.startsWith(rule.slice(0, -1));
    return type === rule;
  });
}

/**
 * Splits picked files into accepted ones and rejections, in order. `existing` counts the files already in the
 * list (excluding rejected ones) towards `maxFiles`.
 */
export function validateFiles(
  files: File[],
  options: { accept?: readonly string[]; maxSize: number; maxFiles: number; existing: number },
): { accepted: File[]; rejected: Rejection[] } {
  const accepted: File[] = [];
  const rejected: Rejection[] = [];
  for (const file of files) {
    const extension = extensionOf(file.name);
    if (!isAccepted(file, options.accept)) {
      rejected.push({ file, code: 'type', extension });
    } else if (file.size === 0) {
      rejected.push({ file, code: 'empty', extension });
    } else if (file.size > options.maxSize) {
      rejected.push({ file, code: 'size', extension });
    } else if (options.existing + accepted.length >= options.maxFiles) {
      rejected.push({ file, code: 'limit', extension });
    } else {
      accepted.push(file);
    }
  }
  return { accepted, rejected };
}

/** «Δήλωση_ατυχήματος_οδηγού_…_2026.pdf»: keeps the start and the end (with the extension) visible. */
export function middleTruncate(name: string, max = 40): string {
  if (name.length <= max) return name;
  const ext = extensionOf(name);
  const keepEnd = Math.min(Math.max(ext.length + 6, 10), Math.floor(max / 2));
  const keepStart = max - keepEnd - 1;
  return `${name.slice(0, keepStart)}…${name.slice(name.length - keepEnd)}`;
}
