$ErrorActionPreference = 'Stop'
$envPath = Join-Path $PSScriptRoot '.env'
if (Test-Path $envPath) {
    throw 'A local .env already exists. Keep it to preserve the current local authentication configuration.'
}
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $signingBytes = New-Object byte[] 48
    $providerBytes = New-Object byte[] 32
    $random.GetBytes($signingBytes)
    $random.GetBytes($providerBytes)
    $signingKey = [Convert]::ToBase64String($signingBytes)
    $providerToken = [Convert]::ToBase64String($providerBytes)
}
finally { $random.Dispose() }
@"
BOOKFLOW_SIGNING_KEY=$signingKey
BOOKFLOW_PROVIDER_TOKEN=$providerToken
"@ | Set-Content $envPath -Encoding ascii
Write-Output 'Created ignored local credentials in src\.env. Run Rancher-Docker.ps1 compose up --build -d from src.'
