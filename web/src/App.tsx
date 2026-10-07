import { useTranslation } from 'react-i18next';

/** Placeholder shell until the design system (F-1d) provides the app shell. */
export function App() {
  const { t } = useTranslation();

  return (
    <main className="app">
      <h1>{t('app.title')}</h1>
    </main>
  );
}
