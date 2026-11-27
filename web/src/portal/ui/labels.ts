import { useTranslation } from 'react-i18next';

/** `IN_FORCE`, `InForce` and `in-force` all become `inforce`: the server mixes enum spellings, the strings key on this. */
export function norm(value: string | null | undefined): string {
  return (value ?? '').replace(/[^A-Za-z0-9]/g, '').toLowerCase();
}

/**
 * Translates a server code (policy status, coverage, product, invoice state, document type) with the code itself as
 * the fallback, so a code the portal has no wording for yet is shown as is rather than hidden or invented.
 */
export function useCodeLabel() {
  const { t } = useTranslation('portal');
  return (group: string, code: string | null | undefined): string => {
    if (!code) return '—';
    return t(`codes.${group}.${norm(code)}`, { defaultValue: code });
  };
}
