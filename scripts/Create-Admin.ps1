$ErrorActionPreference='Stop'
$beastRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $beastRoot
$env:DOCKER_CONFIG=Join-Path $beastRoot '.local/docker'

try {
    if(!(Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker is unavailable. Install Docker Desktop and run Start-BeastContest.bat first.'
    }
    $beastBytes=New-Object byte[] 24
    $beastRng=[System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $beastRng.GetBytes($beastBytes) } finally { $beastRng.Dispose() }
    $beastPassword='Bc!'+[Convert]::ToBase64String($beastBytes)

    # Feed the password through stdin, never through command-line arguments.
    $beastPassword | docker compose exec -T web dotnet BeastContest.Web.dll --create-user admin --role Admin --name Administrator
    if($LASTEXITCODE -ne 0) {
        throw 'Admin was not created. If admin already exists, its password is unchanged. Otherwise run Start-BeastContest.bat first and check the error above.'
    }

    Write-Host ''
    Write-Host 'Administrator created successfully.' -ForegroundColor Green
    Write-Host 'Login:    admin'
    Write-Host "Password: $beastPassword"
    Write-Host 'Website:  http://localhost:5080/account/login'
    Write-Host 'Copy the password before closing this window. It is not saved to a text file.'
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
} finally {
    $beastPassword=$null
    $beastBytes=$null
}

