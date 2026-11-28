import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import type { PolicySearchCriteria, PolicySearchItem } from '../../api/types';
import {
  Button,
  TextField,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { PartySearchPanel } from '../party/PartySearchPanel';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import { useFormat } from '../staff/useFormat';
import { partyDisplayName, usePolicySearch } from './api';
import { useParty } from '../party/api';
import styles from './PolicySearch.module.css';
import { TermStatusPill } from './TermStatusPill';

/** Policy numbers are issued by PLT numbering (format is configuration): letters, digits and a few separators. */
const numberPattern = /^[A-Za-z0-9][A-Za-z0-9._/-]{0,63}$/;

/** The policyholder's name (pty.Party.get, cached per party); a short id until it loads. */
function InsuredName({ partyId }: { partyId: string }) {
  const { data } = useParty(partyId);
  return <span>{data ? partyDisplayName(data.party) : partyId.slice(0, 8)}</span>;
}

interface ChosenParty {
  id: string;
  label: string;
}

/** «Find a policy»: number and/or policyholder → pol.Policy.searchByCriteria, keyset «load more». */
export function PolicySearchSection() {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const navigate = useNavigate();
  const [number, setNumber] = useState('');
  const [party, setParty] = useState<ChosenParty | null>(null);
  const [picking, setPicking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [criteria, setCriteria] = useState<PolicySearchCriteria | null>(null);
  const query = usePolicySearch(criteria);

  const items = useMemo(() => query.data?.pages.flatMap((p) => p.items) ?? [], [query.data]);

  const columns = useMemo<DataColumn<PolicySearchItem>[]>(
    () => [
      identifierColumn<PolicySearchItem>(
        'policyNumber',
        t('search.columns.number'),
        (r) => r.policyNumber,
        { size: 160 },
      ),
      statusColumn<PolicySearchItem>(
        'status',
        t('search.columns.status'),
        (r) => r.status ?? '',
        (r) => (r.status ? <TermStatusPill state={r.status} /> : <span aria-hidden="true">—</span>),
        { size: 170 },
      ),
      textColumn<PolicySearchItem>(
        'from',
        t('search.columns.from'),
        (r) => fmt.date(r.termPeriod?.from),
        { size: 120 },
      ),
      textColumn<PolicySearchItem>(
        'to',
        t('search.columns.to'),
        (r) => fmt.date(r.termPeriod?.to),
        { size: 120 },
      ),
      textColumn<PolicySearchItem>('product', t('search.columns.product'), (r) => r.productCode, {
        size: 140,
      }),
      statusColumn<PolicySearchItem>(
        'insured',
        t('search.columns.insured'),
        (r) => r.insuredPartyId,
        (r) => <InsuredName partyId={r.insuredPartyId} />,
        { size: 240 },
      ),
    ],
    [t, fmt],
  );

  const submit = () => {
    const trimmed = number.trim();
    if (trimmed && !numberPattern.test(trimmed)) {
      setError(t('search.numberInvalid'));
      return;
    }
    if (!trimmed && !party) {
      setError(t('search.needCriteria'));
      return;
    }
    setError(null);
    setCriteria({
      ...(trimmed ? { policyNumber: trimmed } : {}),
      ...(party ? { insuredPartyId: party.id } : {}),
    });
  };

  return (
    <div>
      <form
        noValidate
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <TextField
          label={t('search.number')}
          mono
          value={number}
          onChange={setNumber}
          errorMessage={error ?? undefined}
        />
        <div className={styles.chosen}>
          {party ? (
            <>
              <span>{t('search.holder', { name: party.label })}</span>
              <Button
                variant="ghost"
                onPress={() => {
                  setParty(null);
                }}
              >
                {t('search.clearHolder')}
              </Button>
            </>
          ) : (
            <Button
              variant="secondary"
              onPress={() => {
                setPicking((p) => !p);
              }}
            >
              {picking ? t('search.hidePicker') : t('search.pickHolder')}
            </Button>
          )}
        </div>
        <Button type="submit" variant="primary">
          {t('search.submit')}
        </Button>
      </form>
      {picking && !party ? (
        <div className={styles.picker}>
          <PartySearchPanel
            pickOnClick
            label={t('search.holderSearch')}
            onOpen={(p) => {
              setParty({ id: p.partyId, label: `${p.displayName} (${p.partyNumber})` });
              setPicking(false);
            }}
          />
        </div>
      ) : null}
      {criteria ? (
        <div className={styles.picker}>
          <QueryView query={query}>
            {() => (
              <>
                <SimpleTable<PolicySearchItem>
                  aria-label={t('search.results')}
                  columns={columns}
                  data={items}
                  getRowId={(r) => r.policyId}
                  getRowLabel={(r) => r.policyNumber}
                  onOpen={(r) => {
                    void navigate(`/policies/${r.policyId}`);
                  }}
                  emptyState={<p className="ds-caption">{t('search.empty')}</p>}
                />
                {query.hasNextPage ? (
                  <div className={styles.more}>
                    <Button
                      variant="secondary"
                      isDisabled={query.isFetchingNextPage}
                      onPress={() => {
                        void query.fetchNextPage();
                      }}
                    >
                      {t('search.more')}
                    </Button>
                  </div>
                ) : null}
              </>
            )}
          </QueryView>
        </div>
      ) : null}
    </div>
  );
}
