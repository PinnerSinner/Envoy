<#
.SYNOPSIS
  Authenticode-signs one file for an Envoy release (spec 06, REQ-DIST-8): SHA-256 file digest and an RFC 3161 timestamp.

.DESCRIPTION
  Usage: pwsh -NoProfile -File tools/release/sign.ps1 <file>

  Runs only in v* tag builds, in steps gated by the protected `release` environment (tools/release/check_signing_scope.py
  enforces that). The workflow's "Signing setup" step decodes the ENVOY_SIGN_PFX_BASE64 environment secret to
  $env:RUNNER_TEMP\envoy-sign.pfx; the password comes from ENVOY_SIGN_PFX_PASSWORD. Without the .pfx it prints a notice
  and exits 0 (the release is published unsigned); a signtool failure fails the build. vpk calls this script for every
  binary it packs (--signTemplate), and the workflow calls it for the portable Envoy.exe.

  Other signing services: replace the signtool call below with the service's own client, keeping the same contract
  (one file argument, exit code 0 on success). Azure Trusted Signing: `signtool sign /dlib Azure.CodeSigning.Dlib.dll
  /dmdf metadata.json ...`. SignPath (free for open source): submit the file through its GitHub Action or REST API and
  replace the file with the signed one.
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $File
)

$ErrorActionPreference = "Stop"

$pfx = Join-Path $env:RUNNER_TEMP "envoy-sign.pfx"
if (-not $env:RUNNER_TEMP -or -not (Test-Path $pfx)) {
    Write-Output "::notice::Code signing skipped for $(Split-Path $File -Leaf) (no certificate)"
    exit 0
}
if (-not (Test-Path $File)) {
    Write-Error "sign.ps1: $File does not exist"
    exit 1
}

# The newest signtool.exe from the Windows 10/11 SDK on the runner.
$kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$signtool = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' -and $_.Directory.Parent.Name -match '^\d+(\.\d+)+$' } |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
    Select-Object -First 1
if (-not $signtool) {
    Write-Error "sign.ps1: signtool.exe not found under $kits"
    exit 1
}

& $signtool.FullName sign /fd SHA256 /f $pfx /p $env:ENVOY_SIGN_PFX_PASSWORD /tr http://timestamp.digicert.com /td SHA256 $File
if ($LASTEXITCODE -ne 0) {
    Write-Error "sign.ps1: signtool failed for $File (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}
Write-Output "Signed $(Split-Path $File -Leaf)"
