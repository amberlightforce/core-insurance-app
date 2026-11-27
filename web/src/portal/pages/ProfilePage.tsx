import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { isApiError } from '../../api/client';
import { useIdempotencyKey } from '../../api/idempotency';
import {
  Banner,
  Button,
  Checkbox,
  EmptyState,
  Select,
  StatusPill,
  TextField,
  announce,
} from '../../design-system';
import { PortalHero, PortalPage, PortalProblem, PortalSection } from '../ui/PortalUi';
import { QueryView } from '../ui/QueryView';
import { usePortalTitle } from '../ui/hooks';
import rows from '../ui/pages.module.css';
import styles from '../ui/portal.module.css';
import {
  getContact,
  portalKeys,
  updateContact,
  type ContactUpdate,
  type PortalContact,
} from './portalApi';

type Kind = ContactUpdate['kind'];
type Action = ContactUpdate['action'];
type PointType = NonNullable<ContactUpdate['contactPoint']>['type'];
type AddressType = NonNullable<ContactUpdate['address']>['types'][number];

interface Editing {
  kind: Kind;
  action: Action;
  locationId?: string;
}

const pointTypes: PointType[] = ['MOBILE', 'LANDLINE', 'EMAIL'];
const addressTypes: AddressType[] = ['MAILING', 'BILLING', 'LEGAL'];

/** The PTY stale-record error: someone (or another device) changed the contact data since this page loaded. */
function isStale(error: unknown): boolean {
  return isApiError(error) && (error.code === 'PTY-ERR-STALE' || error.status === 409);
}

function Editor({
  contact,
  editing,
  onClose,
  onDone,
}: {
  contact: PortalContact;
  editing: Editing;
  onClose: () => void;
  onDone: (referred: boolean) => void;
}) {
  const { t } = useTranslation('portal');
  const client = useQueryClient();
  const { keyFor, release } = useIdempotencyKey();
  const existingPoint = contact.contactPoints.find((c) => c.contactPointId === editing.locationId);
  const existingAddress = contact.addresses.find((a) => a.addressId === editing.locationId);

  const [pointType, setPointType] = useState<PointType>(
    (existingPoint?.type === 'WORK_PHONE' ? 'LANDLINE' : existingPoint?.type) ?? 'MOBILE',
  );
  const [value, setValue] = useState(existingPoint?.value ?? '');
  const [addrType, setAddrType] = useState<AddressType>('MAILING');
  const [street, setStreet] = useState(existingAddress?.street ?? '');
  const [number, setNumber] = useState(existingAddress?.number ?? '');
  const [postcode, setPostcode] = useState(existingAddress?.postcode ?? '');
  const [locality, setLocality] = useState(existingAddress?.locality ?? '');
  const [country, setCountry] = useState(existingAddress?.country ?? 'GR');
  const [primary, setPrimary] = useState(
    existingPoint?.primary ?? existingAddress?.primary ?? false,
  );
  const [tried, setTried] = useState(false);

  const removing = editing.action === 'REMOVE';
  const request = (): ContactUpdate => ({
    action: editing.action,
    kind: editing.kind,
    ...(editing.locationId ? { locationId: editing.locationId } : {}),
    expectedRecordVersion: contact.recordVersion,
    ...(removing
      ? {}
      : editing.kind === 'CONTACT_POINT'
        ? { contactPoint: { type: pointType, value: value.trim(), primary } }
        : {
            address: {
              types: [addrType],
              primary,
              country: country.trim().toUpperCase(),
              street: street.trim(),
              number: number.trim(),
              postcode: postcode.trim(),
              locality: locality.trim(),
            },
          }),
  });

  const mutation = useMutation({
    mutationFn: () => {
      const body = request();
      return updateContact(body, keyFor(body));
    },
    onSuccess: (result) => {
      release();
      void client.invalidateQueries({ queryKey: portalKeys.contact });
      onDone(result.outcome === 'REFERRED');
    },
    onError: (error) => {
      // A stale version: load what is there now, so the customer sees it before trying again.
      if (isStale(error)) void client.invalidateQueries({ queryKey: portalKeys.contact });
    },
  });

  const invalid = removing
    ? false
    : editing.kind === 'CONTACT_POINT'
      ? value.trim() === ''
      : street.trim() === '' ||
        postcode.trim() === '' ||
        locality.trim() === '' ||
        country.trim() === '';

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t(`prf.editor.${editing.action}.${editing.kind}`)}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <h3 className={rows.rowTitle}>{t(`prf.editor.${editing.action}.${editing.kind}`)}</h3>
      {removing ? (
        <p className={styles.muted}>{t('prf.removeBody')}</p>
      ) : editing.kind === 'CONTACT_POINT' ? (
        <>
          <Select
            label={t('prf.pointType')}
            value={pointType}
            onChange={(v) => {
              if (v) setPointType(v as PointType);
            }}
            options={pointTypes.map((p) => ({ id: p, label: t(`prf.pointTypes.${p}`) }))}
          />
          <TextField
            label={t('prf.pointValue')}
            value={value}
            onChange={setValue}
            maxLength={320}
            type={pointType === 'EMAIL' ? 'email' : 'tel'}
            errorMessage={tried && invalid ? t('prf.required') : undefined}
          />
        </>
      ) : (
        <>
          <Select
            label={t('prf.addressType')}
            value={addrType}
            onChange={(v) => {
              if (v) setAddrType(v as AddressType);
            }}
            options={addressTypes.map((a) => ({ id: a, label: t(`prf.addressTypes.${a}`) }))}
          />
          <TextField
            label={t('prf.street')}
            value={street}
            onChange={setStreet}
            maxLength={200}
            errorMessage={tried && street.trim() === '' ? t('prf.required') : undefined}
          />
          <TextField label={t('prf.number')} value={number} onChange={setNumber} maxLength={20} />
          <TextField
            label={t('prf.postcode')}
            value={postcode}
            onChange={setPostcode}
            maxLength={20}
            errorMessage={tried && postcode.trim() === '' ? t('prf.required') : undefined}
          />
          <TextField
            label={t('prf.locality')}
            value={locality}
            onChange={setLocality}
            maxLength={100}
            errorMessage={tried && locality.trim() === '' ? t('prf.required') : undefined}
          />
          <TextField
            label={t('prf.country')}
            helperText={t('prf.countryHelp')}
            value={country}
            onChange={setCountry}
            maxLength={2}
            errorMessage={tried && country.trim() === '' ? t('prf.required') : undefined}
          />
        </>
      )}
      {removing ? null : (
        <Checkbox isSelected={primary} onChange={setPrimary}>
          {t('prf.primary')}
        </Checkbox>
      )}
      {mutation.isError ? (
        isStale(mutation.error) ? (
          <Banner variant="warning" live="alert" title={t('prf.stale')}>
            {t('prf.staleBody')}
          </Banner>
        ) : (
          <PortalProblem error={mutation.error} title={t('prf.failed')} />
        )
      ) : null}
      <div className={styles.actions}>
        <Button
          variant={removing ? 'danger' : 'primary'}
          isLoading={mutation.isPending}
          onPress={() => {
            setTried(true);
            if (!invalid) mutation.mutate();
          }}
        >
          {removing ? t('prf.confirmRemove') : t('prf.save')}
        </Button>
        <Button variant="ghost" onPress={onClose}>
          {t('prf.cancel')}
        </Button>
      </div>
    </form>
  );
}

function Details({ contact }: { contact: PortalContact }) {
  const { t } = useTranslation('portal');
  const [editing, setEditing] = useState<Editing | null>(null);
  const [referred, setReferred] = useState(false);
  const [saved, setSaved] = useState(false);

  const open = (next: Editing) => {
    setReferred(false);
    setSaved(false);
    setEditing(next);
  };
  const isEditing = (kind: Kind, id: string) => editing?.kind === kind && editing.locationId === id;

  return (
    <>
      {referred ? (
        <Banner variant="info" live="status" title={t('prf.referred')}>
          {t('prf.referredBody')}
        </Banner>
      ) : null}
      {saved ? (
        <Banner variant="success" live="status" title={t('prf.saved')}>
          {t('prf.savedBody')}
        </Banner>
      ) : null}

      <PortalSection title={t('prf.contactPoints')} family="brand">
        {contact.contactPoints.length === 0 ? (
          <p className={styles.muted}>{t('prf.noPoints')}</p>
        ) : (
          <ul className={rows.rows}>
            {contact.contactPoints.map((c) => (
              <li key={c.contactPointId} className={rows.row}>
                <div className={rows.rowMain}>
                  <p className={rows.rowTitle}>{c.value}</p>
                  <p className={rows.rowMeta}>
                    {t(`prf.pointTypes.${c.type}`, { defaultValue: c.type })}
                  </p>
                </div>
                <div className={rows.rowSide}>
                  {c.primary ? <StatusPill semantic="info" text={t('prf.primaryPill')} /> : null}
                  {c.verified ? <StatusPill semantic="success" text={t('prf.verified')} /> : null}
                  <Button
                    variant="ghost"
                    size="sm"
                    aria-label={t('prf.changeOne', { what: c.value })}
                    onPress={() => {
                      open({
                        kind: 'CONTACT_POINT',
                        action: 'REPLACE',
                        locationId: c.contactPointId,
                      });
                    }}
                  >
                    {t('prf.change')}
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    aria-label={t('prf.removeOne', { what: c.value })}
                    onPress={() => {
                      open({
                        kind: 'CONTACT_POINT',
                        action: 'REMOVE',
                        locationId: c.contactPointId,
                      });
                    }}
                  >
                    {t('prf.remove')}
                  </Button>
                </div>
                {isEditing('CONTACT_POINT', c.contactPointId) && editing ? (
                  <div className={rows.fullRow}>
                    <Editor
                      contact={contact}
                      editing={editing}
                      onClose={() => {
                        setEditing(null);
                      }}
                      onDone={(r) => {
                        setEditing(null);
                        setReferred(r);
                        setSaved(!r);
                        announce(r ? t('prf.referred') : t('prf.saved'));
                      }}
                    />
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}
        {editing?.kind === 'CONTACT_POINT' && editing.action === 'ADD' ? (
          <Editor
            contact={contact}
            editing={editing}
            onClose={() => {
              setEditing(null);
            }}
            onDone={(r) => {
              setEditing(null);
              setReferred(r);
              setSaved(!r);
              announce(r ? t('prf.referred') : t('prf.saved'));
            }}
          />
        ) : (
          <div className={styles.actions}>
            <Button
              variant="secondary"
              size="sm"
              onPress={() => {
                open({ kind: 'CONTACT_POINT', action: 'ADD' });
              }}
            >
              {t('prf.addPoint')}
            </Button>
          </div>
        )}
      </PortalSection>

      <PortalSection title={t('prf.addresses')} family="teal">
        {contact.addresses.length === 0 ? (
          <p className={styles.muted}>{t('prf.noAddresses')}</p>
        ) : (
          <ul className={rows.rows}>
            {contact.addresses.map((a) => {
              const text = a.formattedLines.join(', ');
              return (
                <li key={a.addressId} className={rows.row}>
                  <div className={rows.rowMain}>
                    <p className={rows.rowTitle}>{text}</p>
                  </div>
                  <div className={rows.rowSide}>
                    {a.primary ? <StatusPill semantic="info" text={t('prf.primaryPill')} /> : null}
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-label={t('prf.changeOne', { what: text })}
                      onPress={() => {
                        open({ kind: 'ADDRESS', action: 'REPLACE', locationId: a.addressId });
                      }}
                    >
                      {t('prf.change')}
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-label={t('prf.removeOne', { what: text })}
                      onPress={() => {
                        open({ kind: 'ADDRESS', action: 'REMOVE', locationId: a.addressId });
                      }}
                    >
                      {t('prf.remove')}
                    </Button>
                  </div>
                  {isEditing('ADDRESS', a.addressId) && editing ? (
                    <div className={rows.fullRow}>
                      <Editor
                        contact={contact}
                        editing={editing}
                        onClose={() => {
                          setEditing(null);
                        }}
                        onDone={(r) => {
                          setEditing(null);
                          setReferred(r);
                          setSaved(!r);
                          announce(r ? t('prf.referred') : t('prf.saved'));
                        }}
                      />
                    </div>
                  ) : null}
                </li>
              );
            })}
          </ul>
        )}
        {editing?.kind === 'ADDRESS' && editing.action === 'ADD' ? (
          <Editor
            contact={contact}
            editing={editing}
            onClose={() => {
              setEditing(null);
            }}
            onDone={(r) => {
              setEditing(null);
              setReferred(r);
              setSaved(!r);
              announce(r ? t('prf.referred') : t('prf.saved'));
            }}
          />
        ) : (
          <div className={styles.actions}>
            <Button
              variant="secondary"
              size="sm"
              onPress={() => {
                open({ kind: 'ADDRESS', action: 'ADD' });
              }}
            >
              {t('prf.addAddress')}
            </Button>
          </div>
        )}
      </PortalSection>
    </>
  );
}

/** `/portal/profile`: the customer's own address and contact points, changed through the party service. */
export function ProfilePage() {
  const { t } = useTranslation('portal');
  usePortalTitle(t('prf.title'));
  const query = useQuery({ queryKey: portalKeys.contact, queryFn: getContact });
  return (
    <PortalPage>
      <PortalHero overline={t('prf.overline')} title={t('prf.title')} lead={t('prf.lead')} />
      <QueryView query={query} errorTitle={t('prf.loadFailed')}>
        {(contact) =>
          contact.addresses.length === 0 && contact.contactPoints.length === 0 ? (
            <>
              <EmptyState
                kind="first-use"
                headingLevel={2}
                headline={t('prf.empty')}
                description={t('prf.emptyBody')}
              />
              <Details contact={contact} />
            </>
          ) : (
            <Details contact={contact} />
          )
        }
      </QueryView>
    </PortalPage>
  );
}
