$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'VoxCPM-Setup.ps1')
$script:checks = 0
function Assert($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Hardware([string]$Cuda, [double[]]$Caps = @(8.6)) {
    $version = $null
    if ($Cuda) { $version = [version]$Cuda }
    [pscustomobject]@{ CudaVersion = $version; Capabilities = $Caps }
}
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    Assert ($null -ne $failure -and $failure.ToString() -like "*$Pattern*") "Expected $Pattern, got $failure"
}
Assert ((Resolve-VoxCpmBackend cpu $null) -eq 'cpu') 'Explicit CPU must not require NVIDIA detection.'
Assert ((Resolve-VoxCpmBackend auto (Hardware '')) -eq 'cpu') 'No NVIDIA detection should select CPU.'
foreach ($cuda in @('12.6', '12.7')) {
    Assert ((Resolve-VoxCpmBackend auto (Hardware $cuda)) -eq 'cu126') "Wrong backend for driver CUDA $cuda"
}
foreach ($cuda in @('12.8', '12.9', '13.0', '13.2')) {
    Assert ((Resolve-VoxCpmBackend auto (Hardware $cuda)) -eq 'cu128') "Wrong default for driver CUDA $cuda"
}
Assert ((Resolve-VoxCpmBackend cu126 (Hardware '13.2')) -eq 'cu126') 'Explicit override must win over auto.'
Assert ((Resolve-VoxCpmBackend cu130 (Hardware '13.0')) -eq 'cu130') 'CUDA 13 override must work.'
Assert ((Resolve-VoxCpmBackend auto (Hardware '12.8' @(12.0))) -eq 'cu128') 'RTX 50 must use cu128 or newer.'
Assert ((Resolve-VoxCpmBackend auto (Hardware '13.0' @(6.1))) -eq 'cu126') 'Legacy GPU must not get modern wheels.'
Assert ((Resolve-VoxCpmBackend auto (Hardware '13.0' @(8.6, 6.1))) -eq 'cu126') 'Account for all detected GPU architectures.'
Expect-Failure { Resolve-VoxCpmBackend auto (Hardware '12.4') } 'Update the driver'
Expect-Failure { Resolve-VoxCpmBackend cu128 (Hardware '12.6') } 'needs a driver'
Expect-Failure { Resolve-VoxCpmBackend cu130 (Hardware '12.8') } 'needs a driver'
Expect-Failure { Resolve-VoxCpmBackend cu128 (Hardware '') } 'working NVIDIA driver'
Expect-Failure { Resolve-VoxCpmBackend cu126 (Hardware '13.0' @(12.0)) } 'Blackwell'
Expect-Failure { Resolve-VoxCpmBackend cu128 (Hardware '13.0' @(6.1)) } 'older GPU'
Expect-Failure { Resolve-VoxCpmBackend auto (Hardware '13.0' @(6.1, 12.0)) } 'Mixed old/new'
Expect-Failure { Resolve-VoxCpmBackend auto (Hardware '12.8' @(3.5)) } 'too old'
Expect-Failure { Resolve-VoxCpmBackend auto (Hardware '12.8' @()) } 'unknown'

# Mock the external hardware command, including malformed/failed driver queries.
$global:setupSmiMode = 'ok'
function nvidia-smi {
    $global:LASTEXITCODE = 0
    if ($global:setupSmiMode -eq 'fail') { $global:LASTEXITCODE = 1; return }
    if ($args[0] -eq '-q') {
        return '<nvidia_smi_log><driver_version>570.65</driver_version><cuda_version>12.8</cuda_version></nvidia_smi_log>'
    }
    if ($global:setupSmiMode -eq 'bad') { return 'N/A' }
    if ($global:setupSmiMode -eq 'single') { return '8.6' }
    '8.6'; '12.0'
}
$detected = Get-VoxCpmHardware
Assert ($detected.CudaVersion -eq [version]'12.8' -and $detected.Capabilities.Count -eq 2) 'Hardware query did not parse multiple GPUs.'
$global:setupSmiMode = 'fail'
Expect-Failure { Get-VoxCpmHardware } 'NVIDIA detection failed'
$global:setupSmiMode = 'bad'
Expect-Failure { Get-VoxCpmHardware } 'NVIDIA detection failed'
$global:setupSmiMode = 'ok'

foreach ($backend in @('cpu', 'cu126', 'cu128', 'cu130')) {
    $steps = @(Get-VoxCpmInstallSteps -RepoRoot 'C:\repo with spaces' -Python 'C:\repo with spaces\VoxCPM\.venv\Scripts\python.exe' -Interpreter '3.12' -Backend $backend)
    $sync = $steps[0].Arguments
    Assert (($sync | Where-Object { $_ -eq '--no-install-package' }).Count -eq 2) 'Sync must skip both torch packages.'
    Assert ($sync -contains '--frozen' -and $sync -contains '--inexact') 'Keep lock and existing chosen torch builds.'
    $install = $steps[1].Arguments
    Assert ($install -contains "torch==2.10.0+$backend" -and $install -contains "torchaudio==2.10.0+$backend") 'Version/flavour pairing is wrong.'
    Assert ($install -contains "https://download.pytorch.org/whl/$backend") 'Wrong wheel index.'
    Assert ($install[3] -eq 'C:\repo with spaces\VoxCPM\.venv\Scripts\python.exe') 'Python path must remain one argument.'
    Assert ($steps[3].Arguments[1] -eq 'check') 'Installation must end with a dependency check.'
}

$fresh = @(Get-VoxCpmInstallSteps -RepoRoot 'C:\repo' -Python 'C:\repo\python.exe' -Interpreter 'cpython-3.12-windows-x86_64-none' -Backend cpu)
Assert ($fresh[0].Arguments -contains '--managed-python') 'Fresh installs must avoid system/Windows Store aliases.'

# CheckOnly must execute no uv command, even if uv is installed on this machine.
function uv { throw 'CheckOnly unexpectedly invoked uv' }
$setup = Join-Path (Split-Path $PSScriptRoot -Parent) 'Setup-VoxCPM.ps1'
& $setup -CpuOnly -CheckOnly
$global:setupSmiMode = 'single'
& $setup -TorchBackend cu126 -CheckOnly
Expect-Failure { & $setup -CpuOnly -TorchBackend cu128 -CheckOnly } 'cannot be combined'

# A failure at any install stage must stop later stages and restore process state.
$global:setupUvCalls = [Collections.Generic.List[string[]]]::new()
$global:setupFailAt = 1
function uv {
    $global:setupUvCalls.Add([string[]]$args)
    $global:LASTEXITCODE = 0
    if ($global:setupUvCalls.Count -eq $global:setupFailAt) { $global:LASTEXITCODE = 7 }
}
$cacheBefore = $env:UV_CACHE_DIR
$projectBefore = $env:UV_PROJECT_ENVIRONMENT
$locationBefore = (Get-Location).Path
foreach ($stage in 1..4) {
    $global:setupUvCalls.Clear()
    $global:setupFailAt = $stage
    Expect-Failure { & $setup -CpuOnly } 'failed (exit 7)'
    Assert ($global:setupUvCalls.Count -eq $stage) 'Installer continued after a failed stage.'
    Assert ($env:UV_CACHE_DIR -eq $cacheBefore -and $env:UV_PROJECT_ENVIRONMENT -eq $projectBefore) 'Installer leaked environment variables.'
    Assert ((Get-Location).Path -eq $locationBefore) 'Installer leaked its working directory.'
}
Write-Host "PASS: $script:checks assertions covering hardware selection, driver failures, wheel pairing, paths and read-only preflight."
