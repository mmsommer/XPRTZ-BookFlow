param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("bookflow-verify-" + [Guid]::NewGuid().ToString('N'))
$processes = @()
$environment = @{}
function Set-TestEnvironment($name, $value) {
    if (-not $environment.ContainsKey($name)) {
        $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}
function Free-Port {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
}
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    if (-not $SkipBuild) {
        dotnet build (Join-Path $src 'BookFlow.slnx') --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $bytes = New-Object byte[] 48
        $random.GetBytes($bytes)
        Set-TestEnvironment 'Authentication__SigningKey' ([Convert]::ToBase64String($bytes))
        $random.GetBytes($bytes)
        $providerToken = [Convert]::ToBase64String($bytes)
    }
    finally { $random.Dispose() }
    $frontendPort = Free-Port
    $providersPort = Free-Port
    $webappPort = Free-Port
    $notificationsPort = Free-Port
    Set-TestEnvironment 'ASPNETCORE_ENVIRONMENT' 'Development'
    Set-TestEnvironment 'BookFlow__Mode' 'Local'
    Set-TestEnvironment 'ConnectionStrings__BookFlow' "Data Source=$scratch\bookflow.db;Default Timeout=30"
    Set-TestEnvironment 'BookFlow__BlobRoot' (Join-Path $scratch 'blobs')
    Set-TestEnvironment 'BookFlow__FrontendUrl' "http://localhost:$frontendPort/"
    Set-TestEnvironment 'BookFlow__WebAppUrl' "http://localhost:$webappPort/"
    Set-TestEnvironment 'BookFlow__NotificationsUrl' "http://localhost:$notificationsPort/"
    Set-TestEnvironment 'BookFlow__PublicUrl' "http://localhost:$frontendPort"
    Set-TestEnvironment 'BookFlow__DemoTenant' '11111111-1111-1111-1111-111111111111'
    Set-TestEnvironment 'BookFlow__ListingHosts__0' 'shop.example.test'
    Set-TestEnvironment 'Providers__BaseUrl' "http://localhost:$providersPort/"
    Set-TestEnvironment 'Providers__Token' $providerToken
    Set-TestEnvironment 'Providers__StoreRoot' (Join-Path $scratch 'providers')
    Set-TestEnvironment 'Logging__LogLevel__Microsoft.EntityFrameworkCore' 'Warning'
    Set-TestEnvironment 'Logging__LogLevel__Default' 'Warning'
    Set-TestEnvironment 'BookFlow__InitializeDatabase' 'true'
    Set-TestEnvironment 'BookFlow__InitializeOnly' 'true'
    $webappDll = Join-Path $src 'BookFlow.WebApp\bin\Debug\net10.0\BookFlow.WebApp.dll'
    & dotnet $webappDll
    if ($LASTEXITCODE -ne 0) { throw 'Database initialization failed.' }
    Set-TestEnvironment 'BookFlow__InitializeDatabase' 'false'
    Set-TestEnvironment 'BookFlow__InitializeOnly' 'false'
    $applications = @(
        @{ Name = 'LocalProviders'; Port = $providersPort },
        @{ Name = 'WebApp'; Port = $webappPort },
        @{ Name = 'Notifications'; Port = $notificationsPort },
        @{ Name = 'ApiFrontend'; Port = $frontendPort },
        @{ Name = 'EditorAssignment'; Port = (Free-Port) },
        @{ Name = 'ProofprintGeneration'; Port = (Free-Port) },
        @{ Name = 'ProgressTracking'; Port = (Free-Port) },
        @{ Name = 'PrintDemandOrders'; Port = (Free-Port) },
        @{ Name = 'Inventory'; Port = (Free-Port) }
    )
    foreach ($application in $applications) {
        $project = Join-Path $src ("BookFlow." + $application.Name)
        $dll = Join-Path $project ("bin\Debug\net10.0\BookFlow." + $application.Name + ".dll")
        $process = Start-Process dotnet -ArgumentList @($dll, '--urls', "http://127.0.0.1:$($application.Port)") `
            -WorkingDirectory $project -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $scratch ($application.Name + '.out.log')) `
            -RedirectStandardError (Join-Path $scratch ($application.Name + '.err.log'))
        $processes += $process
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ($process.HasExited) { throw "Application $($application.Name) exited during startup." }
            try {
                Invoke-RestMethod "http://127.0.0.1:$($application.Port)/health/live" | Out-Null
                $ready = $true
                break
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { throw "Application $($application.Name) never became responsive." }
    }
    Invoke-RestMethod "http://localhost:$frontendPort/openapi/v1.json" | Out-Null
    Invoke-WebRequest "http://localhost:$frontendPort/" -UseBasicParsing | Out-Null
    & (Join-Path $src 'Smoke-Test.ps1') -FrontendUrl "http://localhost:$frontendPort" `
        -ProvidersUrl "http://localhost:$providersPort" -ProviderToken $providerToken
    $originalWebApp = $processes[1]
    Stop-Process -Id $originalWebApp.Id
    $originalWebApp.WaitForExit()
    $restarted = Start-Process dotnet -ArgumentList @($webappDll, '--urls', "http://127.0.0.1:$webappPort") `
        -WorkingDirectory (Join-Path $src 'BookFlow.WebApp') -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $scratch 'WebApp.restarted.out.log') `
        -RedirectStandardError (Join-Path $scratch 'WebApp.restarted.err.log')
    $processes += $restarted
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($restarted.HasExited) { throw 'Restarted WebApp exited.' }
        try {
            Invoke-RestMethod "http://localhost:$webappPort/health/ready" | Out-Null
            $ready = $true
            break
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'Restarted WebApp never became ready.' }
    $authorToken = (Invoke-RestMethod "http://localhost:$providersPort/tokens/Author" `
        -Headers @{ Authorization = "Bearer $providerToken" }).token
    $persisted = Invoke-RestMethod "http://localhost:$frontendPort/api/manuscripts" `
        -Headers @{ Authorization = "Bearer $authorToken" }
    if (@($persisted).Count -ne 1 -or $persisted[0].status -ne 'Approved') {
        throw 'Approved manuscript state was not retained across process restart.'
    }
    if (-not (Test-Path (Join-Path $scratch 'bookflow.db'))) { throw 'No persistent database was created.' }
    if (@(Get-ChildItem (Join-Path $scratch 'blobs') -Recurse -Filter '*.pdf').Count -ne 3) {
        throw 'Expected exactly three persisted proofprint PDFs.'
    }
    Write-Output 'Full-process verification passed: all nine applications responded, OpenAPI loaded, and workflow data/files survived WebApp restart.'
}
catch {
    Write-Output ("Verification failed: " + $_.Exception.Message)
    foreach ($log in Get-ChildItem $scratch -Filter '*.log') {
        Write-Output ("--- " + $log.Name)
        Get-Content $log.FullName -Tail 15
    }
    throw
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
        $process.WaitForExit()
        $process.Dispose()
    }
    foreach ($entry in $environment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    if (Test-Path $scratch) {
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            try {
                Remove-Item -LiteralPath $scratch -Recurse -Force
                break
            }
            catch [IO.IOException] {
                if ($attempt -eq 9) { throw }
                Start-Sleep -Milliseconds 200
            }
        }
    }
}
