import { appendFileSync } from 'node:fs';
import type { FullResult, Reporter, TestCase, TestResult } from '@playwright/test/reporter';

// Makes retry-only passes visible: Playwright reports them as "flaky" but the run stays green, so they are easy to miss.
// Emits a GitHub warning annotation per flaky test and appends a table to the job summary (GITHUB_STEP_SUMMARY).
// Never fails the run; a test that fails every attempt is already a failure.
export default class FlakyReporter implements Reporter {
  private readonly flaky: { title: string; file: string; attempts: number }[] = [];

  onTestEnd(test: TestCase, result: TestResult): void {
    // The last attempt passed but an earlier one did not => outcome "flaky".
    if (result.status === 'passed' && result.retry > 0 && test.outcome() === 'flaky') {
      this.flaky.push({ title: test.titlePath().slice(1).join(' > '), file: test.location.file, attempts: result.retry + 1 });
    }
  }

  onEnd(_result: FullResult): void {
    if (this.flaky.length === 0) return;
    for (const f of this.flaky) {
      console.log(`::warning title=Flaky E2E test::${f.title} passed only on attempt ${f.attempts}`);
    }
    const summary = process.env['GITHUB_STEP_SUMMARY'];
    if (summary) {
      const rows = this.flaky.map((f) => `| ${f.title.replace(/\|/g, '\|')} | ${f.attempts} |`).join('\n');
      try {
        appendFileSync(summary, `\n### Flaky E2E tests (passed only on retry)\n\n| Test | Attempts |\n|---|---|\n${rows}\n`);
      } catch {
        // summary is best effort
      }
    }
  }
}
