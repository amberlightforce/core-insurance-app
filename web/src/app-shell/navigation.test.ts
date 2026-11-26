import { describe, expect, it } from 'vitest';

import { moduleIcons } from '../design-system/icons';
import { adminNavItem, defaultNavItems } from './navigation';

describe('navigation icons', () => {
  it('gives every rail entry its own glyph (a super user sees them all)', () => {
    const items = [...defaultNavItems, adminNavItem];
    const glyphs = items.map((item) => moduleIcons[item.icon ?? item.id]);
    const duplicates = items.filter(
      (item, index) => glyphs.indexOf(moduleIcons[item.icon ?? item.id]) !== index,
    );
    expect(duplicates.map((item) => item.to)).toEqual([]);
  });
});
