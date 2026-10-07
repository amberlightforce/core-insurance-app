#!/usr/bin/env bash
# Runs the same steps as .github/workflows/ci.yml on a developer machine.
#   scripts/ci-local.sh                 # everything
#   SKIP_INTEGRATION=1 scripts/ci-local.sh   # without Docker (integration tests need Testcontainers)
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true

step() { printf '\n=== %s\n' "$1"; }

step "Restore"
dotnet restore CoreIns.sln
dotnet tool restore

step "Build (warnings are errors)"
dotnet build CoreIns.sln --configuration Release --no-restore

step "Generated contract code is current and deterministic"
dotnet run --project tools/CoreIns.ContractGen --configuration Release --no-build -- --check

step "Contract schemas and generated samples are valid"
python3 -m pip install --quiet -r contracts/openapi/requirements.txt
python3 contracts/events/validate.py --require-jsonschema --instance tests/CoreIns.Contracts.Tests/Generated/Samples/events/*.json
python3 tools/CoreIns.ContractGen/validate_samples.py

step "Unit, analyser and architecture tests"
for project in tests/*/*.csproj; do
  # CoreIns.Testing.Contracts is a test-support library (sandbox doubles), not a test project.
  case "$project" in *IntegrationTests*|*/CoreIns.Testing.*) continue ;; esac
  dotnet test --project "$project" --configuration Release --no-build
done

if [[ "${SKIP_INTEGRATION:-0}" == "1" ]]; then
  step "Integration tests SKIPPED (SKIP_INTEGRATION=1)"
else
  step "Integration tests (Testcontainers PostgreSQL 17, needs Docker)"
  dotnet test --project tests/CoreIns.IntegrationTests --configuration Release --no-build
fi

step "SBOM (.NET)"
dotnet CycloneDX src/CoreIns.Host/CoreIns.Host.csproj --exclude-test-projects \
  --output artifacts/sbom --filename dotnet.cdx.json --output-format Json

step "Bicep build + Key Vault least-privilege guard"
if command -v az > /dev/null 2>&1; then
  mkdir -p artifacts
  az bicep build --file infra/azure/main.bicep --outfile artifacts/main.json
  python3 scripts/check-keyvault-rbac.py artifacts/main.json
else
  echo "Azure CLI not found: skipped (runs in CI)"
fi

step "Web"
(
  cd web
  npm ci --no-audit
  npm run format:check
  npm run lint
  npm run typecheck
  npm test
  npm run build
  npm run sbom
)

step "End-to-end tests typecheck"
(
  cd tests/e2e
  npm ci --no-audit
  npm run typecheck
)

step "All CI steps passed"
