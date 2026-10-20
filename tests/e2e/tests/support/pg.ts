import { execFileSync } from 'node:child_process';

// Read-only SQL for journeys whose subject has no read API yet (RI cession rows, BIL respread rows, FIN journal links). The run script
// sets E2E_PSQL to "docker exec -i <postgres container> psql -U postgres -d coreins -At -F | -c"; rows come back as text, columns split on "|".

/** Runs one SELECT and returns the rows as arrays of text columns. */
export function sql(query: string): string[][] {
  const command = process.env['E2E_PSQL'];
  if (!command) throw new Error('E2E_PSQL is not set (run through tests/e2e/run-e2e11.sh)');
  const [exe, ...args] = command.split(' ');
  const out = execFileSync(exe!, [...args, query], { encoding: 'utf8' }).trim();
  return out === '' ? [] : out.split('\n').map((row) => row.split('|'));
}

/** One scalar (first column of the first row), or undefined when the query returns nothing. */
export const scalar = (query: string): string | undefined => sql(query)[0]?.[0];
