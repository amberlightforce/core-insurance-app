import { act, fireEvent, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { clearAnnouncements } from '../../a11y/announce';
import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { FileUpload, middleTruncate, validateFiles, type UploadFile } from './index';

function file(name: string, size = 1024, type = 'application/pdf'): File {
  const f = new File(['x'], name, { type });
  Object.defineProperty(f, 'size', { value: size });
  return f;
}

function fileInput(container: HTMLElement): HTMLInputElement {
  const input = container.querySelector<HTMLInputElement>('input[type="file"]');
  if (!input) throw new Error('file input not found');
  return input;
}

afterEach(() => {
  vi.useRealTimers();
  clearAnnouncements();
});

describe('file validation', () => {
  it('checks type, size, emptiness and the file limit in order', () => {
    const { accepted, rejected } = validateFiles(
      [
        file('a.pdf'),
        file('virus.exe', 10, 'application/x-msdownload'),
        file('big.pdf', 30 * 1024 * 1024),
        file('b.pdf'),
        file('c.pdf'),
        file('z.pdf', 0),
      ],
      { accept: ['.pdf'], maxSize: 25 * 1024 * 1024, maxFiles: 3, existing: 1 },
    );
    expect(accepted.map((f) => f.name)).toEqual(['a.pdf', 'b.pdf']);
    expect(rejected.map((r) => r.code)).toEqual(['type', 'size', 'limit', 'empty']);
    expect(rejected[0]?.extension).toBe('.exe');
  });

  it('truncates long names in the middle and keeps the extension', () => {
    const name = 'Δήλωση_ατυχήματος_οδηγού_με_πολλά_στοιχεία_2026_τελικό.pdf';
    const short = middleTruncate(name, 30);
    expect(short).toHaveLength(30);
    expect(short.endsWith('τελικό.pdf')).toBe(true);
    expect(short).toContain('…');
  });
});

describe('FileUpload', () => {
  it('offers a keyboard path: the choose button opens the file dialog', async () => {
    const onAdd = vi.fn();
    const { user, container } = renderWithDs(
      <FileUpload label="Δικαιολογητικά" files={[]} onAdd={onAdd} accept={['.pdf', '.jpg']} />,
    );
    const button = screen.getByRole('button', { name: 'Σύρετε αρχεία εδώ ή επιλέξτε' });
    expect(button).toHaveAccessibleDescription(/PDF, JPG · έως 25 MB το καθένα/);
    const input = fileInput(container);
    expect(input).toHaveAttribute('accept', '.pdf,.jpg');
    await user.upload(input, [file('Δήλωση.pdf')]);
    expect(onAdd).toHaveBeenCalledWith([expect.objectContaining({ name: 'Δήλωση.pdf' })]);
  });

  it('lists client-side rejections with the exact reason and removes them', async () => {
    const onAdd = vi.fn();
    const onReject = vi.fn();
    const { user, container } = renderWithDs(
      <FileUpload
        label="Δικαιολογητικά"
        files={[]}
        onAdd={onAdd}
        onReject={onReject}
        accept={['.pdf']}
      />,
    );
    const input = fileInput(container);
    fireEvent.change(input, {
      target: { files: [file('setup.exe', 10, 'application/x-msdownload')] },
    });
    expect(onAdd).not.toHaveBeenCalled();
    expect(onReject).toHaveBeenCalledOnce();
    expect(screen.getByText('Μη επιτρεπτός τύπος αρχείου (.exe)')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Αφαίρεση setup.exe' }));
    expect(screen.queryByText('Μη επιτρεπτός τύπος αρχείου (.exe)')).toBeNull();
  });

  it('accepts pasted files (Ctrl+V)', () => {
    const onAdd = vi.fn();
    const { container } = renderWithDs(<FileUpload label="Δ" files={[]} onAdd={onAdd} />);
    const root = container.firstElementChild as HTMLElement;
    fireEvent.paste(root, { clipboardData: { files: [file('scan.pdf')] } });
    expect(onAdd).toHaveBeenCalledWith([expect.objectContaining({ name: 'scan.pdf' })]);
  });

  it('shows every pipeline status with a progressbar while uploading', () => {
    const files: UploadFile[] = [
      { id: '1', name: 'a.pdf', size: 2_516_582, status: 'queued' },
      { id: '2', name: 'b.pdf', size: 1000, status: 'uploading', progress: 62 },
      { id: '3', name: 'c.pdf', size: 1000, status: 'scanning' },
      { id: '4', name: 'd.pdf', size: 1000, status: 'classifying' },
      {
        id: '5',
        name: 'e.pdf',
        size: 1000,
        status: 'done',
        classification: { label: 'Δήλωση ατυχήματος', confidence: 96 },
      },
      {
        id: '6',
        name: 'f.exe',
        size: 1000,
        status: 'rejected',
        rejectReason: 'Μη επιτρεπτός τύπος αρχείου (.exe)',
      },
      { id: '7', name: 'g.pdf', size: 1000, status: 'failed' },
    ];
    const onRetry = vi.fn();
    renderWithDs(
      <FileUpload label="Δ" files={files} onAdd={vi.fn()} onRetry={onRetry} onRemove={vi.fn()} />,
    );
    const list = screen.getByRole('list', { name: 'Δ' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(7);
    expect(screen.getByText('Σε αναμονή')).toBeInTheDocument();
    expect(screen.getByText('2,4 MB')).toBeInTheDocument();
    const bar = screen.getByRole('progressbar', { name: 'Μεταφόρτωση b.pdf' });
    expect(bar).toHaveAttribute('aria-valuenow', '62');
    expect(bar.getAttribute('aria-valuetext')?.replace(/\s/gu, ' ')).toBe('Μεταφόρτωση 62 %');
    expect(screen.getByRole('progressbar', { name: 'Έλεγχος ασφαλείας…' })).not.toHaveAttribute(
      'aria-valuenow',
    );
    expect(screen.getByText('Ταξινόμηση…', { selector: 'span' })).toBeInTheDocument();
    expect(screen.getByText('Έτοιμο')).toBeInTheDocument();
    expect(screen.getByText(/Δήλωση ατυχήματος · 96/)).toBeInTheDocument();
    expect(screen.getByText('Η μεταφόρτωση διακόπηκε')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Επανάληψη' }));
    expect(onRetry).toHaveBeenCalledWith('7');
  });

  it('disables the zone at the file limit with the reason', () => {
    const files: UploadFile[] = [
      { id: '1', name: 'a.pdf', size: 10, status: 'done' },
      { id: '2', name: 'b.pdf', size: 10, status: 'done' },
    ];
    renderWithDs(<FileUpload label="Δ" files={files} onAdd={vi.fn()} maxFiles={2} />);
    const button = screen.getByRole('button', { name: 'Σύρετε αρχεία εδώ ή επιλέξτε' });
    expect(button).toHaveAttribute('aria-disabled', 'true');
    expect(button).toHaveAccessibleDescription('Έχετε φτάσει το όριο των 2 αρχείων');
  });

  it('announces pipeline outcomes as one grouped polite message', () => {
    vi.useFakeTimers();
    const base: UploadFile[] = [
      { id: '1', name: 'a.pdf', size: 10, status: 'uploading', progress: 50 },
      { id: '2', name: 'b.pdf', size: 10, status: 'uploading', progress: 50 },
      { id: '3', name: 'c.pdf', size: 10, status: 'uploading', progress: 50 },
      { id: '4', name: 'd.exe', size: 10, status: 'scanning' },
    ];
    const { rerender } = renderWithDs(<FileUpload label="Δ" files={base} onAdd={vi.fn()} />);
    rerender(
      <FileUpload
        label="Δ"
        files={[
          { ...base[0], status: 'done' } as UploadFile,
          { ...base[1], status: 'done' } as UploadFile,
          { ...base[2], status: 'done' } as UploadFile,
          { ...base[3], status: 'rejected', rejectReason: 'Κακόβουλο λογισμικό' } as UploadFile,
        ]}
        onAdd={vi.fn()}
      />,
    );
    act(() => {
      vi.advanceTimersByTime(700);
    });
    expect(document.getElementById('ds-live-polite')).toHaveTextContent(
      '3 αρχεία έτοιμα, 1 απορρίφθηκε',
    );
  });

  it('renders a read-only list without the zone or remove buttons', () => {
    renderWithDs(
      <FileUpload
        label="Δ"
        files={[{ id: '1', name: 'a.pdf', size: 10, status: 'done' }]}
        onAdd={vi.fn()}
        onRemove={vi.fn()}
        isReadOnly
      />,
    );
    expect(screen.queryByRole('button', { name: /Σύρετε/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Αφαίρεση/ })).toBeNull();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<FileUpload label="Documents" files={[]} onAdd={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Drag files here or choose' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <FileUpload
        label="Δικαιολογητικά"
        files={[
          { id: '1', name: 'a.pdf', size: 10, status: 'uploading', progress: 20 },
          { id: '2', name: 'b.jpg', size: 10, status: 'done' },
        ]}
        onAdd={vi.fn()}
        onRemove={vi.fn()}
        onPreview={vi.fn()}
      />,
    );
    await expectNoA11yViolations(container);
  });
});
