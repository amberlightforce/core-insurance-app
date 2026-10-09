import { parseDate } from '@internationalized/date';
import { useMutation } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';

import { useIdempotencyKey } from '../../api/idempotency';
import {
  Banner,
  Button,
  Checkbox,
  CurrencyField,
  DatePicker,
  Dialog,
  TextField,
} from '../../design-system';
import { PartySearchPanel } from '../party/PartySearchPanel';
import { useCatalogue, useProductVersion } from '../quote/api';
import { slice } from '../quote/state';
import { athensToday } from '../quote/time';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { problemOf } from '../staff/problem';
import staff from '../staff/staff.module.css';
import {
  createContract,
  updateContract,
  useContract,
  useRefreshContracts,
  type ContractCreateRequest,
  type ContractView,
} from './api';
import { draftErrors } from './editorModel';
import { LayerDiagram } from './LayerDiagram';
import { microToPct, pctToMicro } from './money';
import { PartyName } from './PartyName';
import { accountantRole, currentUser, hasRole } from './roles';

const eur = (amount: string) => ({ amount, currency: 'EUR' });
const newLayer = (layerNo: number) => ({
  layerNo,
  attachment: eur('0.00'),
  limit: eur('0.00'),
  aad: eur('0.00'),
  aal: null,
});

function initialDraft(contract?: ContractView): ContractCreateRequest {
  if (contract)
    return {
      legalEntity: contract.legalEntity,
      contractType: contract.contractType,
      contractYear: contract.contractYear,
      currency: contract.currency,
      period: contract.period,
      scope: contract.scope,
      clause: contract.clause,
      layers: contract.layers,
      participations: contract.participations,
      placedPct: contract.placedPct,
    };
  const from = athensToday();
  return {
    legalEntity: slice.legalEntity,
    contractType: 'XOL_PER_RISK',
    contractYear: Number(from.slice(0, 4)),
    currency: 'EUR',
    period: { from, to: parseDate(from).add({ years: 1 }).toString() },
    scope: { productCodes: [slice.product], coverageCodes: [] },
    clause: {
      alaeIncluded: false,
      statutoryInterestIncluded: false,
      recoveriesInure: 'REALISED_ONLY',
    },
    layers: [newLayer(1)],
    participations: [],
    placedPct: '100',
  };
}

function ContractEditor({ contract }: { contract?: ContractView }) {
  const { t, i18n } = useTranslation('reinsurance');
  const navigate = useNavigate();
  const refresh = useRefreshContracts();
  const { keyFor, release } = useIdempotencyKey();
  const [draft, setDraft] = useState(() => initialDraft(contract));
  const [tried, setTried] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [organisationError, setOrganisationError] = useState(false);
  const product = useProductVersion(draft.period.from);
  const catalogue = useCatalogue(product.data?.artefactHash);
  const validation = useMemo(() => draftErrors(draft), [draft]);
  const signed = useMemo(() => {
    try {
      return microToPct(
        draft.participations.reduce((sum, p) => sum + pctToMicro(p.signedLinePct), 0n),
      );
    } catch {
      return '—';
    }
  }, [draft.participations]);
  const mutation = useMutation({
    mutationFn: async () => {
      if (contract) {
        const request = {
          expectedRecordVersion: contract.recordVersion,
          period: draft.period,
          scope: draft.scope,
          clause: draft.clause,
          layers: draft.layers,
          participations: draft.participations,
          placedPct: draft.placedPct,
        };
        return updateContract(contract.contractId, request, keyFor(request));
      }
      return createContract(draft, keyFor(draft));
    },
    onSuccess: async (response) => {
      release();
      await refresh();
      void navigate(`/reinsurance/contracts/${response.contract.contractId}`);
    },
  });
  const roles = currentUser().roles;
  if (!hasRole(roles, accountantRole) && !hasRole(roles, 'Platform.Admin'))
    return <Banner variant="warning" title={t('editor.role')} />;
  if (contract && contract.status !== 'DRAFT')
    return <Banner variant="warning" title={t('editor.draftOnly')} />;
  const patchLayer = (
    index: number,
    field: 'attachment' | 'limit' | 'aad' | 'aal',
    amount: string | null,
  ) => {
    setDraft((d) => ({
      ...d,
      layers: d.layers.map((l, i) =>
        i === index
          ? { ...l, [field]: amount === null && field === 'aal' ? null : eur(amount ?? '0.00') }
          : l,
      ),
    }));
  };
  const problem = mutation.isError ? problemOf(mutation.error) : null;
  const explained =
    problem?.code && i18n.exists(`reinsurance:errors.codes.${problem.code}`)
      ? t(`errors.codes.${problem.code}`)
      : t('errors.generic');
  return (
    <div className={staff.page}>
      <PageHeader
        overline={t('overline')}
        title={t(contract ? 'editor.edit' : 'editor.new')}
        actions={<LinkButton to="/reinsurance">{t('contract.back')}</LinkButton>}
      />
      <form
        className={staff.stack}
        onSubmit={(e) => {
          e.preventDefault();
          setTried(true);
          if (validation.length === 0 && catalogue.data) setConfirm(true);
        }}
      >
        <Section title={t('editor.terms')}>
          <div className={staff.grid}>
            <TextField
              label={t('editor.year')}
              isRequired
              isReadOnly={Boolean(contract)}
              inputMode="numeric"
              value={String(draft.contractYear)}
              onChange={(value) => {
                setDraft((d) => ({ ...d, contractYear: Number(value) }));
              }}
            />
            <TextField label={t('editor.product')} isReadOnly value={slice.product} />
            <DatePicker
              label={t('editor.from')}
              isRequired
              value={parseDate(draft.period.from)}
              onChange={(date) => {
                if (date)
                  setDraft((d) => ({ ...d, period: { ...d.period, from: date.toString() } }));
              }}
            />
            <DatePicker
              label={t('editor.to')}
              isRequired
              value={draft.period.to ? parseDate(draft.period.to) : null}
              onChange={(date) => {
                setDraft((d) => ({ ...d, period: { ...d.period, to: date?.toString() ?? null } }));
              }}
            />
          </div>
          <QueryView query={product}>
            {() => (
              <QueryView query={catalogue}>
                {(data) => (
                  <div className={staff.stack}>
                    {(data.coverages ?? []).map((cover) => (
                      <Checkbox
                        key={cover.code}
                        isSelected={draft.scope.coverageCodes.includes(cover.code)}
                        onChange={(selected) => {
                          setDraft((d) => ({
                            ...d,
                            scope: {
                              ...d.scope,
                              coverageCodes: selected
                                ? [...d.scope.coverageCodes, cover.code]
                                : d.scope.coverageCodes.filter((c) => c !== cover.code),
                            },
                          }));
                        }}
                      >
                        {i18n.language === 'en' ? cover.name.en : cover.name.el}
                      </Checkbox>
                    ))}
                  </div>
                )}
              </QueryView>
            )}
          </QueryView>
          <Checkbox
            isSelected={draft.clause.alaeIncluded}
            onChange={(value) => {
              setDraft((d) => ({ ...d, clause: { ...d.clause, alaeIncluded: value } }));
            }}
          >
            {t('terms.alae')}
          </Checkbox>
          <Checkbox
            isSelected={draft.clause.statutoryInterestIncluded}
            onChange={(value) => {
              setDraft((d) => ({
                ...d,
                clause: { ...d.clause, statutoryInterestIncluded: value },
              }));
            }}
          >
            {t('terms.interest')}
          </Checkbox>
        </Section>
        <Section title={t('layers.title')}>
          {draft.layers.map((layer, index) => (
            <div className={staff.stack} key={layer.layerNo}>
              <h3>{t('layers.layerNo', { no: layer.layerNo })}</h3>
              <div className={staff.grid}>
                {(['attachment', 'limit', 'aad', 'aal'] as const).map((field) => (
                  <CurrencyField
                    key={field}
                    label={t(`layers.columns.${field}`)}
                    value={layer[field]?.amount ?? null}
                    allowNegative={false}
                    isRequired={field !== 'aal'}
                    onChange={(amount) => {
                      patchLayer(index, field, amount);
                    }}
                  />
                ))}
              </div>
              {draft.layers.length > 1 ? (
                <Button
                  variant="ghost"
                  onPress={() => {
                    setDraft((d) => ({
                      ...d,
                      layers: d.layers
                        .filter((_, i) => i !== index)
                        .map((l, i) => ({ ...l, layerNo: i + 1 })),
                    }));
                  }}
                >
                  {t('editor.remove')}
                </Button>
              ) : null}
            </div>
          ))}
          <Button
            variant="secondary"
            onPress={() => {
              setDraft((d) => ({ ...d, layers: [...d.layers, newLayer(d.layers.length + 1)] }));
            }}
          >
            {t('editor.addLayer')}
          </Button>
          <LayerDiagram layers={draft.layers} />
        </Section>
        <Section title={t('participants.title')}>
          <TextField
            label={t('editor.placed')}
            isRequired
            inputMode="decimal"
            value={draft.placedPct}
            onChange={(value) => {
              setDraft((d) => ({ ...d, placedPct: value.replaceAll(',', '.') }));
            }}
          />
          <p>{t('editor.signedTotal', { signed, placed: draft.placedPct })}</p>
          {draft.participations.map((p, index) => (
            <div className={staff.grid} key={p.reinsurerPartyId}>
              <PartyName partyId={p.reinsurerPartyId} />
              <TextField
                label={t('participants.columns.signedLine')}
                isRequired
                inputMode="decimal"
                value={p.signedLinePct}
                onChange={(value) => {
                  setDraft((d) => ({
                    ...d,
                    participations: d.participations.map((line, i) =>
                      i === index ? { ...line, signedLinePct: value.replaceAll(',', '.') } : line,
                    ),
                  }));
                }}
              />
              <Checkbox
                isSelected={p.lead}
                onChange={(lead) => {
                  setDraft((d) => ({
                    ...d,
                    participations: d.participations.map((line, i) => ({
                      ...line,
                      lead: i === index && lead,
                    })),
                  }));
                }}
              >
                {t('participants.lead')}
              </Checkbox>
              <Button
                variant="ghost"
                onPress={() => {
                  setDraft((d) => ({
                    ...d,
                    participations: d.participations.filter((_, i) => i !== index),
                  }));
                }}
              >
                {t('editor.remove')}
              </Button>
            </div>
          ))}
          <PartySearchPanel
            pickOnClick
            label={t('editor.findOrganisation')}
            onOpen={(party) => {
              if (party.partyType !== 'ORGANISATION') {
                setOrganisationError(true);
                return;
              }
              setOrganisationError(false);
              setDraft((d) =>
                d.participations.some((p) => p.reinsurerPartyId === party.partyId)
                  ? d
                  : {
                      ...d,
                      participations: [
                        ...d.participations,
                        {
                          reinsurerPartyId: party.partyId,
                          signedLinePct: '0',
                          lead: d.participations.length === 0,
                        },
                      ],
                    },
              );
            }}
          />
          {organisationError ? (
            <Banner variant="warning" live="alert" title={t('editor.organisationOnly')} />
          ) : null}
        </Section>
        {tried && validation.length ? (
          <Banner variant="warning" live="alert" title={t('editor.fixFields')}>
            <ul>
              {validation.map((error) => (
                <li key={error}>{t(`editor.validation.${error}`)}</li>
              ))}
            </ul>
          </Banner>
        ) : null}
        {problem ? (
          <Banner variant="danger" live="alert" title={t('errors.title')}>
            <p>{explained}</p>
            <Button
              variant="secondary"
              onPress={() => {
                setConfirm(false);
                setTried(true);
              }}
            >
              {t('editor.goToFields')}
            </Button>
          </Banner>
        ) : null}
        <Button type="submit" variant="commit" isDisabled={mutation.isPending}>
          {t('editor.save')}
        </Button>
      </form>
      <Dialog
        title={t('editor.confirmTitle')}
        isOpen={confirm}
        isBusy={mutation.isPending}
        onOpenChange={setConfirm}
        closeOnAction={false}
        primaryAction={{
          label: t('editor.save'),
          variant: 'commit',
          onAction: () => {
            mutation.mutate();
          },
        }}
      >
        {t('editor.confirmBody')}
      </Dialog>
    </div>
  );
}

export function NewContractPage() {
  return <ContractEditor />;
}
export function EditContractPage() {
  const { contractId = '' } = useParams();
  const query = useContract(contractId);
  return (
    <QueryView query={query}>
      {(contract) => <ContractEditor key={contract.contractId} contract={contract} />}
    </QueryView>
  );
}
