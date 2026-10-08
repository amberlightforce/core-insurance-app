import { useTranslation } from 'react-i18next';

import { Banner, Button } from '../../design-system';
import { problemOf } from '../staff/problem';
import { ProblemBanner } from '../staff/ProblemBanner';
import { explainInputProblem, fieldLabelOf, type InputField } from './inputProblems';
import type { StepId } from './state';

export interface InputProblemBannerProps {
  error: unknown;
  title: string;
  /** Labels of the fields that come from the PFC question set (use, claims), by input field. */
  questionLabels: Partial<Record<InputField, string>>;
  coverNames: ReadonlyMap<string, string>;
  /** Jumps to the step and focuses the field with this label (if any). */
  onGo: (step: StepId, fieldLabel: string | undefined) => void;
}

/**
 * A rating or underwriting input refusal in plain words: which field, on which step, what to enter, and a button
 * that takes the user there. The code and trace id stay available, but as small secondary text. Any other problem
 * falls back to the generic banner.
 */
export function InputProblemBanner({
  error,
  title,
  questionLabels,
  coverNames,
  onGo,
}: InputProblemBannerProps) {
  const { t } = useTranslation('quote');
  const staff = useTranslation('staff').t;
  const problem = problemOf(error);
  const fix = explainInputProblem(problem);
  if (!fix) return <ProblemBanner error={error} title={title} />;

  const label = fieldLabelOf(fix, t, questionLabels) ?? '';
  const cover = (fix.cover ? coverNames.get(fix.cover) : undefined) ?? t('inputProblem.thisCover');
  const step = fix.step;
  return (
    <Banner
      variant="danger"
      live="alert"
      title={title}
      actions={
        step ? (
          <Button
            variant="secondary"
            size="sm"
            onPress={() => {
              onGo(step, label || undefined);
            }}
          >
            {t('inputProblem.goTo', { step: t(`steps.${step}`) })}
          </Button>
        ) : undefined
      }
    >
      <p>{t(`inputProblem.fields.${fix.field}.what`, { cover, label })}</p>
      <p>{t(`inputProblem.fields.${fix.field}.fix`, { cover, label })}</p>
      <details className="ds-caption">
        <summary>{t('inputProblem.technical')}</summary>
        {problem.code ? <span className="ds-mono">{problem.code}</span> : null}
        {problem.traceId ? (
          <span>{` · ${staff('problem.trace', { id: problem.traceId })}`}</span>
        ) : null}
        {problem.detail ? <p className="ds-mono">{problem.detail}</p> : null}
      </details>
    </Banner>
  );
}
