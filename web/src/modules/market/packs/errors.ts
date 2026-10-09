import type { TFunction } from 'i18next';

import { problemOf } from '../../staff/problem';

/**
 * A plain-language message for a failed pack call: the translated text of the problem code when known (the
 * separation-of-duties refusals included), else the server's title, else a generic line. The server is always
 * the authority; this only explains its answer.
 */
export function packErrorText(t: TFunction, error: unknown): string {
  const problem = problemOf(error);
  const code = problem.code;
  if (code && /^[A-Z0-9-]+$/.test(code)) {
    const key = `market:packs.errors.${code}`;
    const text = t(key, { defaultValue: '' });
    if (text) return text;
  }
  if (problem.status === 403) return t('market:packs.errors.forbidden');
  if (problem.status === 0) return t('market:packs.errors.network');
  return problem.title ?? t('market:packs.errors.generic');
}
