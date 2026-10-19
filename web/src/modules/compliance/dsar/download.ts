import type { DsarPackageView } from './api';

/** Saves the export package of a case as a JSON file (the DPO hands it to the data subject). */
export function downloadPackage(dsarId: string, pack: DsarPackageView | null | undefined) {
  try {
    const blob = new Blob([JSON.stringify(pack ?? {}, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `dsar-${dsarId}.json`;
    document.body.append(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  } catch {
    // TODO(hardening): no download in environments without Blob URLs; the package is still on the case.
  }
}
