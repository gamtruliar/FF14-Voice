param(
    [string]$RuntimeRoot = (Join-Path $PSScriptRoot '.cache/gguf'),
    [ValidateRange(-1, 29)][int]$GpuLayers = 0,
    [ValidateRange(1024, 65535)][int]$EnginePort = 8810,
    [ValidateRange(1024, 65535)][int]$GatewayPort = 8808,
    [switch]$VerifyOnly,
    [switch]$TestSpeech,
    [switch]$LaunchApp
)
$ErrorActionPreference = 'Stop'
if ($EnginePort -eq $GatewayPort) { throw 'Engine and gateway ports must differ.' }
$RuntimeRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RuntimeRoot)
$exe = Join-Path $RuntimeRoot 'build/bin/Release/llama-tts-server.exe'
$python = Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts/python.exe'
$installation = Join-Path $RuntimeRoot 'installed.json'
foreach ($file in @($exe, $python, $installation)) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing $file. Run Setup-VoxCPM-GGUF.cmd first." }
}
$installed = Get-Content -LiteralPath $installation -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tools/gguf/manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($installed.engine_revision -ne $manifest.engine_revision -or $installed.model_revision -ne $manifest.model_revision) {
    throw 'GGUF installation is from a different version. Rerun Setup-VoxCPM-GGUF.cmd.'
}
foreach ($file in $manifest.files) {
    $path = Join-Path $RuntimeRoot ('models/' + $file.name)
    if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -ne $file.size) {
        throw "Missing or incomplete model $path. Rerun Setup-VoxCPM-GGUF.cmd."
    }
}
function Test-Port([int]$Port) {
    $socket = [Net.Sockets.TcpClient]::new()
    try { $socket.Connect('127.0.0.1', $Port); return $true } catch { return $false } finally { $socket.Dispose() }
}
# Never attach to / kill another running engine with different models or settings.
foreach ($port in @($EnginePort, $GatewayPort)) {
    if (Test-Port $port) { throw "Port $port is already in use. Close the existing backend or choose different ports." }
}
$logs = Join-Path $PSScriptRoot ('artifacts/gguf-runs/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$previousPath = $env:PATH
$previousHf = $env:HF_HOME
$previousAnalytics = $env:GRADIO_ANALYTICS_ENABLED
$engineProcess = $null
$gatewayProcess = $null
function Wait-Ready([string]$Url, $Process, [scriptblock]$Validate) {
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-Date) -lt $deadline) {
        if ($Process.HasExited) { throw "Backend exited. See $logs" }
        try { $result = Invoke-RestMethod $Url -TimeoutSec 2 } catch { $result = $null }
        if ($result -and (& $Validate $result)) { return }
        Start-Sleep -Seconds 1
    }
    throw "Backend did not become ready. See $logs"
}
try {
    $env:PATH = $installed.cuda_bin + ';' + $env:PATH
    $env:HF_HOME = Join-Path $PSScriptRoot '.cache/huggingface'
    $env:GRADIO_ANALYTICS_ENABLED = 'False'
    $arguments = @('--host', '127.0.0.1', '--port', "$EnginePort", '--threads-http', '1',
        '--voxcpm2-base-lm', ('"{0}"' -f (Join-Path $RuntimeRoot 'models/VoxCPM2-BaseLM-Q8_0.gguf')),
        '--voxcpm2-acoustic', ('"{0}"' -f (Join-Path $RuntimeRoot 'models/VoxCPM2-Acoustic-F16.gguf')),
        '--voxcpm2-n-gpu-layers', "$GpuLayers")
    Write-Host "Loading VoxCPM2 Q8. BaseLM GPU layers: $GpuLayers; acoustic components use the GPU."
    $engineProcess = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'engine.stdout.log') -RedirectStandardError (Join-Path $logs 'engine.stderr.log')
    Wait-Ready "http://127.0.0.1:$EnginePort/v1/audio/speech/models" $engineProcess {
        param($result)
        @($result.data | Where-Object { $_.id -eq 'voxcpm' -and $_.sample_rate -eq 48000 }).Count -gt 0
    }
    $engineLog = Get-Content -LiteralPath (Join-Path $logs 'engine.stderr.log') -Raw
    if ($engineLog -notmatch 'custom component backend=CUDA') {
        throw "The GGUF engine did not initialize CUDA. Check the NVIDIA driver and CUDA DLLs; see $logs"
    }
    $gatewayProcess = Start-Process -FilePath $python -ArgumentList @('-u', ('"{0}"' -f (Join-Path $PSScriptRoot 'tools/voxcpm/ff14_server.py')), '--gguf-url', "http://127.0.0.1:$EnginePort", '--port', "$GatewayPort") -WorkingDirectory (Join-Path $PSScriptRoot 'VoxCPM') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'gateway.stdout.log') -RedirectStandardError (Join-Path $logs 'gateway.stderr.log')
    Wait-Ready "http://127.0.0.1:$GatewayPort/api/health" $gatewayProcess {
        param($result)
        $result.backend -eq 'llama-cpp-voxcpm2-gguf'
    }
    $null = Invoke-RestMethod "http://127.0.0.1:$GatewayPort/api/warmup" -Method Post -TimeoutSec 30
    if ($TestSpeech) {
        $sample = Join-Path $logs 'setup-test.wav'
        $null = Invoke-WebRequest "http://127.0.0.1:$GatewayPort/api/tts" -UseBasicParsing -Method Post -ContentType 'application/json' -Body '{"text":"Hello, welcome to Eorzea.","language":"en","seed":42}' -OutFile $sample -TimeoutSec 300
        $bytes = [IO.File]::ReadAllBytes($sample)
        if ($bytes.Length -le 44 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'RIFF' -or
            [Text.Encoding]::ASCII.GetString($bytes, 8, 4) -ne 'WAVE' -or [BitConverter]::ToInt32($bytes, 24) -ne 48000) {
            throw "Speech verification failed. See $logs"
        }
    }
    if ($VerifyOnly) { Write-Host 'GGUF startup verified; stopping the test processes.'; return }
    $appProcess = $null
    if ($LaunchApp) {
        $app = Join-Path $PSScriptRoot 'src/FF14Sound/bin/Release/net10.0-windows/FF14Sound.exe'
        if (!(Test-Path -LiteralPath $app)) { throw 'Run Install-FF14Voice.cmd first to build the app.' }
        $appProcess = Start-Process -FilePath $app -WorkingDirectory (Split-Path -Parent $app) -WindowStyle Normal -PassThru
    }
    Write-Host "GGUF ready: http://127.0.0.1:$GatewayPort. Keep this window open; Ctrl+C stops this backend."
    Write-Host "Logs: $logs"
    while (!$engineProcess.HasExited -and !$gatewayProcess.HasExited) {
        if ($appProcess -and $appProcess.HasExited) { return }
        Start-Sleep -Seconds 1
    }
    throw "A backend process exited. See $logs"
} finally {
    foreach ($process in @($gatewayProcess, $engineProcess)) {
        if ($null -ne $process -and !$process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }
    }
    $env:PATH = $previousPath
    $env:HF_HOME = $previousHf
    $env:GRADIO_ANALYTICS_ENABLED = $previousAnalytics
}
