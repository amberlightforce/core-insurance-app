# Runs the same steps as .github/workflows/ci.yml on a developer machine (Windows PowerShell 5.1 or PowerShell 7).
#   .\scripts\ci-local.ps1                     # everything
#   .\scripts\ci-local.ps1 -SkipIntegration    # without Docker (integration tests need Testcontainers)
param([switch]$SkipIntegration)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'

function Step([string]$Name) { Write-Host "`n=== $Name" -ForegroundColor Cyan }

function Run([string]$Command, [string[]]$Arguments) {
    # Native tools report progress on stderr; success is judged by the exit code only.
    $ErrorActionPreference = 'Continue'
    & $Command @Arguments 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "'$Command $($Arguments -join ' ')' failed with exit code $LASTEXITCODE" }
}

Step 'Restore'
Run dotnet @('restore', 'CoreIns.sln')
Run dotnet @('tool', 'restore')

Step 'Build (warnings are errors)'
Run dotnet @('build', 'CoreIns.sln', '--configuration', 'Release', '--no-restore')

Step 'Generated contract code is current and deterministic'
Run dotnet @('run', '--project', 'tools/CoreIns.ContractGen', '--configuration', 'Release', '--no-build', '--', '--check')

Step 'Contract schemas and generated samples are valid'
Run python @('-m', 'pip', 'install', '--quiet', '-r', 'contracts/openapi/requirements.txt')
$eventSamples = @(Get-ChildItem tests/CoreIns.Contracts.Tests/Generated/Samples/events -Filter *.json | ForEach-Object { $_.FullName })
Run python (@('contracts/events/validate.py', '--require-jsonschema', '--instance') + $eventSamples)
Run python @('tools/CoreIns.ContractGen/validate_samples.py')

Step 'Unit, analyser and architecture tests'
# CoreIns.Testing.Contracts is a test-support library (sandbox doubles), not a test project.
Get-ChildItem tests -Filter *.csproj -Recurse -Depth 1 |
    Where-Object { $_.Name -notlike '*IntegrationTests*' -and $_.Name -notlike 'CoreIns.Testing.*' } |
    ForEach-Object { Run dotnet @('test', '--project', $_.FullName, '--configuration', 'Release', '--no-build') }

if ($SkipIntegration) {
    Step 'Integration tests SKIPPED (-SkipIntegration)'
} else {
    Step 'Integration tests (Testcontainers PostgreSQL 17, needs Docker)'
    Run dotnet @('test', '--project', 'tests/CoreIns.IntegrationTests', '--configuration', 'Release', '--no-build')
}

Step 'SBOM (.NET)'
Run dotnet @('CycloneDX', 'src/CoreIns.Host/CoreIns.Host.csproj', '--exclude-test-projects',
    '--output', 'artifacts/sbom', '--filename', 'dotnet.cdx.json', '--output-format', 'Json')

Step 'Bicep build + Key Vault least-privilege guard'
if (Get-Command az -ErrorAction SilentlyContinue) {
    New-Item -ItemType Directory -Force artifacts | Out-Null
    Run az @('bicep', 'build', '--file', 'infra/azure/main.bicep', '--outfile', 'artifacts/main.json')
    Run python @('scripts/check-keyvault-rbac.py', 'artifacts/main.json')
} else {
    Write-Host 'Azure CLI not found: skipped (runs in CI)'
}

Step 'Web'
Push-Location web
try {
    foreach ($script in @(@('ci', '--no-audit'), @('run', 'format:check'), @('run', 'lint'), @('run', 'typecheck'),
                          @('test'), @('run', 'build'), @('run', 'sbom'))) {
        Run npm $script
    }
} finally { Pop-Location }

Step 'End-to-end tests typecheck'
Push-Location tests/e2e
try {
    Run npm @('ci', '--no-audit')
    Run npm @('run', 'typecheck')
} finally { Pop-Location }

Step 'All CI steps passed'
