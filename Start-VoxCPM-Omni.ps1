param([string]$Distro = 'Ubuntu')
$ErrorActionPreference = 'Stop'
$omniUrl = 'http://127.0.0.1:8809'
$gatewayUrl = 'http://127.0.0.1:8808'
$artifactFolder = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactFolder -Force | Out-Null
function Test-OmniReady {
    try {
        $models = Invoke-RestMethod "$omniUrl/v1/models" -TimeoutSec 3
        return @($models.data | Where-Object id -eq 'voxcpm2').Count -gt 0
    } catch { return $false }
}
try { $existing = Invoke-RestMethod "$gatewayUrl/api/health" -TimeoutSec 2 } catch { $existing = $null }
if ($existing -and $existing.backend -ne 'vllm-omni-wsl2') {
    throw 'Port 8808 is running the native backend. Pause batch dubbing and close that backend before starting Omni.'
}
if (!(Test-OmniReady)) {
    $linuxScript = ((& wsl -d $Distro --exec wslpath -a (Join-Path $PSScriptRoot 'tools/wsl/start-omni.sh')) -join '').Trim()
    if ($LASTEXITCODE -ne 0 -or !$linuxScript) { throw 'Cannot access Ubuntu WSL2.' }
    $wslProcess = Start-Process -FilePath 'wsl.exe' -ArgumentList @('-d', $Distro, '--exec', 'bash', $linuxScript) -WindowStyle Hidden -PassThru
    $wslProcess.Id | Set-Content (Join-Path $artifactFolder 'omni-wsl-launcher.pid')
    Write-Host 'Starting WSL2 Omni. First startup compiles CUDA kernels; see artifacts/omni-server.log.'
    $deadline = (Get-Date).AddMinutes(10)
    while (!(Test-OmniReady)) {
        if ($wslProcess.HasExited -and $wslProcess.ExitCode -ne 0) { throw 'WSL Omni exited; inspect artifacts/omni-server.log.' }
        if ((Get-Date) -gt $deadline) { throw 'Omni is still warming up; inspect artifacts/omni-server.log before retrying.' }
        Start-Sleep -Seconds 3
    }
}
if (!$existing) {
    $env:HF_HOME = Join-Path $PSScriptRoot '.cache/huggingface'
    $env:MODELSCOPE_CACHE = Join-Path $PSScriptRoot '.cache/modelscope'
    $env:GRADIO_ANALYTICS_ENABLED = 'False'
    $python = Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts/python.exe'
    $gateway = Start-Process -FilePath $python -ArgumentList @('-u', '../tools/voxcpm/ff14_server.py', '--omni-url', $omniUrl) -WorkingDirectory (Join-Path $PSScriptRoot 'VoxCPM') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $artifactFolder 'omni-gateway.stdout.log') -RedirectStandardError (Join-Path $artifactFolder 'omni-gateway.stderr.log')
    $gateway.Id | Set-Content (Join-Path $artifactFolder 'omni-gateway.pid')
    $deadline = (Get-Date).AddMinutes(1)
    while ($true) {
        try { $health = Invoke-RestMethod "$gatewayUrl/api/health" -TimeoutSec 2 } catch { $health = $null }
        if ($health -and $health.backend -eq 'vllm-omni-wsl2') { break }
        if ($gateway.HasExited -or (Get-Date) -gt $deadline) { throw 'Gateway failed to start; inspect artifacts/omni-gateway.stderr.log.' }
        Start-Sleep -Seconds 2
    }
}
Invoke-RestMethod "$gatewayUrl/api/warmup" -Method Post -TimeoutSec 30 | Out-Null
Write-Host 'WSL2 Omni ready. C# GUI and clone/language web UI: http://127.0.0.1:8808'
