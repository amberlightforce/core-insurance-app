import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import { FileUpload, type UploadFile } from './index';

const pipeline: UploadFile[] = [
  { id: '1', name: 'Δήλωση_ατυχήματος.pdf', size: 1_240_000, status: 'queued' },
  {
    id: '2',
    name: 'Φωτογραφία_ζημίας_εμπρός_αριστερά_2026-10-07.jpg',
    size: 3_400_000,
    status: 'uploading',
    progress: 62,
  },
  { id: '3', name: 'Άδεια_κυκλοφορίας.pdf', size: 520_000, status: 'scanning' },
  { id: '4', name: 'Πραγματογνωμοσύνη.docx', size: 210_000, status: 'classifying' },
  {
    id: '5',
    name: 'Δήλωση_οδηγού.pdf',
    size: 880_000,
    status: 'done',
    classification: { label: 'Δήλωση ατυχήματος', confidence: 96 },
  },
  {
    id: '6',
    name: 'setup.exe',
    size: 12_000,
    status: 'rejected',
    rejectReason: 'Μη επιτρεπτός τύπος αρχείου (.exe)',
  },
  { id: '7', name: 'Τιμολόγιο_συνεργείου.pdf', size: 640_000, status: 'failed' },
];

const meta = {
  title: 'Components/FileUpload',
  component: FileUpload,
  args: {
    label: 'Δικαιολογητικά ζημίας',
    files: pipeline,
    onAdd: () => undefined,
    onRemove: () => undefined,
    onRetry: () => undefined,
    onPreview: () => undefined,
    accept: ['.pdf', '.jpg', '.png', '.heic', '.docx', '.msg'],
  },
} satisfies Meta<typeof FileUpload>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Pipeline: Story = {};

export const Interactive: Story = {
  render: function Render(args) {
    const [files, setFiles] = useState<UploadFile[]>([]);
    return (
      <FileUpload
        {...args}
        files={files}
        onAdd={(added) => {
          setFiles((current) => [
            ...current,
            ...added.map((f, i) => ({
              id: `${f.name}-${String(current.length + i)}`,
              name: f.name,
              size: f.size,
              type: f.type,
              status: 'done' as const,
            })),
          ]);
        }}
        onRemove={(id) => {
          setFiles((current) => current.filter((f) => f.id !== id));
        }}
      />
    );
  },
};

export const AtLimit: Story = {
  args: {
    maxFiles: 2,
    files: [
      { id: 'a', name: 'α.pdf', size: 1000, status: 'done' },
      { id: 'b', name: 'β.pdf', size: 1000, status: 'done' },
    ],
  },
};

export const ReadOnly: Story = { args: { isReadOnly: true, files: pipeline.slice(4, 5) } };

export const Large: Story = { args: { size: 'lg', files: [] } };
