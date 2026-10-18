# 安裝與排錯

喺專案根目錄執行 `Setup-VoxCPM.cmd`。請先關閉正在運行嘅配音 backend，避免 Python 套件被鎖住。

## 選擇 CPU／CUDA

| 情況 | 做法 |
| --- | --- |
| 一般 NVIDIA 顯卡 | 直接執行 `Setup-VoxCPM.cmd`，按驅動及顯卡自動選擇 |
| 想先睇安裝選擇 | `Setup-VoxCPM.cmd -CheckOnly`，唔會安裝或修改套件 |
| 指定 CUDA 12.6／12.8／13.0 版 | `Setup-VoxCPM.cmd -TorchBackend cu126`，亦可改用 `cu128` 或 `cu130` |
| 冇 NVIDIA，或者只想用 CPU | `Setup-VoxCPM.cmd -CpuOnly`，會明確安裝 CPU 版，包括由 CUDA 版切換 |
| AMD／Intel 顯卡 | 呢個 Windows 安裝器目前使用 CPU，未配置 ROCm／DirectML／XPU 加速 |
| NVIDIA 驅動只支援 CUDA 12.4 或更舊 | 更新顯示卡驅動，或者用 `-CpuOnly` |
| RTX 50／Blackwell | 用相容驅動配 `cu128` 或 `cu130`；唔適合 `cu126` |
| 較舊顯卡，例如 GTX 10 系列 | 自動選擇 `cu126`；實際能否執行仍以安裝後運算檢查為準 |

**唔需要令本機 CUDA Toolkit 版本同套件名稱一致。** 安裝器使用嘅 PyTorch 套件自帶 CUDA runtime；需要配合嘅係 NVIDIA 顯示卡驅動。`nvidia-smi` 顯示嘅 CUDA 版本代表驅動支援程度，唔係已安裝 Toolkit 版本。

自動選擇優先用 CUDA 12.x：驅動支援 12.8 或以上、顯卡架構合適時用 `cu128`，其餘相容情況用 `cu126`；`cu130` 留畀手動選擇。多張 NVIDIA 顯卡會一併檢查；如果舊／新世代無法共用同一套預設套件，安裝器會提示，唔會下載已知唔相容嘅版本。

安裝器固定使用配對嘅 PyTorch／torchaudio 2.10.0 同 TorchCodec 0.10，唔會將不同版本任意混裝。CUDA 選項依據 [PyTorch 官方安裝組合](https://pytorch.org/get-started/previous-versions/#v2100)，TorchCodec 配對見 [官方相容表](https://github.com/meta-pytorch/torchcodec#compatibility-with-torch-versions)。舊顯卡即使裝到 CUDA 套件，亦未必支援模型需要嘅 BF16 運算；檢查失敗可以改用 CPU。CPU 配音速度視乎硬件，唔保證即時。

## Python 與首次安裝

需要 `uv`，安裝方法見 [uv 官方指引](https://docs.astral.sh/uv/getting-started/installation/)。安裝後重新開 terminal。

新環境預設使用由 `uv` 管理嘅 64-bit Python 3.12，按需要自動下載，避免誤用 32-bit Python 或 Windows Store 捷徑。亦可選：

```powershell
.\Setup-VoxCPM.cmd -PythonVersion 3.11
```

支援 Python 3.10–3.12。已有相容嘅 `VoxCPM/.venv` 會沿用；如果想改 Python 版本，請先關閉 backend，將該資料夾改名備份，再重新執行安裝器。安裝器唔會自行刪除原有環境。

如果提示缺少 submodule：

```powershell
git submodule update --init --recursive
```

## 安裝失敗或切換模式

- **偵測 NVIDIA 失敗**：先執行 `nvidia-smi` 檢查驅動；如只想使用 CPU，可明確加 `-CpuOnly`。
- **驅動版本不足**：更新 NVIDIA 驅動，或者選擇支援嘅較低 CUDA 版本。單獨安裝另一個 CUDA Toolkit 唔會更新驅動。
- **下載失敗／套件被鎖住**：確認網絡正常，關閉 backend 後重跑相同命令。失敗時環境可能只完成部分安裝，未見 `Setup complete` 前唔好當作成功。
- **CUDA available 但運算失敗**：安裝器會實際測試 float32／BF16 運算、音訊重採樣同 VoxCPM 匯入。留意顯卡架構、驅動或 DLL 錯誤；需要時改用 CPU。
- **想由 CPU 切返 GPU**：重新執行 `Setup-VoxCPM.cmd`，或者明確指定 `-TorchBackend`。

成功後用 `Start-VoxCPM.cmd --device auto` 啟動。呢個安裝器處理 Windows 原生 backend；WSL2／vLLM-Omni 有自己嘅環境同硬件要求，見 [進階設定](advanced.md)。
