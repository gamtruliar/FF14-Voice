param([string]$SqPack = $env:FF14_SQPACK, [switch]$CheckOnly, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools/OneClick-Install.ps1')
. (Join-Path $PSScriptRoot 'tools/VoxCPM-Setup.ps1')
if ($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitProcess -or
    $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or [Environment]::OSVersion.Version.Build -lt 19045) {
    throw 'This experimental installer requires Windows 10 22H2 / Windows 11, x64.'
}
Write-Host 'FF14 Voice - experimental Windows GGUF installer'
Write-Host 'Installs missing build/runtime tools, downloads VoxCPM2 Q8, extracts your installed FF14 resources and starts the app.'
Write-Host 'GTX 1660 Super + FF14 performance is not yet verified. First setup needs internet, time and substantial free disk space.'
Write-Host 'Windows may ask for administrator approval for dependencies. No WSL is needed for this backend.'
if ($CheckOnly) {
    foreach ($name in @('winget', 'git', 'uv', 'ffmpeg', 'cmake', 'nvcc', 'nvidia-smi')) {
        Write-Host ("{0}: {1}" -f $name, $(if (Test-InstallCommand $name) { 'found' } else { 'missing' }))
    }
    Write-Host ".NET 10 SDK: $(Test-Dotnet10); VS 2022 C++ tools: $([bool](Get-CppBuildTools))"
    Write-Host 'CheckOnly: no packages, files or settings changed.'
    return
}
$logs = Join-Path $PSScriptRoot 'artifacts/install'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
Start-Transcript -Path (Join-Path $logs ('install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')) | Out-Null
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Update-InstallPath
    Assert-GgufHardware (Get-VoxCpmHardware)
    $appPath = Join-Path $PSScriptRoot 'src/FF14Sound/bin/Release/net10.0-windows/FF14Sound.exe'
    if (@(Get-Process -Name FF14Sound -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appPath }).Count) {
        throw 'Close this copy of FF14 Voice before installing, then rerun. Your running app will not be stopped automatically.'
    }
    if (!(Test-InstallCommand winget)) {
        Write-Host 'Installing WinGet for the current Windows user...'
        Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Scope CurrentUser -Force | Out-Null
        Install-Module -Name Microsoft.WinGet.Client -Scope CurrentUser -Repository PSGallery -Force -AllowClobber
        Import-Module Microsoft.WinGet.Client
        Repair-WinGetPackageManager -Latest | Out-Null
        Update-InstallPath
        if (!(Test-InstallCommand winget)) { throw 'WinGet is not available yet. Sign out/in and rerun this installer.' }
    }
    foreach ($package in @(
        @{Command='git'; Id='Git.Git'}, @{Command='uv'; Id='astral-sh.uv'},
        @{Command='ffmpeg'; Id='Gyan.FFmpeg'}, @{Command='cmake'; Id='Kitware.CMake'}
    )) {
        if (!(Test-InstallCommand $package.Command)) { Install-WingetPackage $package.Id }
        if (!(Test-InstallCommand $package.Command)) { throw "$($package.Command) is unavailable after installation. Reopen the installer." }
    }
    if (!(Test-Dotnet10)) { Install-WingetPackage 'Microsoft.DotNet.SDK.10' }
    if (!(Test-Dotnet10)) { throw '.NET 10 SDK is still unavailable.' }
    if (!(Get-CppBuildTools)) {
        Install-WingetPackage 'Microsoft.VisualStudio.2022.BuildTools' -Override '--wait --passive --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended'
    }
    if (!(Get-CppBuildTools)) { throw 'Visual Studio C++ tools are still unavailable. Complete any requested restart and rerun.' }
    $cudaPath = Join-Path $env:ProgramFiles 'NVIDIA GPU Computing Toolkit/CUDA/v12.9'
    if (!(Test-Path -LiteralPath (Join-Path $cudaPath 'bin/nvcc.exe'))) {
        # Only the compiler/runtime/build integration; do not replace the user's display driver.
        Install-WingetPackage 'Nvidia.CUDA' -Version '12.9' -Override '-y -gm2 -s -n nvcc_12.9 cudart_12.9 cublas_12.9 cublas_dev_12.9 thrust_12.9 visual_studio_integration_12.9'
    }

    # Also supports GitHub Download ZIP, which does not contain submodule files.
    Initialize-InstallSource $PSScriptRoot 'VoxCPM' 'https://github.com/OpenBMB/VoxCPM.git' 'cce58a5b59303c9bd63d12f23afe6a49a4a80c59' 'uv.lock'
    Initialize-InstallSource $PSScriptRoot 'TataruHelper' 'https://github.com/NightlyRevenger/TataruHelper.git' '6269051a05006b43b2b8ed15723c3fe7cdc1060d' 'Translation/Translation.csproj'
    $python = Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts/python.exe'
    if (!(Test-Path -LiteralPath $python)) {
        & (Join-Path $PSScriptRoot 'Setup-VoxCPM.ps1') -CpuOnly
    } else {
        # Keep a user's existing CPU/CUDA environment; don't replace its torch build.
        Invoke-InstallCommand uv @('pip', 'check', '--python', $python)
        Invoke-InstallCommand $python @('-c', 'import torch, gradio, httpx, soundfile, voxcpm')
    }
    & (Join-Path $PSScriptRoot 'Setup-VoxCPM-GGUF.ps1') -CudaPath $cudaPath
    if (!$SqPack) {
        foreach ($relative in @('SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/sqpack', 'Steam/steamapps/common/FINAL FANTASY XIV Online/game/sqpack')) {
            $candidate = Join-Path ${env:ProgramFiles(x86)} $relative
            if (@(Get-ChildItem -LiteralPath (Join-Path $candidate 'ffxiv') -Filter '*.index' -File -ErrorAction SilentlyContinue).Count) { $SqPack = $candidate; break }
        }
    }
    if (!$SqPack) { $SqPack = Read-Host 'Path to your international FF14 game\sqpack directory' }
    $SqPack = (Resolve-Path -LiteralPath $SqPack.Trim('"')).ProviderPath
    & (Join-Path $PSScriptRoot 'tools/Restore-GameResources.ps1') -SqPack $SqPack -CheckOnly
    $fingerprint = Get-ResourceFingerprint $SqPack
    $resourceFolder = Join-Path $PSScriptRoot 'data/game-voices'
    Invoke-InstallStage (Join-Path $logs 'resources.json') $fingerprint {
        Test-InstalledResources $resourceFolder
    } {
        & (Join-Path $PSScriptRoot 'tools/Restore-GameResources.ps1') -SqPack $SqPack
    }
    Invoke-InstallCommand dotnet @('build', (Join-Path $PSScriptRoot 'src/FF14Sound/FF14Sound.csproj'), '-c', 'Release')
    & (Join-Path $PSScriptRoot 'Start-VoxCPM-GGUF.ps1') -VerifyOnly -TestSpeech
    Write-Host 'First-time users start with Google Translate (dialogue is sent to Google; no API key needed). You can switch providers in the app. Existing settings are preserved.'
    Initialize-FirstRunSettings (Join-Path $env:LOCALAPPDATA 'FF14Sound/settings.json')
    Write-Host 'Model, gateway and resources verified. In the app, select your game language and translation provider, then connect to FF14.'
    Write-Host 'Google Translate requires no API key; Ollama and Gemini need your own configuration.'
} finally { Stop-Transcript | Out-Null }
if (!$NoLaunch) {
    & (Join-Path $PSScriptRoot 'Start-VoxCPM-GGUF.ps1') -LaunchApp
}
