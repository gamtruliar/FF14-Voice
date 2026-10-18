param(
    [switch]$CpuOnly,
    [ValidateSet('auto', 'cpu', 'cu126', 'cu128', 'cu130')][string]$TorchBackend = 'auto',
    [ValidateSet('3.10', '3.11', '3.12')][string]$PythonVersion = '3.12',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools/VoxCPM-Setup.ps1')
if ($CpuOnly -and $TorchBackend -notin @('auto', 'cpu')) {
    throw '-CpuOnly cannot be combined with a CUDA -TorchBackend.'
}
if (![Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT' -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') {
    throw 'This installer supports Windows x64. Use the upstream instructions for other platforms.'
}
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'VoxCPM/uv.lock'))) {
    throw 'VoxCPM submodule is missing. Run: git submodule update --init --recursive'
}
if ($CpuOnly) { $TorchBackend = 'cpu' }
$hardware = $null
if ($TorchBackend -ne 'cpu') {
    $hardware = Get-VoxCpmHardware
    Write-Host $hardware.Description
}
$backend = Resolve-VoxCpmBackend -Backend $TorchBackend -Hardware $hardware
$python = Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts/python.exe'
$interpreter = "cpython-$PythonVersion-windows-x86_64-none"
if (Test-Path -LiteralPath $python) {
    $pythonInfo = @(& $python -c 'import sys, struct; print(str(sys.version_info.major) + chr(46) + str(sys.version_info.minor)); print(struct.calcsize(chr(80)) * 8)')
    if ($LASTEXITCODE -ne 0 -or $pythonInfo.Count -ne 2 -or $pythonInfo[0] -notin @('3.10', '3.11', '3.12') -or $pythonInfo[1] -ne '64') {
        throw 'Existing VoxCPM/.venv must use 64-bit Python 3.10-3.12. Move it aside and rerun setup to create a supported environment.'
    }
    $version = $pythonInfo[0]
    if ($PSBoundParameters.ContainsKey('PythonVersion') -and $version -ne $PythonVersion) {
        throw "Existing environment uses Python $version. Move it aside before selecting Python $PythonVersion."
    }
    $interpreter = $python
}
Write-Host "Selected PyTorch 2.10.0 / torchaudio 2.10.0: $backend"
if ($backend -eq 'cpu') {
    Write-Host 'CPU-only installation. AMD/Intel GPU acceleration is not configured by this installer; CPU generation can be slow.'
}
$steps = @(Get-VoxCpmInstallSteps -RepoRoot $PSScriptRoot -Python $python -Interpreter $interpreter -Backend $backend)
if (!(Get-Command uv -ErrorAction SilentlyContinue)) { throw 'Install uv and reopen your terminal.' }
if ($CheckOnly) {
    foreach ($step in $steps) { Write-Host ("{0}: uv {1}" -f $step.Name, ($step.Arguments -join ' ')) }
    Write-Host 'CheckOnly: no packages installed or changed.'
    return
}
$previousCache = $env:UV_CACHE_DIR
$previousProjectEnvironment = $env:UV_PROJECT_ENVIRONMENT
$env:UV_CACHE_DIR = Join-Path $PSScriptRoot '.cache/uv'
$env:UV_PROJECT_ENVIRONMENT = Join-Path $PSScriptRoot 'VoxCPM/.venv'
Push-Location (Join-Path $PSScriptRoot 'VoxCPM')
try {
    foreach ($step in $steps) {
        Write-Host ("Installing/checking: {0}" -f $step.Name)
        $arguments = $step.Arguments
        & uv @arguments
        if ($LASTEXITCODE -ne 0) { throw "$($step.Name) failed (exit $LASTEXITCODE). Close the backend before rerunning setup. The environment may be incomplete; rerun the same command after fixing the error." }
    }
    & $python (Join-Path $PSScriptRoot 'tools/Test-VoxCPMRuntime.py') --backend $backend
    if ($LASTEXITCODE -ne 0) { throw 'Runtime validation failed. See the error above; check the NVIDIA driver / GPU support, or rerun with -CpuOnly.' }
    Write-Host 'Setup complete. Start with: .\Start-VoxCPM.cmd --device auto'
} finally {
    Pop-Location
    $env:UV_CACHE_DIR = $previousCache
    $env:UV_PROJECT_ENVIRONMENT = $previousProjectEnvironment
}
