# Keep hardware selection separate from installation so it can be tested without a GPU.
function Get-VoxCpmHardware {
    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if (!$smi) {
        return [pscustomobject]@{ CudaVersion = $null; Capabilities = @(); Description = 'nvidia-smi not found' }
    }
    try {
        $xmlText = & nvidia-smi -q -x 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'nvidia-smi failed' }
        $info = [xml]($xmlText -join "`n")
        $rows = @(& nvidia-smi --query-gpu=compute_cap --format=csv,noheader 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot query GPU compute capability' }
        $caps = @($rows | ForEach-Object {
            [double]::Parse($_.Trim(), [Globalization.CultureInfo]::InvariantCulture)
        })
        if (!$caps.Count) { throw 'No NVIDIA GPUs reported' }
        return [pscustomobject]@{
            CudaVersion = [version]$info.nvidia_smi_log.cuda_version
            Capabilities = $caps
            Description = "NVIDIA driver $($info.nvidia_smi_log.driver_version), CUDA support $($info.nvidia_smi_log.cuda_version), GPU capabilities $($caps -join ', ')"
        }
    } catch {
        throw "NVIDIA detection failed: $_. Check your driver, or explicitly use -CpuOnly."
    }
}

function Resolve-VoxCpmBackend {
    param(
        [ValidateSet('auto', 'cpu', 'cu126', 'cu128', 'cu130')][string]$Backend,
        $Hardware
    )
    if ($Backend -eq 'cpu') { return 'cpu' }
    if (!$Hardware.CudaVersion) {
        if ($Backend -ne 'auto') { throw 'CUDA requires a working NVIDIA driver / nvidia-smi. Use -CpuOnly for CPU.' }
        return 'cpu'
    }
    $caps = @($Hardware.Capabilities)
    if (!$caps.Count) { throw 'GPU compute capability is unknown. Check the NVIDIA driver or use -CpuOnly.' }
    $oldGpu = @($caps | Where-Object { $_ -lt 7.5 }).Count -gt 0
    $blackwell = @($caps | Where-Object { $_ -ge 10.0 }).Count -gt 0
    if (@($caps | Where-Object { $_ -lt 5.0 }).Count) {
        throw 'This GPU is too old for the packaged PyTorch builds. Use -CpuOnly.'
    }
    if ($Backend -eq 'auto') {
        if ($Hardware.CudaVersion -lt [version]'12.6') {
            throw 'The NVIDIA driver supports CUDA below 12.6. Update the driver or use -CpuOnly; installing a CUDA Toolkit alone does not update the driver.'
        }
        # Prefer the established 12.x builds. CUDA 13 remains an explicit choice.
        if (!$oldGpu -and $Hardware.CudaVersion -ge [version]'12.8') { $Backend = 'cu128' }
        else { $Backend = 'cu126' }
    }
    $required = @{ cu126 = '12.6'; cu128 = '12.8'; cu130 = '13.0' }[$Backend]
    if ($Hardware.CudaVersion -lt [version]$required) {
        throw "$Backend needs a driver reporting CUDA $required or newer. Update the driver, select a compatible -TorchBackend, or use -CpuOnly."
    }
    if ($blackwell -and $Backend -eq 'cu126') {
        throw 'Blackwell / RTX 50 GPUs need cu128 or cu130 with a compatible driver. Mixed old/new GPU generations may need separate environments.'
    }
    if ($oldGpu -and $Backend -ne 'cu126') {
        throw 'For older GPU architectures select -TorchBackend cu126, or use -CpuOnly.'
    }
    return $Backend
}

function Get-VoxCpmInstallSteps {
    param([string]$RepoRoot, [string]$Python, [string]$Interpreter, [string]$Backend)
    # Frozen dependencies include TorchCodec 0.10, matching PyTorch 2.10.
    # Skip both torch packages during sync: do not download/install the wrong build first.
    $syncArgs = @('sync', '--frozen', '--extra', 'dev', '--inexact', '--python', $Interpreter, '--no-install-package', 'torch', '--no-install-package', 'torchaudio')
    # Fresh installs use uv's x64 CPython, avoiding 32-bit/system/Store aliases.
    if ($Interpreter -like 'cpython-*') { $syncArgs += '--managed-python' }
    [pscustomobject]@{ Name = 'Dependencies'; Arguments = $syncArgs }
    [pscustomobject]@{ Name = 'PyTorch'; Arguments = @('pip', 'install', '--python', $Python, "torch==2.10.0+$Backend", "torchaudio==2.10.0+$Backend", '--index-url', "https://download.pytorch.org/whl/$Backend", '--no-deps') }
    [pscustomobject]@{ Name = 'Gemini SDK'; Arguments = @('pip', 'install', '--python', $Python, '-r', (Join-Path $RepoRoot 'tools/gemini-requirements.txt')) }
    [pscustomobject]@{ Name = 'Dependency check'; Arguments = @('pip', 'check', '--python', $Python) }
}
