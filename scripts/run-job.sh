#!/usr/bin/env bash
# Starts a Container Apps job (bootstrap | migrate) in $RESOURCE_GROUP and waits for it to finish.
# Used by .github/workflows/deploy.yml; needs an authenticated Azure CLI.
set -euo pipefail

JOB="${1:?usage: run-job.sh <job-name>}"
: "${RESOURCE_GROUP:?RESOURCE_GROUP must be set}"
TIMEOUT_SECONDS="${JOB_TIMEOUT_SECONDS:-1200}"

EXECUTION=$(az containerapp job start --name "$JOB" --resource-group "$RESOURCE_GROUP" --query name -o tsv)
echo "$JOB: started execution $EXECUTION"

DEADLINE=$((SECONDS + TIMEOUT_SECONDS))
while ((SECONDS < DEADLINE)); do
  STATUS=$(az containerapp job execution show --name "$JOB" --resource-group "$RESOURCE_GROUP" \
    --job-execution-name "$EXECUTION" --query properties.status -o tsv)
  echo "$JOB: $STATUS"
  case "$STATUS" in
    Succeeded) exit 0 ;;
    Failed | Stopped | Degraded) exit 1 ;;
  esac
  sleep 10
done

echo "$JOB did not finish within ${TIMEOUT_SECONDS}s" >&2
exit 1
