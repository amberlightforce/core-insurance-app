import { useMemo } from 'react';

import { useCatalogue } from '../../quote/api';
import { useLocalised } from '../../quote/useLocalised';

/** Cover code → localised name from the term's product catalogue; the code stays as the fallback while it loads. */
export function useCoverNames(artefactHash: string | undefined): ReadonlyMap<string, string> {
  const catalogue = useCatalogue(artefactHash);
  const localised = useLocalised();
  const coverages = catalogue.data?.coverages;
  return useMemo(
    () => new Map((coverages ?? []).map((c) => [c.code, localised(c.name)] as const)),
    [coverages, localised],
  );
}
