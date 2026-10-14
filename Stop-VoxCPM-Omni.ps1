param([string]$Distro = 'Ubuntu')
$ErrorActionPreference = 'Stop'
$batchFolder = Join-Path $PSScriptRoot 'data/batch-ja'
if (Test-Path -LiteralPath $batchFolder) {
    Set-Content -LiteralPath (Join-Path $batchFolder 'pause.request') -Value 'Pause before stopping Omni.'
    $lockPath = Join-Path $batchFolder 'worker.lock'
    $deadline = (Get-Date).AddMinutes(2)
    while (Test-Path -LiteralPath $lockPath) {
        try { $probe = [System.IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None'); $probe.Dispose(); break }
        catch [System.IO.IOException] {
            if ((Get-Date) -gt $deadline) { throw 'Batch is still finishing its active sentence; retry after it pauses.' }
            Start-Sleep -Seconds 2
        }
    }
}
$pidFile = Join-Path $PSScriptRoot 'artifacts/omni-gateway.pid'
if (Test-Path -LiteralPath $pidFile) {
    $launcherId = [int](Get-Content -LiteralPath $pidFile)
    $launcher = Get-CimInstance Win32_Process -Filter "ProcessId=$launcherId"
    $expected = Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts/python.exe'
    if ($launcher -and [System.IO.Path]::GetFullPath($launcher.ExecutablePath) -eq [System.IO.Path]::GetFullPath($expected) -and $launcher.CommandLine -like '*ff14_server.py*--omni-url*8809*') {
        Get-CimInstance Win32_Process -Filter "ParentProcessId=$launcherId" | Where-Object { $_.CommandLine -like '*ff14_server.py*--omni-url*8809*' } | ForEach-Object { Stop-Process -Id $_.ProcessId }
        Stop-Process -Id $launcherId -ErrorAction SilentlyContinue
    }
}
$linuxScript = ((& wsl -d $Distro --exec wslpath -a (Join-Path $PSScriptRoot 'tools/wsl/stop-omni.sh')) -join '').Trim()
if ($LASTEXITCODE -ne 0 -or !$linuxScript) { throw 'Cannot access Ubuntu WSL2.' }
& wsl -d $Distro --exec bash $linuxScript
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the owned Omni server.' }
Write-Host 'Omni stopped. Cached speech is preserved. To use native PyTorch again, run Start-VoxCPM.cmd.'
