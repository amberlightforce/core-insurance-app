#!/usr/bin/env bash
# Post-deploy smoke test against a stamp's api (used by .github/workflows/deploy.yml; also runnable by hand).
# usage: scripts/smoke.sh <fqdn-or-base-url>
# Checks: liveness, readiness depth (body must not report an Unhealthy check), anonymous API calls get a 401 from the
# application (never a sign-in redirect, INFRASTRUCTURE 6.1) on several modules, and - when SMOKE_TOKEN (a bearer
# token) is set - authenticated read endpoints return 2xx.
set -euo pipefail

TARGET="${1:?usage: smoke.sh <fqdn-or-base-url>}"
BASE="${TARGET%/}"
[[ "$BASE" == http* ]] || BASE="https://$BASE"
RETRIES="${SMOKE_RETRIES:-12}"
FAILED=0

code() { curl --silent --output /dev/null --max-time 20 --write-out '%{http_code}' "$@" || echo 000; }

expect() { # expect <label> <wanted-code> <actual-code>
  if [[ "$2" == "$3" ]]; then echo "ok   $1 -> $3"; else echo "FAIL $1 -> $3 (wanted $2)" >&2; FAILED=1; fi
}

# Retry liveness/readiness: a fresh revision can take a short while to take traffic.
for path in /health/live /health/ready; do
  got=000
  for ((i = 1; i <= RETRIES; i++)); do
    got=$(code "$BASE$path")
    [[ "$got" == 200 ]] && break
    echo "wait $path -> $got (attempt $i/$RETRIES)"
    sleep 10
  done
  expect "$path" 200 "$got"
done

READY_BODY=$(curl --silent --max-time 20 "$BASE/health/ready" || true)
echo "ready body: ${READY_BODY:0:400}"
if grep -qi 'unhealthy' <<<"$READY_BODY"; then echo "FAIL /health/ready reports an Unhealthy check" >&2; FAILED=1; fi

# Anonymous API reads must be rejected by the app itself (401), across modules.
for path in /api/plt/v1/health /api/pol/v1/policies /api/clm/v1/claims /api/bil/v1/invoices; do
  got=$(code "$BASE$path")
  # 401 = authentication enforced. 404 is tolerated for module paths that may not exist; 3xx/200/5xx fail.
  if [[ "$got" == 401 || ("$path" != /api/plt/v1/health && "$got" == 404) ]]; then
    echo "ok   $path (anonymous) -> $got"
  else
    echo "FAIL $path (anonymous) -> $got (wanted 401)" >&2
    FAILED=1
  fi
done

if [[ -n "${SMOKE_TOKEN:-}" ]]; then
  for path in /api/plt/v1/health; do
    got=$(code -H "Authorization: Bearer $SMOKE_TOKEN" "$BASE$path")
    if [[ "$got" =~ ^2 ]]; then echo "ok   $path (bearer) -> $got"; else echo "FAIL $path (bearer) -> $got (wanted 2xx)" >&2; FAILED=1; fi
  done
else
  echo "skip authenticated reads (SMOKE_TOKEN not set)"
fi

exit "$FAILED"
