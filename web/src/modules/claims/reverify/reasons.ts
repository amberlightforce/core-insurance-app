/** Illustrative reason codes of the re-verification decision (the configured list, REQ-CLM-058 «reasons»). */
export const reverifyReasons = {
  KEEP: ['LOSS_BEFORE_CHANGE', 'CHANGE_NOT_MATERIAL', 'HANDLER_JUDGEMENT'],
  ADOPT: ['CHANGE_APPLIES', 'POLICY_CORRECTED', 'HANDLER_JUDGEMENT'],
} as const;

export type ReverifyDecision = keyof typeof reverifyReasons;
export const reverifyDecisions: readonly ReverifyDecision[] = ['KEEP', 'ADOPT'];
