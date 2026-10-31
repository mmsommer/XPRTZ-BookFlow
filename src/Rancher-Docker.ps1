$ErrorActionPreference = 'Stop'
$DockerArguments = $args
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Rancher Desktop\resources\resources\win32\bin\docker.exe'),
    (Join-Path $env:ProgramFiles 'Rancher Desktop\resources\resources\win32\bin\docker.exe'),
    (Join-Path $env:USERPROFILE '.rd\bin\docker.exe')
)
$docker = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $docker) {
    throw 'Rancher Desktop Docker CLI was not found. Install Rancher Desktop and select dockerd (moby) as its container engine.'
}
if ($env:DOCKER_HOST -or $env:DOCKER_CONTEXT) {
    throw 'Remove DOCKER_HOST and DOCKER_CONTEXT overrides from this terminal before targeting Rancher Desktop.'
}
if (-not $DockerArguments) {
    $DockerArguments = @('info')
}
& $docker --context default @DockerArguments
exit $LASTEXITCODE
