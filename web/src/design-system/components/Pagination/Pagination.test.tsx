import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { pageRange, pageSlots } from './pages';
import { Pagination } from './Pagination';

describe('page helpers', () => {
  it('builds page slots with gaps', () => {
    const labels = pageSlots(5, 97).map((s) => (s.kind === 'gap' ? '…' : String(s.page)));
    expect(labels).toEqual(['1', '…', '4', '5', '6', '…', '97']);
    expect(pageSlots(1, 3)).toHaveLength(3);
  });

  it('computes the visible range', () => {
    expect(pageRange(1, 50, 4812)).toEqual([1, 50]);
    expect(pageRange(97, 50, 4812)).toEqual([4801, 4812]);
    expect(pageRange(1, 50, 0)).toEqual([0, 0]);
  });
});

describe('Pagination', () => {
  it('is a labelled nav with the el-GR range and the current page', () => {
    renderWithDs(<Pagination page={1} pageSize={50} total={4812} onPageChange={vi.fn()} />);
    const nav = screen.getByRole('navigation', { name: 'Σελιδοποίηση' });
    expect(nav).toHaveTextContent('1–50 από 4.812');
    expect(within(nav).getByRole('button', { name: 'Σελίδα 1' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(within(nav).getByRole('button', { name: 'Προηγούμενη σελίδα' })).toBeDisabled();
  });

  it('moves between pages by click and keyboard', async () => {
    const onPageChange = vi.fn();
    const { user } = renderWithDs(
      <Pagination page={2} pageSize={50} total={4812} onPageChange={onPageChange} />,
    );
    await user.click(screen.getByRole('button', { name: 'Επόμενη σελίδα' }));
    expect(onPageChange).toHaveBeenLastCalledWith(3);
    await user.click(screen.getByRole('button', { name: 'Σελίδα 97' }));
    expect(onPageChange).toHaveBeenLastCalledWith(97);

    screen.getByRole('button', { name: 'Σελίδα 2' }).focus();
    await user.keyboard('{ArrowRight}');
    expect(screen.getByRole('button', { name: 'Σελίδα 3' })).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(onPageChange).toHaveBeenLastCalledWith(3);
    await user.keyboard('{ArrowLeft}{ArrowLeft}');
    expect(screen.getByRole('button', { name: 'Σελίδα 1' })).toHaveFocus();
  });

  it('changes the page size', async () => {
    const onPageSizeChange = vi.fn();
    const { user } = renderWithDs(
      <Pagination
        page={1}
        pageSize={50}
        total={4812}
        onPageChange={vi.fn()}
        onPageSizeChange={onPageSizeChange}
      />,
    );
    const trigger = screen.getByRole('button', { name: /Εγγραφές ανά σελίδα/ });
    expect(trigger).toHaveTextContent('50');
    await user.click(trigger);
    await user.click(screen.getByRole('option', { name: '100' }));
    expect(onPageSizeChange).toHaveBeenCalledWith(100);
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Pagination page={3} pageSize={25} total={60} onPageChange={vi.fn()} />);
    expect(screen.getByRole('navigation', { name: 'Pagination' })).toHaveTextContent('51–60 of 60');
    expect(screen.getByRole('button', { name: 'Next page' })).toBeDisabled();
  });

  it('has no axe violations', async () => {
    renderWithDs(
      <Pagination
        page={5}
        pageSize={50}
        total={4812}
        onPageChange={vi.fn()}
        onPageSizeChange={vi.fn()}
      />,
    );
    await expectNoA11yViolations();
  });
});
