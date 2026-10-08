import { Eye, EyeOff, FilePlus2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';

import { isApiError } from '../../api/client';
import type { PartyView } from '../../api/types';
import {
  Banner,
  Button,
  Dialog,
  KeyValueList,
  Select,
  announce,
  type KeyValueItem,
} from '../../design-system';
import { formatAfm } from '../../format';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { revealPurposes, useParty, useRevealParty } from './api';

const maskedDate = '••/••/••••';

function displayName(party: PartyView): string {
  const native = party.names.find((n) => n.form === 'NATIVE') ?? party.names[0];
  if (!native) return party.partyNumber;
  return (
    native.organisationName ?? [native.familyName, native.givenNames].filter(Boolean).join(' ')
  );
}

function PartyDetails({
  party,
  revealed,
  onReveal,
  onHide,
}: {
  party: PartyView;
  revealed: boolean;
  onReveal: () => void;
  onHide: () => void;
}) {
  const { t } = useTranslation('party');
  const navigate = useNavigate();
  const fmt = useFormat();
  const native = party.names.find((n) => n.form === 'NATIVE') ?? party.names[0];
  const latin = party.names.find((n) => n.form !== 'NATIVE');
  const name = displayName(party);

  const identity: KeyValueItem[] = [
    { id: 'number', label: t('view.fields.number'), value: party.partyNumber, kind: 'mono' },
    {
      id: 'type',
      label: t('view.fields.type'),
      value: party.partyType === 'PERSON' ? t('type.person') : t('type.organisation'),
    },
    { id: 'status', label: t('view.fields.status'), value: party.status },
    { id: 'name', label: t('view.fields.name'), value: name },
  ];
  if (latin) {
    identity.push({
      id: 'latin',
      label: t('view.fields.nameLatin'),
      value:
        latin.organisationName ?? [latin.familyName, latin.givenNames].filter(Boolean).join(' '),
    });
  }
  if (native?.fatherName) {
    identity.push({ id: 'father', label: t('view.fields.fatherName'), value: native.fatherName });
  }
  if (party.partyType === 'PERSON') {
    identity.push(
      party.p2Revealed
        ? { id: 'birth', label: t('view.fields.birthDate'), value: fmt.date(party.birthDate) }
        : {
            id: 'birth',
            label: t('view.fields.birthDate'),
            masked: { display: maskedDate, reason: t('view.maskedReason') },
          },
    );
  }
  identity.push({
    id: 'language',
    label: t('view.fields.language'),
    value: party.preferredLanguage,
  });

  const identifierItems: KeyValueItem[] = party.identifiers.map((identifier) => {
    const value =
      identifier.scheme === 'AFM' && !identifier.masked
        ? formatAfm(identifier.value)
        : identifier.value;
    return identifier.masked
      ? {
          id: identifier.identifierId,
          label: identifier.scheme,
          masked: { display: identifier.value, reason: t('view.maskedReason') },
        }
      : { id: identifier.identifierId, label: identifier.scheme, value, kind: 'mono' };
  });

  return (
    <div className={styles.page}>
      <PageHeader
        overline={t('overline')}
        title={name}
        recordId={party.partyNumber}
        actions={
          <>
            {revealed ? (
              <Button variant="secondary" icon={EyeOff} onPress={onHide}>
                {t('view.hide')}
              </Button>
            ) : (
              <Button variant="secondary" icon={Eye} onPress={onReveal}>
                {t('view.reveal')}
              </Button>
            )}
            <Button
              variant="primary"
              icon={FilePlus2}
              onPress={() => {
                void navigate(`/policies/quotes/new?partyId=${party.partyId}`);
              }}
            >
              {t('view.newQuote')}
            </Button>
          </>
        }
      />
      {revealed ? (
        <Banner variant="info" title={t('view.revealedTitle')}>
          {t('view.revealedBody')}
        </Banner>
      ) : null}
      <div className={styles.grid}>
        <Section title={t('view.sections.identity')}>
          <KeyValueList items={identity} aria-label={t('view.sections.identity')} />
        </Section>
        <Section title={t('view.sections.identifiers')}>
          {identifierItems.length > 0 ? (
            <KeyValueList items={identifierItems} aria-label={t('view.sections.identifiers')} />
          ) : (
            <p className={styles.muted}>{t('view.noIdentifiers')}</p>
          )}
        </Section>
        <Section title={t('view.sections.addresses')}>
          {party.addresses.length > 0 ? (
            <KeyValueList
              aria-label={t('view.sections.addresses')}
              items={party.addresses.map((a) => ({
                id: a.addressId,
                label: a.types.map((type) => t(`addressType.${type}`)).join(' · '),
                value: a.formattedLines.join(', '),
              }))}
            />
          ) : (
            <p className={styles.muted}>{t('view.noAddresses')}</p>
          )}
        </Section>
        <Section title={t('view.sections.contacts')}>
          {party.contactPoints.length > 0 ? (
            <KeyValueList
              aria-label={t('view.sections.contacts')}
              items={party.contactPoints.map((c) => ({
                id: c.contactPointId,
                label: t(`contactType.${c.type}`),
                value: c.value,
                kind: 'mono' as const,
              }))}
            />
          ) : (
            <p className={styles.muted}>{t('view.noContacts')}</p>
          )}
        </Section>
      </div>
    </div>
  );
}

/** Party view: personal (P2) values are masked until an audited reveal with a purpose (REQ-PTY-044). */
export function PartyViewPage() {
  const { partyId = '' } = useParams();
  const { t } = useTranslation('party');
  const query = useParty(partyId);
  const reveal = useRevealParty(partyId);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [purpose, setPurpose] = useState<string | null>(null);
  const [revealedView, setRevealedView] = useState<PartyView | null>(null);

  // Revealed P2 values never outlive the party they were revealed for: a route change (another party id) or leaving the
  // page clears them, not only closing the dialog.
  useEffect(
    () => () => {
      setRevealedView(null);
      setDialogOpen(false);
      setPurpose(null);
    },
    [partyId],
  );

  const closeDialog = () => {
    setDialogOpen(false);
    setPurpose(null);
    reveal.reset();
  };

  const error = reveal.isError
    ? isApiError(reveal.error) && reveal.error.status === 403
      ? t('view.revealDenied')
      : reveal.error.message
    : undefined;

  return (
    <QueryView query={query} notFoundMessage={t('view.notFound')}>
      {(data) => (
        <>
          <PartyDetails
            party={revealedView ?? data.party}
            revealed={revealedView !== null}
            onReveal={() => {
              setDialogOpen(true);
            }}
            onHide={() => {
              setRevealedView(null);
              announce(t('view.hidden'));
            }}
          />
          <Dialog
            title={t('view.revealDialog.title')}
            icon={Eye}
            tone="warning"
            isOpen={dialogOpen}
            onOpenChange={(open) => {
              if (!open) closeDialog();
            }}
            isBusy={reveal.isPending}
            {...(error ? { error } : {})}
            primaryAction={{
              label: t('view.revealDialog.confirm'),
              ...(purpose ? {} : { disabledReason: t('view.revealDialog.purposeRequired') }),
              onAction: () => {
                if (!purpose) return;
                reveal.mutate(purpose, {
                  onSuccess: (response) => {
                    setRevealedView(response.party);
                    announce(t('view.revealedTitle'));
                    closeDialog();
                  },
                });
              },
            }}
            closeOnAction={false}
          >
            <p>{t('view.revealDialog.body')}</p>
            <Select
              label={t('view.revealDialog.purpose')}
              isRequired
              options={revealPurposes.map((code) => ({ id: code, label: t(`purpose.${code}`) }))}
              value={purpose}
              onChange={setPurpose}
            />
          </Dialog>
        </>
      )}
    </QueryView>
  );
}
