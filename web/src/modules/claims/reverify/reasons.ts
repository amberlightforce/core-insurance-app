/** Reason codes of the re-verification decision, exactly as CLM accepts them per decision (REQ-CLM-058 «reasons»). */
export const reverifyReasons = {
  KEEP: ['LOSS_BEFORE_CHANGE', 'CHANGE_NOT_MATERIAL', 'HANDLER_JUDGEMENT'],
  ADOPT: ['CHANGE_APPLIES', 'POLICY_CORRECTED'],
} as const;

export type ReverifyDecision = keyof typeof reverifyReasons;
export const reverifyDecisions: readonly ReverifyDecision[] = ['KEEP', 'ADOPT'];
