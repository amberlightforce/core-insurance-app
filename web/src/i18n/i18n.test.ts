import { act } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { check } from '../../scripts/check-i18n.mjs';
import i18n, {
  changeLanguage,
  languageStorageKey,
  mergeMessages,
  namespaces,
  resources,
} from './index';

describe('i18n', () => {
  it('defaults to Greek and builds a namespace per area', () => {
    expect(i18n.language).toBe('el');
    expect(namespaces).toEqual(expect.arrayContaining(['common', 'shell', 'ds']));
    expect(Object.keys(resources.el).sort()).toEqual(Object.keys(resources.en).sort());
  });

  it('formats ICU plurals in both languages', async () => {
    expect(i18n.t('shell:statusBar.jobs', { count: 1 })).toBe('1 εργασία στο παρασκήνιο');
    expect(i18n.t('shell:statusBar.jobs', { count: 3 })).toBe('3 εργασίες στο παρασκήνιο');
    await act(() => i18n.changeLanguage('en'));
    expect(i18n.t('shell:statusBar.jobs', { count: 1 })).toBe('1 background job');
  });

  it('falls back to Greek, never to the key, when English lacks a string', async () => {
    i18n.addResource('el', 'common', 'onlyGreek', 'Μόνο ελληνικά');
    await act(() => i18n.changeLanguage('en'));
    expect(i18n.t('common:onlyGreek')).toBe('Μόνο ελληνικά');
  });

  it('persists the language and updates <html lang>', async () => {
    await act(() => changeLanguage('en'));
    expect(localStorage.getItem(languageStorageKey)).toBe('en');
    expect(document.documentElement.lang).toBe('en');
    await act(() => changeLanguage('el'));
    expect(document.documentElement.lang).toBe('el');
  });

  it('deep-merges message files', () => {
    expect(mergeMessages({ a: { b: '1' } }, { a: { c: '2' }, d: '3' })).toEqual({
      a: { b: '1', c: '2' },
      d: '3',
    });
  });

  it('passes the key lint (every literal key exists in el and en; ICU is valid)', () => {
    expect(check()).toEqual([]);
  });
});
