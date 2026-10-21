import type { DsarPackageView } from './api';

function clickDownload(href: string, fileName: string) {
  const link = document.createElement('a');
  link.href = href;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();
}

/**
 * Saves the export package of a case as a JSON file (the DPO hands it to the data subject). Uses a Blob URL and,
 * where Blob URLs are unavailable, a data: URL; returns false when neither works (the package stays on the case).
 */
export function downloadPackage(dsarId: string, pack: DsarPackageView | null | undefined): boolean {
  const json = JSON.stringify(pack ?? {}, null, 2);
  const fileName = `dsar-${dsarId}.json`;
  try {
    const url = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
    try {
      clickDownload(url, fileName);
    } finally {
      URL.revokeObjectURL(url);
    }
    return true;
  } catch {
    try {
      clickDownload(`data:application/json;charset=utf-8,${encodeURIComponent(json)}`, fileName);
      return true;
    } catch {
      return false;
    }
  }
}
