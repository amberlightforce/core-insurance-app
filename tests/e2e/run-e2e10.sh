#!/usr/bin/env bash
# Automated E2E-10 happy path (API level) on a fresh Docker Compose stack.
#
#   tests/e2e/run-e2e10.sh                  # build the image, start project "coreins-e2e10", run the test, tear down (down -v)
#   KEEP_STACK=1 tests/e2e/run-e2e10.sh     # leave the stack running afterwards (http://127.0.0.1:30000)
#   SKIP_BUILD=1 tests/e2e/run-e2e10.sh     # reuse an already built coreins-host:e2e10 image
#
# The stack uses its own compose project name, host ports in the 30000 range (tests/e2e/stack/e2e10.env) and its own image
# tag, so it runs beside a developer's own "coreins" stack without touching it. Needs docker (compose v2.24+), node >= 24, curl.
set -euo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
PROJECT=coreins-e2e10
API=${E2E_BASE_URL:-http://127.0.0.1:30000}
COMPOSE=(docker compose -p "$PROJECT" --project-directory "$ROOT/infra/local"
  -f "$ROOT/infra/local/compose.yaml" -f "$ROOT/tests/e2e/stack/compose.e2e10.yaml" --env-file "$ROOT/tests/e2e/stack/e2e10.env")

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "--- E2E-10 failed (exit $status); recent logs of the stack" >&2
    "${COMPOSE[@]}" ps -a >&2 || true
    "${COMPOSE[@]}" logs --no-color --tail 60 api worker migrate >&2 || true
  fi
  if [ "${KEEP_STACK:-0}" = "1" ]; then
    echo "KEEP_STACK=1: stack left running as project $PROJECT ($API)"
  else
    "${COMPOSE[@]}" down -v --remove-orphans >&2 || true
  fi
  exit "$status"
}
trap cleanup EXIT

echo "--- starting a fresh stack (project $PROJECT)"
"${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
if [ "${SKIP_BUILD:-0}" = "1" ]; then
  "${COMPOSE[@]}" up -d
else
  "${COMPOSE[@]}" up -d --build
fi

echo "--- waiting for $API/health/ready"
for i in $(seq 1 120); do
  if [ "$(curl -s -o /dev/null -w '%{http_code}' "$API/health/ready" || true)" = "200" ]; then
    echo "ready after about $((i * 2))s"
    break
  fi
  if [ "$i" = "120" ]; then
    echo "the api did not become ready" >&2
    exit 1
  fi
  sleep 2
done

echo "--- running the E2E-10 kill switch journey"
cd "$HERE"
if [ ! -d node_modules ]; then
  npm ci --no-audit
fi
export E2E_BASE_URL="$API" E2E_PRODUCT_SEED="$ROOT/src/CoreIns.Modules.Product/Seed/motor-gr.product.json"
npx playwright test e2e10 --workers=1
# The journey leaves the global kill switch active: the E2E-01 and E2E-02a happy paths now run with AI off and must give the same outcomes.
echo "--- re-running E2E-01 and E2E-02a with AI off"
npx playwright test 'e2e01\.spec' 'e2e02\.spec' --workers=1
