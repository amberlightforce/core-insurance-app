import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { DocumentViewer, zoomIn, zoomOut } from './index';

const images = ['/p1.png', '/p2.png', '/p3.png'];

function normalized(text: string | null): string {
  return (text ?? '').replace(/\s/gu, ' ');
}

describe('viewer model', () => {
  it('steps zoom within 25–400 %', () => {
    expect(zoomIn(100)).toBe(125);
    expect(zoomOut(100)).toBe(75);
    expect(zoomIn(400)).toBe(400);
    expect(zoomOut(25)).toBe(25);
  });
});

describe('DocumentViewer', () => {
  it('renders the toolbar with title, version and language pills and image pages', () => {
    renderWithDs(
      <DocumentViewer
        title="Ασφαλιστήριο ΑΣΦ-2026-0412"
        version="v3"
        language={{ code: 'el', binding: true }}
        pageCount={3}
        pageImages={images}
      />,
    );
    const toolbar = screen.getByRole('toolbar', { name: 'Εργαλεία εγγράφου' });
    expect(toolbar).toHaveTextContent('Ασφαλιστήριο ΑΣΦ-2026-0412');
    expect(toolbar).toHaveTextContent('v3');
    expect(toolbar).toHaveTextContent('ΕΛ · δεσμευτικό');
    expect(toolbar).toHaveTextContent('Σελίδα 1 από 3');
    expect(screen.getAllByRole('img', { name: /Σελίδα \d από 3/ })).toHaveLength(3);
  });

  it('moves between pages with PageDown/PageUp and Home/End inside the canvas', async () => {
    const onPageChange = vi.fn();
    const { user } = renderWithDs(
      <DocumentViewer
        title="Έγγραφο"
        pageCount={3}
        pageImages={images}
        onPageChange={onPageChange}
      />,
    );
    const canvas = screen.getByRole('region', { name: 'Έγγραφο' });
    canvas.focus();
    await user.keyboard('{PageDown}');
    expect(onPageChange).toHaveBeenLastCalledWith(2);
    expect(screen.getByRole('toolbar')).toHaveTextContent('Σελίδα 2 από 3');
    await user.keyboard('{End}');
    expect(onPageChange).toHaveBeenLastCalledWith(3);
    await user.keyboard('{PageDown}');
    expect(onPageChange).toHaveBeenLastCalledWith(3);
    await user.keyboard('{Home}');
    expect(onPageChange).toHaveBeenLastCalledWith(1);
    await user.keyboard('{PageUp}');
    expect(onPageChange).toHaveBeenLastCalledWith(1);
  });

  it('zooms with Ctrl +/− and the toolbar, and rotates with R', async () => {
    const { user, container } = renderWithDs(
      <DocumentViewer title="Έγγραφο" pageCount={1} pageImages={images} />,
    );
    const toolbar = screen.getByRole('toolbar');
    expect(within(toolbar).getByRole('button', { name: 'Προσαρμογή στο πλάτος' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    screen.getByRole('region', { name: 'Έγγραφο' }).focus();
    await user.keyboard('{Control>}={/Control}');
    expect(normalized(toolbar.textContent)).toContain('125 %');
    await user.keyboard('{Control>}-{/Control}{Control>}-{/Control}');
    expect(normalized(toolbar.textContent)).toContain('75 %');
    await user.click(within(toolbar).getByRole('button', { name: 'Μεγέθυνση' }));
    expect(normalized(toolbar.textContent)).toContain('100 %');
    await user.click(within(toolbar).getByRole('button', { name: 'Προσαρμογή στη σελίδα' }));
    expect(container.querySelector('[data-fit="fit-page"]')).not.toBeNull();
    screen.getByRole('region', { name: 'Έγγραφο' }).focus();
    await user.keyboard('r');
    expect(screen.getByRole('img')).toHaveAttribute('data-rotation', '90');
  });

  it('is a toolbar with arrow-key navigation', async () => {
    const { user } = renderWithDs(
      <DocumentViewer
        title="Έγγραφο"
        pageCount={1}
        pageImages={images}
        onDownload={vi.fn()}
        onPrint={vi.fn()}
      />,
    );
    await user.tab();
    expect(screen.getByRole('button', { name: 'Σμίκρυνση' })).toHaveFocus();
    await user.keyboard('{ArrowRight}');
    expect(screen.getByRole('button', { name: 'Μεγέθυνση' })).toHaveFocus();
  });

  it('opens search with Ctrl+F and submits the query', async () => {
    const onSearch = vi.fn();
    const { user } = renderWithDs(
      <DocumentViewer title="Έγγραφο" pageCount={1} pageImages={images} onSearch={onSearch} />,
    );
    screen.getByRole('region', { name: 'Έγγραφο' }).focus();
    await user.keyboard('{Control>}f{/Control}');
    const field = await screen.findByRole('searchbox', { name: 'Αναζήτηση στο έγγραφο' });
    expect(field).toHaveFocus();
    await user.type(field, 'απαλλαγή{Enter}');
    expect(onSearch).toHaveBeenCalledWith('απαλλαγή');
  });

  it('runs extra actions from the ⋯ menu', async () => {
    const onAction = vi.fn();
    const { user } = renderWithDs(
      <DocumentViewer
        title="Έγγραφο"
        pageCount={1}
        pageImages={images}
        moreActions={[{ id: 'meta', label: 'Μεταδεδομένα', onAction }]}
      />,
    );
    const more = screen.getByRole('button', { name: 'Περισσότερα' });
    await user.click(more);
    await user.click(await screen.findByRole('menuitem', { name: 'Μεταδεδομένα' }));
    expect(onAction).toHaveBeenCalledOnce();
  });

  it('shows the legal hold and superseded banners', async () => {
    const onOpen = vi.fn();
    const { user } = renderWithDs(
      <DocumentViewer
        title="Έγγραφο"
        pageCount={1}
        pageImages={images}
        legalHold
        supersededBy={{ version: '3', onOpen }}
      />,
    );
    expect(screen.getByText('Υπό δικαστική δέσμευση · δεν διαγράφεται')).toBeInTheDocument();
    expect(screen.getByText('Αντικαταστάθηκε από την έκδοση 3')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Άνοιγμα' }));
    expect(onOpen).toHaveBeenCalledOnce();
  });

  it('renders loading, error and unsupported states', async () => {
    const onDownload = vi.fn();
    const { rerender, user, container } = renderWithDs(
      <DocumentViewer title="Έγγραφο" pageCount={2} state="loading" />,
    );
    expect(container.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(screen.getByRole('img', { name: 'Φόρτωση εγγράφου…' })).toBeInTheDocument();
    rerender(
      <DocumentViewer title="Έγγραφο" pageCount={2} state="error" onDownload={onDownload} />,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('Δεν ήταν δυνατή η προβολή του εγγράφου.');
    await user.click(screen.getByRole('button', { name: 'Λήψη αρχείου' }));
    expect(onDownload).toHaveBeenCalledOnce();
    rerender(
      <DocumentViewer
        title="email.msg"
        pageCount={0}
        state="unsupported"
        file={{
          name: 'email.msg',
          type: 'application/vnd.ms-outlook',
          size: 2048,
          hash: 'sha256:ab12',
        }}
        textPreview="Καλημέρα σας"
        onDownload={onDownload}
      />,
    );
    expect(screen.getByText('email.msg', { selector: 'dd' })).toBeInTheDocument();
    expect(screen.getByText('sha256:ab12')).toBeInTheDocument();
    expect(screen.getByText('Καλημέρα σας')).toBeInTheDocument();
  });

  it('uses a pluggable renderer when given', () => {
    renderWithDs(
      <DocumentViewer
        title="Έγγραφο"
        pageCount={2}
        renderPage={(n, view) => <span>{`σελ. ${String(n)} @ ${String(view.zoom)}`}</span>}
      />,
    );
    expect(screen.getByText('σελ. 2 @ 100')).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <DocumentViewer
        title="Policy"
        pageCount={2}
        pageImages={images}
        language={{ code: 'en', binding: false }}
      />,
    );
    expect(screen.getByRole('toolbar', { name: 'Document tools' })).toHaveTextContent(
      'EN · informative',
    );
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <DocumentViewer
        title="Έγγραφο"
        version="v2"
        pageCount={2}
        pageImages={images}
        showThumbnails
        rightPanel={<p>Αρχειοθέτηση: 10 έτη</p>}
        onDownload={vi.fn()}
        onSearch={vi.fn()}
        legalHold
      />,
    );
    await expectNoA11yViolations(container);
  });
});
