# Exports public certificates only, never private keys, into an ignored local file.
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTools = Join-Path $taskRoot '.tools'
New-Item -ItemType Directory -Force -Path $taskTools | Out-Null
$taskCertificates = Get-ChildItem Cert:\LocalMachine\Root,Cert:\CurrentUser\Root |
    Sort-Object -Property Thumbprint -Unique
$taskPem = foreach ($certificate in $taskCertificates) {
    '-----BEGIN CERTIFICATE-----'
    [Convert]::ToBase64String($certificate.RawData, [Base64FormattingOptions]::InsertLineBreaks)
    '-----END CERTIFICATE-----'
}
[IO.File]::WriteAllLines((Join-Path $taskTools 'docker-build-ca.pem'), $taskPem, [Text.Encoding]::ASCII)
Write-Output 'Public root bundle exported to .tools/docker-build-ca.pem (ignored by Git/Docker context).'

# Compose automatically merges this machine-local override on subsequent builds.
$taskOverride = @"
services:
  accumulator:
    build:
      secrets:
        - build_ca
  generator:
    build:
      secrets:
        - build_ca
secrets:
  build_ca:
    file: ./.tools/docker-build-ca.pem
"@
$taskOverridePath = Join-Path $taskRoot 'compose.override.yaml'
if (Test-Path -LiteralPath $taskOverridePath) {
    $taskExistingOverride = [IO.File]::ReadAllText($taskOverridePath)
    if ($taskExistingOverride.Trim() -ne $taskOverride.Trim()) {
        throw 'compose.override.yaml already exists with custom configuration. Add build_ca manually; the file was preserved.'
    }
} else {
    [IO.File]::WriteAllText($taskOverridePath, $taskOverride)
}
Write-Output 'Compose build certificate configured. Run docker compose up --build -d normally.'
