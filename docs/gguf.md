# VoxCPM2 GGUF 量化版（測試中）

使用 Q8 BaseLM 配 F16 Acoustic，保留 VoxCPM2 嘅廣東話、文字描述生聲線、reference clone 同串流播放。原生 Windows 執行，毋須 WSL。模型下載約 3.55 GB；下載大小唔代表實際 VRAM 用量。

目前已驗證 Windows 編譯、模型載入、聲線描述生成、reference clone 同串流 API；**未完成全新 Windows 10／GTX 1660 Super 同時運行 FF14 嘅實機驗證**。

## 安裝

### 自動安裝（測試中）

適用 Windows 10 22H2／Windows 11 x64、支援 AVX2 嘅 CPU、GTX 16／RTX 20 系列或更新嘅 NVIDIA GPU。先更新顯示驅動；`nvidia-smi` 顯示嘅 CUDA 支援版本須為 12.9 或更新。安裝器唔會更換顯示驅動。

下載並解壓 repo，雙擊 **`Install-FF14Voice.cmd`**。GitHub「Download ZIP」亦可，安裝器會補回兩個 submodule 嘅程式碼。請放喺自己可寫入嘅資料夾，唔好放入遊戲目錄或 Program Files。

安裝器會補齊 Git、uv／Python、ffmpeg、.NET 10 SDK、CMake、VS 2022 C++ Build Tools 同 CUDA Toolkit 12.9，編譯 GGUF 引擎、下載及校驗模型，再由本機 FF14 抽取資源、編譯 app、生成測試音訊，最後開啟 backend 同 app。毋須 WSL，毋須額外下載 Ollama 文字模型；全新設定預選 Google 翻譯，對白會傳送至 Google。已有設定會保留。

首次安裝有較多下載及編譯，請預留數十 GB 空間同足夠時間。Windows 可能要求管理員授權；如果套件要求重開機，完成後再次雙擊同一檔案。中斷後可重跑，會重用已下載模型及編譯結果；資源階段只有喺遊戲檔案未變、索引及 reference 仍完整時先會略過。遊戲更新後重跑會重建資源，亦會更新之前挑選過嘅 reference。

一般 Square Enix／Steam 安裝位置會自動偵測；其他位置會提示輸入 `game\sqpack`。亦可指定：

```powershell
.\Install-FF14Voice.cmd -SqPack "D:\Games\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"
.\Install-FF14Voice.cmd -CheckOnly
```

`-CheckOnly` 只列出環境狀態；`-NoLaunch` 完成安裝及生成驗證後唔開 app。安裝日誌喺 `artifacts/install/`。再次使用雙擊 **`Start-FF14Voice.cmd`**；首次喺 app 揀遊戲文字語言、字幕／配音語言，再按「連接 FF14」。

### 手動安裝

已自行準備以上工具時，先按 [基本安裝](../README.md) 下載完整 repo，再安裝 Windows gateway。新環境用 CPU 版 PyTorch 即可；已有正常使用中嘅環境可以沿用。GGUF 使用 CUDA Toolkit 12.9 編譯，與 PyTorch wheel 嘅 CUDA 版本分開選擇。

```powershell
.\Setup-VoxCPM.cmd -CpuOnly
.\Setup-VoxCPM-GGUF.cmd
```

第一次會編譯引擎及下載模型，可能需時較長。下載中斷可以重新執行；完成後會核對 SHA-256。

安裝前只檢查環境：

```powershell
.\Setup-VoxCPM-GGUF.cmd -CheckOnly
```

## 使用

先關閉原本嘅原生／Omni backend，再啟動：

```powershell
.\Start-VoxCPM-GGUF.cmd
.\Start-FF14Sound.cmd
```

以上兩個命令喺不同 terminal 執行。GGUF 啟動視窗保持開啟，按 Ctrl+C 停止；app 使用同一個 `http://127.0.0.1:8808`。資源抽取仍用 `Restore-GameResources.cmd`。

預設 `-GpuLayers 0` 將 BaseLM 放喺 CPU，聲學部分用 GPU，減少 VRAM 用量；速度亦受 CPU 影響。增加 GPU layers 可加速，但會用更多 VRAM。顯存充裕先試：

```powershell
.\Start-VoxCPM-GGUF.cmd -GpuLayers 29
```

必須用實際遊戲場景驗證，唔好只喺遊戲未開時測試。程式會拒絕使用已佔用嘅 port，唔會停止其他 backend。日誌喺 `artifacts/gguf-runs/`；模型與引擎喺 `.cache/gguf/`。

## 驗證與來源

2026-10-18 喺 i7-13700K + RTX 3090、`GpuLayers 0` 嘅一次實測：

| 測試 | 首段聲音 | 總生成時間 | 音訊長度 |
| --- | ---: | ---: | ---: |
| 描述生聲線，模型載入後首次生成 | 1.35 秒 | 2.95 秒 | 3.04 秒 |
| 描述生聲線，預熱後 | 0.33 秒 | 1.73 秒 | 3.04 秒 |
| Reference clone，廣東話指令 | 0.78 秒 | 1.86 秒 | 2.40 秒 |

當時 GPU 有其他程式使用，載入及測試期間整張卡嘅記憶體用量最高比測試前增加約 3 GiB；呢個係估算，唔係獨佔程序精確用量。以上時間唔包括啟動模型，亦唔代表 1660 Super 或遊戲中效能。試聽仍需確認口音、讀字同角色相似度。

可以量度首段聲音等待時間同完整生成速度，輸出 WAV 供試聽：

```powershell
VoxCPM/.venv/Scripts/python.exe tools/gguf/benchmark.py --output artifacts/gguf-benchmark --reference "path/to/reference.wav"
```

`realtime_factor` 低於 1 代表該次生成快過音訊播放長度；唔等於遊戲中每次都達到相同表現。

只驗證模型及 gateway 可啟動，完成後自動停止測試程序：

```powershell
.\Start-VoxCPM-GGUF.cmd -VerifyOnly
```

目前接駁使用 10 steps / CFG 2，未開啟 reference 降噪或額外文字正規化。一般 clone 唔需要逐字稿；逐字稿延續模式仍需 Auto 語言及留空風格指令。請用非零 seed；上游引擎將 0 當作不重設隨機狀態，測試版會拒絕呢個值，避免聲線無聲無息改變。

引擎與模型版本記喺 `tools/gguf/manifest.json`。模型使用 [VoxCPM 官方 README 連結嘅 GGUF 權重](https://huggingface.co/DennisHuang648/VoxCPM2-GGUF)，引擎及 API 見 [llama.cpp-omni VoxCPM 文件](https://github.com/tc-mb/llama.cpp-omni/blob/master/tools/omni/voxcpm2/README.md)。
