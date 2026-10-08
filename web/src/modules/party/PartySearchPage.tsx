import { UserPlus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { Button } from '../../design-system';
import { PageHeader } from '../staff/PageHeader';
import styles from '../staff/staff.module.css';
import { PartySearchPanel } from './PartySearchPanel';

/** Party search (W2-PTY slice): the search terms live in component state only, never in the route or query. */
export function PartySearchPage() {
  const { t } = useTranslation('party');
  const navigate = useNavigate();
  const create = (
    <Button
      variant="primary"
      icon={UserPlus}
      onPress={() => {
        void navigate('/parties/new');
      }}
    >
      {t('search.create')}
    </Button>
  );
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('search.title')}
        overline={t('overline')}
        description={t('search.lead')}
        actions={create}
      />
      <div className={styles.section}>
        <PartySearchPanel
          emptyAction={create}
          onOpen={(party) => {
            void navigate(`/parties/${party.partyId}`);
          }}
        />
      </div>
    </div>
  );
}
