# Execute the real installer in isolated fixtures; every external action is mocked.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path $repo ('artifacts/install-flow-tests/' + [Guid]::NewGuid().ToString('N'))
$originalLocal = $env:LOCALAPPDATA
$originalProgram = $env:ProgramFiles
$script:checks = 0
function Assert($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Run-Scenario([string]$Failure, [switch]$NoLaunch) {
    $folder = Join-Path $root $(if ($Failure) { $Failure } else { 'success with spaces' })
    New-Item -ItemType Directory -Path (Join-Path $folder 'tools') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'Install-FF14Voice.ps1') -Destination $folder
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OneClick-Install.ps1') -Destination (Join-Path $folder 'tools')
    Add-Content -LiteralPath (Join-Path $folder 'tools/OneClick-Install.ps1') -Value @'
function Record-Step([string]$Step) {
    $global:installFlowCalls.Add($Step)
    if ($global:installFlowFailure -eq $Step) { throw "mock failure: $Step" }
}
function Update-InstallPath {}
function Test-InstallCommand([string]$Name) { return $Name -ne 'git' -or $global:installFlowGit }
function Get-CppBuildTools { return 'mock VS' }
function Test-Dotnet10 { return $true }
function Install-WingetPackage { Record-Step 'package'; $global:installFlowGit = $true }
function Start-Transcript {}
function Stop-Transcript {}
function Invoke-InstallCommand([string]$Command, [string[]]$Arguments) {
    if ($Command -eq 'dotnet') { Record-Step 'build' } else { Record-Step 'python-check' }
}
function Initialize-InstallSource { Record-Step 'source' }
'@
    Set-Content (Join-Path $folder 'tools/VoxCPM-Setup.ps1') @'
function Get-VoxCpmHardware { [pscustomobject]@{CudaVersion=[version]'12.9'; Capabilities=@(7.5)} }
'@
    Set-Content (Join-Path $folder 'Setup-VoxCPM.ps1') @'
param([switch]$CpuOnly)
Record-Step 'python-setup'
if (!$CpuOnly) { throw 'Fresh GGUF gateway must request CPU PyTorch.' }
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts') -Force | Out-Null
Set-Content (Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts/python.exe') 'not executable: mocked'
'@
    Set-Content (Join-Path $folder 'Setup-VoxCPM-GGUF.ps1') @'
param([string]$CudaPath)
Record-Step 'gguf-setup'
if ($CudaPath -notlike '*v12.9') { throw 'Must pin CUDA 12.9.' }
'@
    Set-Content (Join-Path $folder 'Start-VoxCPM-GGUF.ps1') @'
param([switch]$VerifyOnly, [switch]$TestSpeech, [switch]$LaunchApp)
if ($VerifyOnly) {
    Record-Step 'speech-test'
    if (!$TestSpeech) { throw 'Installer must generate audio before claiming readiness.' }
} elseif ($LaunchApp) { Record-Step 'launch' } else { throw 'Unexpected launcher invocation.' }
'@
    Set-Content (Join-Path $folder 'tools/Restore-GameResources.ps1') @'
param([string]$SqPack, [switch]$CheckOnly)
if ($CheckOnly) { Record-Step 'resource-check'; return }
Record-Step 'extract'
$resources = Join-Path (Split-Path -Parent $PSScriptRoot) 'data/game-voices'
New-Item -ItemType Directory -Path $resources -Force | Out-Null
Set-Content (Join-Path $resources 'catalog.json') '{"Voices":[{"References":[{"RelativePath":"voice.wav"}]}],"Errors":[]}'
Set-Content (Join-Path $resources 'native-voiced-lines.json') '{"Version":1,"Lines":[{"Text":"hello"}]}'
Set-Content (Join-Path $resources 'npc-voice-index.json') '[{"Key":"npc:1"}]'
Set-Content (Join-Path $resources 'npc-voices.csv') 'mock CSV'
$wav = [byte[]]::new(100)
[Text.Encoding]::ASCII.GetBytes('RIFF').CopyTo($wav, 0)
[Text.Encoding]::ASCII.GetBytes('WAVE').CopyTo($wav, 8)
[IO.File]::WriteAllBytes((Join-Path $resources 'voice.wav'), $wav)
'@
    $sqpack = Join-Path $folder 'game/sqpack'
    New-Item -ItemType Directory -Path (Join-Path $sqpack 'ffxiv') -Force | Out-Null
    if (!(Test-Path (Join-Path $sqpack 'ffxiv/test.index'))) { Set-Content (Join-Path $sqpack 'ffxiv/test.index') 'game index' }
    $env:LOCALAPPDATA = Join-Path $folder 'user profile'
    $env:ProgramFiles = Join-Path $folder 'Program Files'
    $cuda = Join-Path $env:ProgramFiles 'NVIDIA GPU Computing Toolkit/CUDA/v12.9/bin'
    New-Item -ItemType Directory -Path $cuda -Force | Out-Null
    Set-Content (Join-Path $cuda 'nvcc.exe') 'not executable: mocked'
    $global:installFlowCalls = [Collections.Generic.List[string]]::new()
    $global:installFlowFailure = $Failure
    $global:installFlowGit = $false
    $caught = $null
    try { & (Join-Path $folder 'Install-FF14Voice.ps1') -SqPack $sqpack -NoLaunch:$NoLaunch | Out-Null } catch { $caught = $_ }
    if ($Failure) {
        Assert ($caught -and $caught.ToString() -like "*mock failure: $Failure*") "Unexpected failure: $caught"
        Assert ($global:installFlowCalls[-1] -eq $Failure) 'Failed stages must stop all subsequent work.'
        Assert (!(Test-Path (Join-Path $env:LOCALAPPDATA 'FF14Sound/settings.json'))) 'A failed install must not change app settings.'
        if ($Failure -eq 'extract') { Assert (!(Test-Path (Join-Path $folder 'artifacts/install/resources.json'))) 'Interrupted extraction must not be marked complete.' }
    } else {
        if ($caught) { throw $caught }
        Assert ($global:installFlowCalls.Contains('speech-test')) 'Successful installation must verify generated audio.'
        Assert ($global:installFlowCalls.Contains('launch') -eq !$NoLaunch) 'NoLaunch must control only the final app launch.'
        Assert ((Get-Content (Join-Path $env:LOCALAPPDATA 'FF14Sound/settings.json') -Raw | ConvertFrom-Json).TranslationEngine -eq 'google') 'First run must have a usable translation provider.'
    }
}
try {
    foreach ($failure in @('package', 'source', 'python-setup', 'gguf-setup', 'extract', 'build', 'speech-test')) { Run-Scenario $failure }
    Run-Scenario ''
    Assert ($global:installFlowCalls.IndexOf('extract') -lt $global:installFlowCalls.IndexOf('build')) 'Resources must precede app build.'
    Assert ($global:installFlowCalls.IndexOf('speech-test') -lt $global:installFlowCalls.IndexOf('launch')) 'Speech verification must precede launch.'
    Run-Scenario '' -NoLaunch
    Assert (!$global:installFlowCalls.Contains('extract')) 'Rerun must reuse unchanged validated resources.'
    Assert (!$global:installFlowCalls.Contains('python-setup')) 'Rerun must preserve existing Python environment.'
    Write-Host "PASS: $script:checks installer orchestration checks. Fixtures: $root"
} finally {
    $env:LOCALAPPDATA = $originalLocal
    $env:ProgramFiles = $originalProgram
    Remove-Variable installFlowCalls, installFlowFailure, installFlowGit -Scope Global -ErrorAction SilentlyContinue
}
