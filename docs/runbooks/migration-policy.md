# Migration policy: expand / contract

The deploy runs `migrate` **before** the new api/worker start, and rollback re-deploys an older image against a schema
that has already moved on. So every release must be safe with both the previous and the next app version running against
its schema. Migrations are forward-only and applied ones are never edited.

## Rules
1. **Expand** (release N): only additive, backward-compatible changes - new nullable columns or columns with a default,
   new tables, new indexes (use `CONCURRENTLY` for large tables), new enum values. Old code must keep working unchanged.
2. **Migrate/backfill** in code or a separate idempotent step; dual-write old and new shape while both versions can run.
3. **Contract** (release N+1 or later, after N is proven in `prod` and rollback to N-1 is no longer needed): drop or
   rename-by-copy old columns/tables, add `NOT NULL`/constraints, remove the dual-write. Never in the same release as the
   code that stops using them.
4. Never in one release: `DROP`/`RENAME` of a column or table still read by the previous version, `ALTER COLUMN TYPE`
   with a table rewrite, adding `NOT NULL` without a default to a populated table, destructive data rewrites.
5. Each migration is small, idempotent where practical, and avoids long locks (the `migrate` job wait is
   `JOB_TIMEOUT_SECONDS`, default 1200 s).
6. A release containing a contract step is labelled so in the commit message and needs a verified restore point first
   ([backup-restore.md](backup-restore.md)); it cannot be rolled back by image alone.

## Review checklist
- Could the previous image run against the schema after this migration? If not, split it into expand + contract.
- Is anything destructive? Then it is a contract step: confirm the previous release has been live in `prod` and no
  rollback target depends on the old shape.
- Rollback story stated: image-only (expand) or fix-forward/restore (contract).

See [deploy-rollback.md](deploy-rollback.md) and [migration-failure.md](migration-failure.md).
