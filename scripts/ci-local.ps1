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

Step 'Unit, analyser and architecture tests'
Get-ChildItem tests -Filter *.csproj -Recurse -Depth 1 |
    Where-Object { $_.Name -notlike '*IntegrationTests*' } |
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
