param([switch]$Docker,[switch]$OpenBrowser)
$ErrorActionPreference='Stop'
$beastRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $beastRoot
$env:DOCKER_CONFIG=Join-Path $beastRoot '.local/docker'
$beastEnvPath=Join-Path $beastRoot '.env'
if(!(Test-Path -LiteralPath $beastEnvPath)) {
    $beastBytes=New-Object byte[] 24
    $beastRng=[System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $beastRng.GetBytes($beastBytes) } finally { $beastRng.Dispose() }
    $beastPassword=([BitConverter]::ToString($beastBytes)).Replace('-','')
    [System.IO.File]::WriteAllText($beastEnvPath,"BEAST_DB_PASSWORD=$beastPassword")
}
$beastPasswordLine=Get-Content -LiteralPath $beastEnvPath | Where-Object { $_ -match '^BEAST_DB_PASSWORD=' } | Select-Object -First 1
if(!$beastPasswordLine){throw 'BEAST_DB_PASSWORD is missing in .env'}
$env:BEAST_DB_PASSWORD=$beastPasswordLine.Substring('BEAST_DB_PASSWORD='.Length)
if(!(Get-Command docker -ErrorAction SilentlyContinue)){throw 'Install Docker Desktop with Linux containers first.'}
function Test-BeastDocker {
    try { docker info --format '{{.ServerVersion}}' 2>$null | Out-Null; return $LASTEXITCODE -eq 0 }
    catch { return $false }
}
if(!(Test-BeastDocker)){
    $beastDocker=Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if(!(Test-Path -LiteralPath $beastDocker)){throw 'Start Docker Desktop first.'}
    Start-Process -FilePath $beastDocker -WindowStyle Hidden
    $beastReady=$false
    for($i=0;$i -lt 60;$i++) { Start-Sleep -Seconds 2; if(Test-BeastDocker){$beastReady=$true;break} }
    if(!$beastReady){throw 'Docker Desktop is not ready. Check WSL / Linux containers.'}
}
if($Docker) {
    docker compose up --build -d
    if($LASTEXITCODE -ne 0){throw 'Docker startup failed. Check Docker Desktop and port 5080.'}
    $beastReady=$false
    for($i=0;$i -lt 60;$i++) {
        try { $response=Invoke-WebRequest 'http://127.0.0.1:5080/health' -UseBasicParsing -TimeoutSec 10; if($response.StatusCode -eq 200){$beastReady=$true;break} } catch {}
        Start-Sleep -Seconds 2
    }
    if(!$beastReady){throw 'Site did not become ready. Run: docker compose logs web'}
    Write-Host 'BeastContest: http://localhost:5080'
    if($OpenBrowser){Start-Process 'http://localhost:5080/account/login'}
    exit 0
}
if($OpenBrowser){throw 'Use -Docker with -OpenBrowser, or use Start-BeastContest.bat.'}
docker compose up -d --wait db
if($LASTEXITCODE -ne 0){throw 'PostgreSQL startup failed.'}
$env:ConnectionStrings__Database="Host=localhost;Port=54329;Database=beastcontest;Username=beastcontest;Password=$env:BEAST_DB_PASSWORD"
$env:DOTNET_CLI_HOME=Join-Path $beastRoot '.local/dotnet'
$env:NUGET_PACKAGES=Join-Path $beastRoot '.local/packages'
$env:APPDATA=Join-Path $beastRoot '.local/appdata'
dotnet restore BeastContest.slnx --configfile NuGet.Config
if($LASTEXITCODE -ne 0){throw 'Restore failed.'}
dotnet run --project src/BeastContest.Web --no-restore --launch-profile http


