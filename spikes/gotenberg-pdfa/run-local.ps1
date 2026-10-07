# Local path actually used for the evidence in FINDINGS.md:
#   - .NET 10 SDK (dotnet-install into a scratch dir; no machine-wide install)
#   - local Chrome 154 driven over CDP with Gotenberg's Page.printToPDF parameters
#   - veraPDF 1.30.2 CLI (headless izpack install) on a Temurin 21 JRE
param(
  [string]$Dotnet = "dotnet",
  [string]$VeraPdf = "verapdf.bat",
  [string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe",
  [switch]$SkipRender
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$out = Join-Path $here "out"
New-Item -ItemType Directory -Force $out | Out-Null
& $Dotnet build "$here\src\PdfaSpike" -c Release -o "$here\.bin"
$exe = "$here\.bin\PdfaSpike.exe"
$common = @("--out=$out", "--fonts=$here\fonts", "--chrome=$Chrome")
if ($SkipRender) { $null } else {
& $exe --mode=all @common
& $exe --mode=large --rows=4000 @common
& $exe --mode=large --rows=8000 @common
& $exe --mode=large --rows=16000 @common
& $exe --mode=large --rows=16000 --tagged=false @common
& $exe --mode=probe --hf=templates --name=hf-templates.pdf @common   # Chromium header/footer templates (fallback-font finding)
}

$ErrorActionPreference = "Continue"   # veraPDF logs font warnings on stderr
$files = Get-ChildItem "$out\det-run*.pdfa.pdf", "$out\det-run1.sealed.pdf", "$out\glyph-*.pdfa.pdf", "$out\large-16000.pdfa.pdf" | ForEach-Object FullName
$report = foreach ($f in "3a", "3b", "3u", "ua1") { "== flavour $f"; & $VeraPdf --flavour $f --format text $files 2>$null }
$report += "== raw Chromium output"; $report += & $VeraPdf --flavour 3b --format text "$out\det-run1.raw.pdf" 2>$null
$report += & $VeraPdf --flavour ua1 --format text "$out\det-run1.raw.pdf" 2>$null
$report += "== Chromium header/footer templates variant (raw)"; $report += & $VeraPdf --flavour ua1 --format text "$out\hf-templates.pdf" 2>$null
$report | ForEach-Object { $_ -replace [regex]::Escape("$out\"), "" } | Set-Content -Encoding utf8 "$out\verapdf-summary.txt"
