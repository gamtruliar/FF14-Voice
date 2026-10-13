# FF14Sound

獨立 Windows / .NET 10 WPF app：引用 Tataru 嘅即時對白 reader 同翻譯 library，按 NPC 名稱配對遊戲原聲，再交畀本機 VoxCPM2 clone 配音。唔需要啟動 TataruHelper GUI。

## 啟動

需要 .NET 10 SDK、Python 3.10–3.12、uv；重建聲音庫另需 PATH 上有 ffmpeg。NVIDIA GPU 建議用 CUDA PyTorch，本機已用 RTX 3090 測試。

```powershell
# 第一次安裝；使用 VoxCPM/uv.lock 及 CUDA 12.8 wheels
.\Setup-VoxCPM.cmd
# CPU 安裝選項：.\Setup-VoxCPM.ps1 -CpuOnly

# Backend + clone / language web test UI
.\Start-VoxCPM.cmd --device cuda
# 另一個視窗啟動 C# GUI
.\Start-FF14Sound.cmd
```

Web UI：<http://127.0.0.1:8808>。首次生成會下載 `openbmb/VoxCPM2` 到 `.cache/huggingface`。GUI「預載 VoxCPM」可以預先載入模型；連接 FF14 時，如已勾選自動配音，亦會先預載。

## 角色聲音庫：已接入自動配音

本機 `data/game-voices/catalog.json` 已建立，GUI 會自動載入。2026-10-13 掃描結果：**341 個說話者代號、1,364 個 reference（日／英／德／法各 341）**，約 396 MiB；全部已驗證為 24 kHz、mono、PCM16 WAV。157 個代號有從 NPC 資料表建立嘅名稱別名，其餘保留代號供手動配對。

1. 選遊戲文字語言同翻譯目標；廣東話請選「廣東話」。繁體中文目標會用普通話配音。
2. 選「Clone 原聲版本」，預設日語。呢個選項决定用邊個版本嘅聲優；實際配音語言跟翻譯目標。
3. 開 FF14，按「連接 FF14」。自動翻譯 + 配音預設開啟。
4. 每次新對白：**Tataru reader 讀 NPC + 原文 → Tataru Google provider 翻譯 → 名稱匹配 reference → VoxCPM → 播放**。
5. 未識別嘅名字：喺右邊搜尋／選擇角色，按「配對目前 NPC → 所選角色」，之後會記住。自訂聲音 profile 優先於遊戲聲音庫。
6. 唔開遊戲亦可以填角色名（例如 `Y'shtola`）同原文，按「翻譯 + 配音」測試。更改一般設定後按「儲存設定」。

下一句對白、關閉對白或按「停止 / 略過配音」會取消舊請求及播放。Backend 會跳過排隊中已過期嘅句子；已經喺 GPU 計算嘅一句仍要完成，但結果唔會再播放。重複文字及同一個 reference 會使用本機 WAV 快取；修改 reference 內容或語音設定會使快取失效。

「即時」指對白更新後自動開始生成，仍有翻譯及推理延遲。長句會較慢。設定及快取位於 `%LOCALAPPDATA%/FF14Sound`；關閉 app 後可刪除其中 `speech-cache` 釋放空間。

**涵蓋範圍：** 341 係今次 VoiceMan 索引搵到嘅說話者代號，包含群眾／多人代號，唔等於遊戲全部 NPC。原本完全冇聲嘅角色無法憑空取得原角色 reference。部分角色只得很短嘅語音，GUI 會提示；音色相似度仍要試聽。未識別、名稱有歧義或欠缺所選語言原聲時會提示配對，唔會任意借另一個角色嘅聲。

**現有限制：** reader 未提供可靠嘅「今句正在播原聲」訊號，所以自動模式亦會配已有原聲嘅對白；可用停止按鈕略過。遊戲讀取沿用 sub repo reader，FF14 更新後可能要更新 offset；尚未完成實際遊戲內端到端驗證。

### 重建聲音庫

```powershell
.\Build-VoiceLibrary.cmd
# 或指定另一個 client 嘅 game/sqpack 路徑
.\Build-VoiceLibrary.cmd "D:\Game\ff14\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"
```

Script 會使用 `artifacts/game-voices/voice-clips.json`，缺少時先掃描，再為每個代號／語言選一段 reference。優先完整 5–12 秒片段，最多取 12 秒，並保留來源遊戲路徑／sheet。只讀遊戲檔案，輸出放本 repo。

換 client 或遊戲更新後，先明確重掃，再重建，避免沿用舊索引：

```powershell
dotnet run --project tools/FF14Sound.VoiceScan -- "D:\Game\ff14\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack" "artifacts/game-voices"
.\Build-VoiceLibrary.cmd
```

掃描方法及涵蓋限制見 [VoiceScan](tools/FF14Sound.VoiceScan/README.md)。原始索引含 45,051 個不同有效語音資源路徑；placeholder／解析失敗另行記錄，唔計入有效 reference。

## Web test UI：clone + language

上傳乾淨、單一角色樣本，填要講嘅文字，選 Speech language，再按 Generate Speech。一般 timbre clone 唔需要 transcript；廣東話請輸入廣東話口語文字。可直接上傳 `data/game-voices/references/<代號>/ja.wav` 等遊戲 reference。

Transcript / Ultimate Cloning 需要樣本正確逐字稿，並使用 Auto 語言及留空風格指令；跨語言配音使用一般 clone。語言選項使用 VoxCPM2 嘅文字提示，唔係翻譯功能，亦唔保證每種語言／音色品質一致。

## 結構

- `src/FF14Sound`：WPF GUI、自動流程、音訊快取及播放器。
- `src/FF14Sound.TataruBridge`：以 linked source 引用 Tataru realtime reader；保留 license。
- `TataruHelper/Translation`：以 ProjectReference 引用翻譯 library。
- `src/FF14Sound.Voices`：SqPack reference 匯出、catalog 及角色別名匹配。
- `tools/FF14Sound.VoiceScan` / `tools/FF14Sound.VoiceLibrary`：掃描及批量建立聲音庫。
- `VoxCPM/ff14_server.py`：web UI + WAV API 共用一個序列化模型實例。
- `VoxCPM/speech_options.py` / `request_versions.py`：語言／clone 驗證及過期請求管理。

兩個 sub repo 保留獨立 Git repository；根目錄未建立 Git repository。生成嘅模型、遊戲音檔及 artifacts 唔應加入 source commit。

## API / 驗證

Backend 綁定 `127.0.0.1`。翻譯 provider 會收到對白文字；語音生成由本機模型處理。

- `GET /api/health`：server、model 狀態及支援語言。
- `POST /api/warmup`：預載模型。
- `POST /api/tts`：例如 `{ "text": "你好呀", "language": "yue", "reference_audio": "H:/voices/npc.wav", "session_id": "client-a", "revision": 1 }`，回傳 PCM16 WAV。
- `POST /api/cancel`：例如 `{ "session_id": "client-a", "revision": 2 }`；較舊 revision 回 409。每個 app instance 使用獨立 session。

```powershell
dotnet build FF14Sound.slnx
dotnet run --project tests/FF14Sound.Smoke -- H:/PJ/FF14Sound/artifacts/gui-library.png
# 開 backend 後，加 --tts 實測 Tataru 翻譯 + 遊戲 Y'shtola reference + 廣東話生成
dotnet run --project tests/FF14Sound.Smoke -- H:/PJ/FF14Sound/artifacts/gui-library.png --tts
VoxCPM/.venv/Scripts/python.exe -m pytest VoxCPM/tests/test_ff14_speech.py -q
```

C# smoke 驗證名稱歧義、語言配對、自訂覆蓋、快取失效、取消結果及 WPF render；Python 測試用 fake model 驗證 WAV API、語言／clone 輸入、預載及同時請求嘅過期處理。真實模型測試與單元測試分開。

2026-10-13 驗證：solution build 0 warnings / 0 errors；8 個 Python 測試及 C# smoke 通過。RTX 3090 實測已完成「Tataru 英文 → 廣東話翻譯 → 自動配對 Y'shtola 日語 reference → VoxCPM WAV」，模型預載後該句生成用時 11.93 秒，輸出 `artifacts/integrated-yshtola-yue.wav`。此數字唔包括翻譯／首次模型載入，亦唔代表所有句子嘅速度。WPF 畫面渲染見 `artifacts/gui-library.png`；尚未試聽評估音色相似度，亦未連接運行中 FF14 驗證。

TataruHelper 使用 MIT license，build 複製到 `Licenses/TataruHelper.txt`；VoxCPM 使用其 sub repo 嘅 Apache-2.0 license，模型另依模型授權使用。
