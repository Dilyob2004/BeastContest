param([Parameter(Mandatory=$true)][string]$Login,[ValidateSet('Admin','Participant')][string]$Role='Participant',[string]$DisplayName='')
$ErrorActionPreference='Stop'
$beastRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $beastRoot
$env:DOCKER_CONFIG=Join-Path $beastRoot '.local/docker'
if(!$DisplayName){$DisplayName=$Login}
docker compose exec web dotnet BeastContest.Web.dll --create-user $Login --role $Role --name $DisplayName
if($LASTEXITCODE -ne 0){throw 'Account creation failed. Start the server first and check the message above.'}

