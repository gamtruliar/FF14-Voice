# FF14 Voice

**想聽住 NPC 講故仔，仲可以配埋廣東話字幕？**

FF14 Voice 係一個獨立 Windows app，為《FINAL FANTASY XIV》對白加入即時翻譯同 AI 配音。讀取遊戲中嘅 NPC 名稱及對白，用本機 VoxCPM2 生成聲音；有可用遊戲原聲樣本嘅角色可以 clone 聲線，未配對嘅 NPC 則按描述建立聲音。

可以揀 **日文原文配音＋廣東話字幕**，亦可以直接聽 **廣東話翻譯配音**。唔需要另外開 TataruHelper GUI。

> 呢個係非官方實驗專案。Repo 提供程式碼及資源重建工具；遊戲原聲、對白索引、模型權重及個人 API key 唔包含喺入面。請先用自己已安裝嘅遊戲 client 建立本機資源。

## 示範影片

實際遊戲中嘅對白翻譯、字幕同角色配音示範。**影片包含遊戲劇情。**

https://github.com/user-attachments/assets/4843614d-fe3e-49f8-8675-1080ceda593b

https://github.com/user-attachments/assets/05050371-2f7d-4312-b1a3-cb1c1279a228

https://github.com/user-attachments/assets/172fbe7e-f464-4ff5-82e9-0c1cfa5b0d96

https://github.com/user-attachments/assets/ac9007ee-4ff7-4e33-ba47-57d7c616e8ec

https://github.com/user-attachments/assets/1ce9aebe-65db-4dab-b8ca-cb4507443e47

## 有咩功能？

- **即時翻譯**：支援 Ollama、Gemini 同 Google 翻譯，可選廣東話字幕／配音。
- **角色聲線**：按 NPC 配對日／英／德／法原聲 reference；可手動配對、用自己嘅 reference，或者編輯聲線描述。
- **冇原聲嘅 NPC 都可以講嘢**：首句按描述生成，之後保存為該角色嘅 reference，配合固定 seed 維持聲線。
- **已有錄音交返遊戲播放**：索引識別到嘅原錄音對白會略過 AI 配音，翻譯及字幕照常顯示。
- **獨立字幕視窗**：原文、譯文或雙語，支援置頂、字體、透明度、精簡模式及手動播放。
- **播放控制**：音量、保留音高嘅變速、自動平衡音量；換句會取消舊配音。
- **本機快取／背景預配**：重用已生成音檔，亦可預先建立原文配音，暫停後續跑。
- **可選官方繁中來源**：用自己嘅國際版及繁中版 client 建立日文 ID → 繁中對白配對，再翻成廣東話。

## 開始之前

| 需要 | 用途 |
| --- | --- |
| Windows x64 | 執行桌面程式及讀取遊戲對白 |
| .NET **10 SDK** | 編譯 app 及資源工具；只裝 Runtime 唔夠 |
| Git | 下載主 repo 同兩個 submodule |
| Python 3.10–3.12、`uv` | 安裝本機 VoxCPM 環境 |
| `ffmpeg`（加入 PATH） | 把遊戲原聲轉成 reference WAV |
| 已安裝嘅 FF14 國際版 client | 讀取日／英／德／法遊戲資源 |
| NVIDIA GPU + 相容驅動 | CUDA 配音；提供 CPU 安裝選項，但速度會受硬件限制 |

首次安裝需要下載依賴同模型，請預留磁碟空間。開發機用 RTX 3090；實際延遲受句長、模型、遊戲負載及翻譯服務影響。

## 快速上手

### 1. 下載完整 repo

```powershell
git clone --recurse-submodules https://github.com/gamtruliar/FF14-Voice.git ff14-voice
cd ff14-voice
```

如果已經 clone：

```powershell
git submodule update --init --recursive
```

請用 Git clone；GitHub 嘅 ZIP 唔包含 submodule 內容。`TataruHelper/` 同 `VoxCPM/` 都要下載完整。

### 2. 安裝配音環境

```powershell
.\Setup-VoxCPM.cmd
```

安裝器會按 NVIDIA 驅動同顯卡選擇相容版本；偵測唔到 NVIDIA 時使用 CPU。唔需要自行安裝 CUDA Toolkit。安裝完成會檢查配音環境能否執行。

想先檢查、指定版本，或者只用 CPU：

```powershell
.\Setup-VoxCPM.cmd -CheckOnly
.\Setup-VoxCPM.cmd -TorchBackend cu126
.\Setup-VoxCPM.cmd -CpuOnly
```

支援 `cu126`、`cu128`、`cu130` 同 `cpu`。AMD／Intel 顯卡目前用 CPU 模式；舊驅動、RTX 50 系列、Python 版本同安裝失敗嘅處理方法，見 [安裝與排錯](docs/setup.md)。

第一次生成／預載時會下載 VoxCPM2 模型，請保持網絡連線並等候完成。

### 3. 由自己嘅 client 重建遊戲資源

一般用預設安裝位置，可以直接執行：

```powershell
.\Restore-GameResources.cmd
```

工具會先試 Square Enix 版，再試 Steam 版嘅預設位置。想明確指定時，用 **`game\sqpack`**，唔係 launcher 所在資料夾；Square Enix 版常見預設路徑如下（自訂安裝位置請自行更改）：

```powershell
.\Restore-GameResources.cmd -SqPack "C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"
```

Steam 預設位置係 `C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XIV Online\game\sqpack`；自訂 Steam library 請用 `-SqPack` 指定。

工具會由已安裝嘅遊戲建立聲音庫同對白索引，**唔會修改遊戲檔案**。搵唔到安裝位置時會提示你輸入路徑。

呢步係首次使用配音嘅必要步驟：欠缺原錄音索引時，app 會暫停配音。遊戲更新後可再執行同一命令，完成後重開 app。重建會更新生成資源，包括之前挑選過嘅 reference；個人設定另存於 LocalAppData。

更多選項，包括繁中對白、預配索引、前置檢查及各輸出檔用途，見 [資源重建指南](docs/game-resources.md)。

### 4. 啟動 backend 同 app

開兩個 terminal：

```powershell
# Terminal 1：保持運行
.\Start-VoxCPM.cmd --device auto
```

```powershell
# Terminal 2
.\Start-FF14Sound.cmd
```

Backend 測試頁：<http://127.0.0.1:8808>。可以先用 web UI 試文字生成／clone，或喺 app 按「預載 VoxCPM」減少第一句等待時間。

### 5. 揀翻譯同配音方式

1. 設定「遊戲文字語言」、翻譯目標（例如廣東話），同「配音內容」：原文配音或翻譯配音。
2. 設定翻譯引擎。預設係 **Ollama**，需自行準備可用服務及文字模型；填 API base URL，按「更新模型」，揀已安裝模型。亦可改選 Google，或用自己 API key 設定 Gemini。
3. 「Clone 原聲版本」決定 reference 用邊種語言嘅聲優，唔係輸出配音語言。
4. 開 FF14，按「連接 FF14」，開始同 NPC 對話。想用獨立字幕就按「字幕視窗」。
5. 未配對角色會用描述生成；需要修正時，選聲音庫角色，再按「配對目前 NPC → 所選角色」。單純瀏覽角色唔會改配對。

唔開遊戲都可以填 NPC 名同對白，按「翻譯 + 配音」試用。一般設定會自動保存；NPC 表及角色 profile 編輯有各自儲存按鈕。

Gemini：將自己嘅 key 存成單行文字檔，喺 GUI 選取，再按「更新模型」揀帳戶可用模型。請妥善保管 API key。Ollama 會把本句及相關詞條傳到你設定嘅服務；Google／Gemini 會把翻譯內容傳到各自雲端服務。TTS 在本機執行。

## 進階用法

- [資源重建指南](docs/game-resources.md)：建立聲音庫、加入繁中對白、更新資源及排錯。
- [進階設定與開發](docs/advanced.md)：WSL2 / vLLM-Omni 串流、字幕、聲線、快取、背景預配、詞表及測試。

## 常見問題

**連接 FF14 顯示「存取被拒」？** 先關閉舊 app，再用 `Start-FF14Sound-Admin.cmd`，或者按「以管理員重啟」。Backend 唔需要管理員權限。錯誤詳情喺 `%LOCALAPPDATA%\FF14Sound\connection-error.log`。

**缺少聲音庫／原錄音索引？** 執行資源重建命令，確認產生 `catalog.json` 同 `native-voiced-lines.json`，然後重開 app。用自訂輸出資料夾時，在 GUI 選返對應 `catalog.json`。

**翻譯冇反應？** 預設 Ollama 需要另外啟動及安裝模型；「更新模型」只列出已有模型，唔會代下載。Gemini 需要有效 key 同可用額度。

**有啲 NPC 配唔到／聲線唔似？** 聲音庫只涵蓋掃描器識別到嘅 VoiceMan 資源；同名、缺少所選語言、短樣本及多人錄音都可能影響結果。可以手動配對／自訂 reference；未配對聲線屬創作，唔代表官方聲優。

**遊戲更新後讀唔到對白？** 即時 reader 沿用 TataruHelper，可能需要更新 reader／offset；重建音檔索引唔會修復記憶體讀取相容性。

## 鳴謝與授權

感謝 [TataruHelper](https://github.com/NightlyRevenger/TataruHelper)（MIT）、[VoxCPM](https://github.com/OpenBMB/VoxCPM)（Apache-2.0）、[Lumina](https://github.com/NotAdam/Lumina) 同 [SoundTouch.Net](https://github.com/owoudenberg/soundtouch.net)（LGPL-2.1-or-later）。第三方授權見各 submodule 及 [Licenses/](Licenses/)。`tools/voxcpm/app.py` 改編自 VoxCPM web UI，保留上游授權說明；模型另依模型本身授權使用。

FINAL FANTASY XIV 及遊戲素材屬 SQUARE ENIX 所有；本專案與 SQUARE ENIX 無隸屬關係。資源工具由使用者嘅本機安裝讀取素材，repo 不附遊戲原聲庫或完整對白資料。
