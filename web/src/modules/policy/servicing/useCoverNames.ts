import { useMemo } from 'react';

import { useCatalogue } from '../../quote/api';
import { useTranslation } from 'react-i18next';

/** Cover code → localised name from the term's product catalogue; the code stays as the fallback while it loads. */
export function useCoverNames(artefactHash: string | undefined): ReadonlyMap<string, string> {
  const catalogue = useCatalogue(artefactHash);
  const { i18n } = useTranslation();
  const language = i18n.language;
  const coverages = catalogue.data?.coverages;
  return useMemo(
    () =>
      new Map(
        (coverages ?? []).map((c) => [c.code, language === 'en' ? c.name.en : c.name.el] as const),
      ),
    [coverages, language],
  );
}
