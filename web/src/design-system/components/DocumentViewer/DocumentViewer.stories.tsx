import type { Meta, StoryObj } from '@storybook/react-vite';

import { DocumentViewer } from './index';

function pageSvg(n: number): string {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="794" height="1123" viewBox="0 0 794 1123"><rect width="794" height="1123" fill="white"/><text x="72" y="120" font-family="sans-serif" font-size="28">Σελίδα ${String(n)}</text></svg>`;
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
}

const pages = [1, 2, 3, 4].map(pageSvg);

const meta = {
  title: 'Components/DocumentViewer',
  component: DocumentViewer,
  args: {
    title: 'Ασφαλιστήριο ΑΣΦ-2026-0412',
    version: 'v3',
    language: { code: 'el', binding: true },
    pageCount: pages.length,
    pageImages: pages,
    onDownload: () => undefined,
    onPrint: () => undefined,
    onOpenNewWindow: () => undefined,
    onSearch: () => undefined,
    moreActions: [{ id: 'meta', label: 'Μεταδεδομένα', onAction: () => undefined }],
  },
  argTypes: {
    state: { control: 'inline-radio', options: ['loading', 'ready', 'error', 'unsupported'] },
  },
  decorators: [
    (Story) => (
      <div style={{ blockSize: 640 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof DocumentViewer>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const WithRails: Story = {
  args: {
    showThumbnails: true,
    rightPanel: <p style={{ margin: 0 }}>Αποτύπωμα sha256:ab12… · Διατήρηση 10 έτη</p>,
    rightPanelLabel: 'Μεταδεδομένα',
  },
};

export const Banners: Story = {
  args: { legalHold: true, supersededBy: { version: '4', onOpen: () => undefined } },
};

export const Loading: Story = { args: { state: 'loading' } };
export const LoadFailed: Story = { args: { state: 'error' } };
export const Unsupported: Story = {
  args: {
    state: 'unsupported',
    title: 'email-asfalismenou.msg',
    pageCount: 0,
    file: {
      name: 'email-asfalismenou.msg',
      type: 'application/vnd.ms-outlook',
      size: 48_200,
      hash: 'sha256:9f2c…e1',
    },
    textPreview: 'Καλημέρα σας,\nσας επισυνάπτω τις φωτογραφίες της ζημίας.',
  },
};
