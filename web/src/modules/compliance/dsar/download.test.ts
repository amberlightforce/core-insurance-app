import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { downloadPackage } from './download';

describe('downloadPackage', () => {
  // jsdom has no Blob URLs, so the originals may be absent; restore exactly what was there.
  const names = ['createObjectURL', 'revokeObjectURL'] as const;
  const original = new Map<string, PropertyDescriptor | undefined>(
    names.map((name) => [name, Object.getOwnPropertyDescriptor(URL, name)]),
  );

  beforeEach(() => {
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
  });

  afterEach(() => {
    for (const name of names) {
      const descriptor = original.get(name);
      if (descriptor) Object.defineProperty(URL, name, descriptor);
      else Reflect.deleteProperty(URL, name);
    }
    vi.restoreAllMocks();
  });

  it('uses a Blob URL when available', () => {
    const create = vi.fn(() => 'blob:x');
    const revoke = vi.fn();
    URL.createObjectURL = create;
    URL.revokeObjectURL = revoke;
    expect(downloadPackage('d1', null)).toBe(true);
    expect(create).toHaveBeenCalled();
    expect(revoke).toHaveBeenCalledWith('blob:x');
  });

  it('falls back to a data URL when Blob URLs are unavailable', () => {
    URL.createObjectURL = () => {
      throw new Error('no blob');
    };
    let href = '';
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      href = this.href;
    });
    expect(downloadPackage('d1', null)).toBe(true);
    expect(href.startsWith('data:application/json')).toBe(true);
  });
});
