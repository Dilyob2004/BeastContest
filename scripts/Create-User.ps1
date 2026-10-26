param([string]$Login='',[string]$DisplayName='')
$ErrorActionPreference='Stop'
$beastRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $beastRoot
$env:DOCKER_CONFIG=Join-Path $beastRoot '.local/docker'

try {
    if(!$Login){$Login=Read-Host 'Participant login (Enter = student01)';if(!$Login){$Login='student01'}}
    if($Login -notmatch '^[a-zA-Z0-9_.-]{3,40}$'){throw 'Login must contain 3-40 letters, digits, dots, underscores or hyphens.'}
    if(!$DisplayName){$DisplayName=$Login}
    if($DisplayName.Length -gt 80){throw 'Display name must be at most 80 characters.'}
    if(!(Get-Command docker -ErrorAction SilentlyContinue)){throw 'Install Docker Desktop and run Start-BeastContest.bat first.'}
    $beastDirectory=Join-Path $beastRoot 'credentials'
    New-Item -ItemType Directory -Path $beastDirectory -Force | Out-Null
    $beastFile=Join-Path $beastDirectory ('participant-'+$Login+'-'+[Guid]::NewGuid().ToString('N')+'.txt')
    # Reserve the output before creating the account; never overwrite an existing file.
    $beastStream=[System.IO.File]::Open($beastFile,[System.IO.FileMode]::CreateNew,[System.IO.FileAccess]::Write,[System.IO.FileShare]::None)
    $beastBytes=New-Object byte[] 6
    $beastAlphabet='ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $beastRng=[System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        do {
            $beastRng.GetBytes($beastBytes)
            $beastPassword=-join ($beastBytes | ForEach-Object { $beastAlphabet[$_ -band 31] })
        } while (($beastPassword.ToCharArray() | Select-Object -Unique).Count -lt 3)
    } finally {$beastRng.Dispose()}
    $beastPassword | docker compose exec -T web dotnet BeastContest.Web.dll --create-user $Login --role Participant --name $DisplayName
    if($LASTEXITCODE -ne 0){throw 'Account was not created. Check that the server is running and the login is not already taken. Existing accounts are unchanged.'}
    $beastCreated=$true
    $beastText="BeastContest - Participant`r`nName: $DisplayName`r`nLogin: $Login`r`nPassword: $beastPassword`r`nWebsite: http://localhost:5080/account/login`r`n`r`nThis address works on the server computer. LAN access is not configured yet.`r`nKeep this file private and give it only to this participant.`r`n"
    $beastWriter=New-Object System.IO.StreamWriter($beastStream,(New-Object System.Text.UTF8Encoding($true)))
    $beastWriter.Write($beastText)
    $beastWriter.Dispose();$beastWriter=$null;$beastStream=$null
    Write-Host "Participant created: $Login" -ForegroundColor Green
    Write-Host "Login details saved to: $beastFile"
    Write-Host 'Open this TXT file to copy the password. Use a private browser window to test the participant account.'
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    if($beastCreated){Write-Host "Account exists, but saving failed. Login: $Login Password: $beastPassword"}
    exit 1
} finally {
    if($beastWriter){$beastWriter.Dispose()}
    if($beastStream){$beastStream.Dispose()}
    if(!$beastCreated -and $beastFile -and (Test-Path -LiteralPath $beastFile)){Remove-Item -LiteralPath $beastFile}
    $beastPassword=$null
}
