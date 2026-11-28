import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { Button, Select, TextField } from '../../design-system';
import { Section } from '../staff/PageHeader';
import styles from '../staff/staff.module.css';
import { subjectModules } from './api';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const typePattern = /^[A-Za-z][A-Za-z0-9]{1,39}$/;

/** Lookup of documents by the object they are about (module/type/id) or by document id. */
export function ObjectLookup() {
  const { t } = useTranslation('documents');
  const navigate = useNavigate();
  const [module, setModule] = useState<string | null>('POL');
  const [type, setType] = useState('');
  const [subjectId, setSubjectId] = useState('');
  const [tried, setTried] = useState(false);
  const [documentId, setDocumentId] = useState('');
  const [triedId, setTriedId] = useState(false);
  const subjectValid =
    module !== null && typePattern.test(type.trim()) && subjectId.trim().length > 0;
  const idValid = uuid.test(documentId.trim());

  return (
    <div className={styles.grid}>
      <Section title={t('home.bySubject')}>
        <form
          noValidate
          className={styles.stack}
          aria-label={t('home.bySubject')}
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (subjectValid && module)
              void navigate(
                `/documents/subject/${module}/${encodeURIComponent(type.trim())}/${encodeURIComponent(subjectId.trim())}`,
              );
          }}
        >
          <Select
            label={t('home.module')}
            id="documents-module"
            options={subjectModules.map((m) => ({ id: m, label: t(`modules.${m}`) }))}
            value={module}
            onChange={setModule}
          />
          <TextField
            label={t('home.type')}
            helperText={t('home.typeHelp')}
            value={type}
            onChange={setType}
            errorMessage={
              tried && !typePattern.test(type.trim()) ? t('home.typeInvalid') : undefined
            }
          />
          <TextField
            label={t('home.subjectId')}
            helperText={t('home.subjectIdHelp')}
            mono
            value={subjectId}
            onChange={setSubjectId}
            errorMessage={tried && subjectId.trim() === '' ? t('home.subjectIdInvalid') : undefined}
          />
          <div className={styles.row}>
            <Button type="submit" variant="primary">
              {t('home.list')}
            </Button>
          </div>
        </form>
      </Section>
      <Section title={t('home.byId')}>
        <form
          noValidate
          className={styles.stack}
          aria-label={t('home.byId')}
          onSubmit={(event) => {
            event.preventDefault();
            setTriedId(true);
            if (idValid) void navigate(`/documents/${documentId.trim()}`);
          }}
        >
          <TextField
            label={t('home.documentId')}
            helperText={t('home.documentIdHelp')}
            mono
            value={documentId}
            onChange={setDocumentId}
            errorMessage={triedId && !idValid ? t('home.documentIdInvalid') : undefined}
          />
          <div className={styles.row}>
            <Button type="submit" variant="primary">
              {t('home.open')}
            </Button>
          </div>
        </form>
      </Section>
    </div>
  );
}
