import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { downloadPackage } from './download';

describe('downloadPackage', () => {
  const original = { create: URL.createObjectURL, revoke: URL.revokeObjectURL };

  beforeEach(() => {
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
  });

  afterEach(() => {
    URL.createObjectURL = original.create;
    URL.revokeObjectURL = original.revoke;
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
