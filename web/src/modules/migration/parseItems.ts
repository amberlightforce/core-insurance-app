import type { BatchItemInput } from './api';

/** Parses the pasted items: a JSON array of { module, objectType, legacyId, payload }. */
export function parseItems(text: string): BatchItemInput[] | null {
  try {
    const value: unknown = JSON.parse(text);
    if (!Array.isArray(value) || value.length === 0) return null;
    const ok = value.every((v: unknown) => {
      const o = v as Partial<BatchItemInput> | null;
      return (
        !!o &&
        typeof o.module === 'string' &&
        typeof o.objectType === 'string' &&
        typeof o.legacyId === 'string' &&
        o.payload !== undefined
      );
    });
    return ok ? (value as BatchItemInput[]) : null;
  } catch {
    return null;
  }
}
