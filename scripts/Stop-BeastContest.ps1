$ErrorActionPreference='Stop'
$beastRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $beastRoot
$env:DOCKER_CONFIG=Join-Path $beastRoot '.local/docker'
docker compose down
if($LASTEXITCODE -ne 0){throw 'Docker shutdown failed.'}
# Named volumes are preserved.

