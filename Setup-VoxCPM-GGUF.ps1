param(
    [string]$RuntimeRoot = (Join-Path $PSScriptRoot '.cache/gguf'),
    [string]$CudaPath = (Join-Path $env:ProgramFiles 'NVIDIA GPU Computing Toolkit/CUDA/v12.9'),
    [switch]$CheckOnly,
    [ValidateRange(1, 32)][int]$BuildJobs = 4
)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tools/gguf/manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (![Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') { throw 'Windows x64 is required.' }
foreach ($name in @('git', 'cmake')) {
    if (!(Get-Command $name -ErrorAction SilentlyContinue)) { throw "Missing $name. See docs/gguf.md for the build prerequisites." }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 2022 C++ Build Tools are required.' }
$vs = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Install the Visual Studio 2022 Desktop development with C++ workload and Windows SDK.' }
$cuda = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CudaPath)
if (!(Test-Path -LiteralPath (Join-Path $cuda 'bin/nvcc.exe'))) { throw "CUDA Toolkit 12.9 is required at $cuda. Use Install-FF14Voice.cmd to install it, or pass -CudaPath." }
$versionText = & (Join-Path $cuda 'bin/nvcc.exe') --version
if ($LASTEXITCODE -ne 0 -or ($versionText -join ' ') -notmatch 'release 12\.9,') { throw 'This pinned GGUF build requires CUDA Toolkit 12.9 (including GTX 16 support).' }
$python = Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts/python.exe'
if (!(Test-Path -LiteralPath $python)) { throw 'Run Setup-VoxCPM.cmd -CpuOnly first to install the Windows gateway.' }
$RuntimeRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RuntimeRoot)
$engine = Join-Path $RuntimeRoot 'engine'
$build = Join-Path $RuntimeRoot 'build'
$models = Join-Path $RuntimeRoot 'models'
Write-Host "GGUF runtime: $RuntimeRoot"
Write-Host "Engine revision: $($manifest.engine_revision)"
Write-Host "Q8 BaseLM + F16 Acoustic: 3.55 GB download; runtime VRAM must be measured separately."
Write-Host "CUDA toolkit: $cuda"
if ($CheckOnly) { Write-Host 'CheckOnly: no changes made.'; return }
function Run-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE). Fix the error and rerun setup." }
}
New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null
if (!(Test-Path -LiteralPath (Join-Path $engine '.git'))) {
    if ((Test-Path -LiteralPath $engine) -and @(Get-ChildItem -LiteralPath $engine -Force).Count) { throw "Unrecognized engine folder: $engine. Move it aside before rerunning." }
    Run-Checked git @('init', $engine)
    Run-Checked git @('-C', $engine, 'remote', 'add', 'origin', $manifest.engine_repository)
}
$revision = & git -C $engine rev-parse --verify --quiet HEAD
if ($LASTEXITCODE -ne 0 -or $revision -ne $manifest.engine_revision) {
    $changes = & git -C $engine status --porcelain
    if ($LASTEXITCODE -ne 0 -or $changes) { throw "Engine directory is not a clean checkout: $engine. Move it aside before rerunning." }
    Run-Checked git @('-C', $engine, 'fetch', '--depth', '1', $manifest.engine_repository, $manifest.engine_revision)
    Run-Checked git @('-C', $engine, 'checkout', '--detach', $manifest.engine_revision)
}
$changes = & git -C $engine status --porcelain
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'The cached engine has local changes; move it aside before installing the pinned build.' }
Run-Checked cmake @('-S', $engine, '-B', $build, '-G', 'Visual Studio 17 2022', '-A', 'x64', '-T', "cuda=$cuda",
    ('-DCMAKE_PROJECT_TOP_LEVEL_INCLUDES=' + (Join-Path $PSScriptRoot 'tools/gguf/windows-compat.cmake').Replace('\', '/')),
    '-DGGML_CUDA=ON', '-DCMAKE_CUDA_ARCHITECTURES=75;86', '-DGGML_NATIVE=OFF', '-DGGML_CCACHE=OFF',
    '-DLLAMA_OPENSSL=OFF', '-DLLAMA_BUILD_TESTS=OFF', '-DLLAMA_BUILD_EXAMPLES=OFF', '-DLLAMA_BUILD_UI=OFF',
    '-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /W3 /GR /EHsc /utf-8',
    '-DCMAKE_C_FLAGS=/DWIN32 /D_WINDOWS /W3 /utf-8', '-DCMAKE_CUDA_FLAGS=-Xcompiler=/utf-8')
Run-Checked cmake @('--build', $build, '--config', 'Release', '--target', 'llama-tts-server', '--parallel', "$BuildJobs")
Run-Checked $python @((Join-Path $PSScriptRoot 'tools/gguf/download_models.py'), '--directory', $models)
$manifest | Add-Member -NotePropertyName cuda_bin -NotePropertyValue (Join-Path $cuda 'bin')
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RuntimeRoot 'installed.json') -Encoding UTF8
Write-Host 'GGUF files ready. Run Start-VoxCPM-GGUF.cmd; actual model readiness is checked at startup.'
