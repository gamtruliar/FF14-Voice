# 進階設定與開發

基本安裝見 [README](../README.md)，遊戲檔重建見 [資源指南](game-resources.md)。以下命令都喺 repo 根目錄執行。

## WSL2 / vLLM-Omni 串流

可選加速 backend，需要已配置好 GPU 支援嘅 Ubuntu WSL2、Python venv 支援，以及先完成 Windows `Setup-VoxCPM.cmd`。開發時用 RTX 3090；部署設定喺 `tools/wsl/voxcpm2-3090.yaml`，其他硬件可能需要調整。

```powershell
.\Setup-VoxCPM-Omni.cmd
.\Start-VoxCPM-Omni.cmd
# 停止／切返原生 Windows backend：
.\Stop-VoxCPM-Omni.cmd
```

WSL 環境預設喺 `~/.local/share/ff14sound-omni/venv`，模型服務係 `127.0.0.1:8809`，Windows gateway 同測試 UI 仍用 `127.0.0.1:8808`。開 Omni 前先暫停背景預配同關閉原生 backend，避免撞 port。首次啟動會下載指定模型 snapshot（已有則重用），並編譯 CUDA kernels，可能等幾分鐘。日誌喺 `artifacts/omni-server.log` 及 `omni-gateway.*.log`。

安裝版本固定喺 `tools/wsl/setup-omni.sh`。啟動器可用 WSL 環境變數 `FF14_VOXCPM_MODEL` 指定另一個已存在嘅相容模型目錄；預設使用 repo `.cache/huggingface`。唔需要把模型 commit 入 Git。

GUI「串流播放」收到首段 PCM 就開始播放，完整成功先寫快取；換句／停止會清走舊音訊。原生 backend 未支援串流時會回退完整 WAV。Omni 部署固定 10 steps / CFG 2；未接入嘅控制會喺 web UI 停用。

## 字幕與播放

- 「字幕視窗」可顯示原文、譯文或雙語；拖動文字／標題移位，右下角改大小。精簡模式可雙擊或右鍵恢復控制。
- 「播放原文／播放譯文」只試播目前句子，唔會更改主視窗配音模式。翻譯完成唔會搶走手動播放；換句則取消舊工作。
- 音量支援 0–400%，速度支援 0.75×–1.50×，用 SoundTouch tempo 保留音高。播放設定唔會改寫原音檔或快取。
- 「自動平衡音量」可調短時 RMS 上下限及 PCM 峰值上限；預設關閉。自動模式有增益限制及短緩衝，唔保證所有聲音都達到目標響度。
- 原文模式下，生成原文聲音同翻譯同步進行，翻譯失敗亦唔阻止原文配音。翻譯模式則用選定目標語言；繁體中文目標配普通話，廣東話請明確選廣東話。

## 聲線、reference 與 seed

優先次序係自訂 profile → 配對遊戲原聲 → 描述生成。主畫面會顯示真正使用中嘅 voice、配對依據、seed 同 Voice description；遊戲 reference clone 未套用描述時會明確標示。

每個可識別角色有穩定 seed。未配對 NPC 首句完整生成後，會保存最長 12 秒作之後嘅 reference；取消、靜音或損壞結果唔會成為 reference。「轉 seed／重試聲線」可重新試聲，但已有 reference 嘅角色仍 clone 同一 reference，唔代表換聲優。固定 seed／reference 亦唔保證跨句完全一致。

「NPC 聲音表 / 無原聲標記」可搜尋名字／ID、改描述、CV 標記同備註，以及匯出 CSV。同名但無法分辨 ID 嘅 NPC 可能共用聲線；「未找到原聲」只表示未配對，唔係確定官方冇 CV。

Web UI 可上傳單一角色樣本試 clone。一般 timbre clone 唔需要 transcript；Ultimate Cloning 要正確逐字稿、Auto 語言同留空風格指令。跨語言配音用一般 clone。

Reference 音質評分／更新係額外工作流，工具包括 `rank_voice_refs.py`、`refresh_voice_refs.py`、`verify_ref_refresh.py` 及 `audition_voice_refs.py`；各工具參數可用 `--help` 查看。依賴列喺 `tools/ref-quality-requirements.txt`，建議另開 Python 環境。完整資源重建會重新匯出 reference，請先備份自己挑選過嘅版本。

## 快取與原文預配

「配音快取」可按角色清除生成對白；預設保留已建立嘅首句 reference。勾選「同時重設首句 reference」先會重新定聲。清快取前先暫停背景預配，避免同時寫入。

「全 client 預配／進度…」會按所選遊戲語言建立佇列，優先有 reference 嘅角色，再處理 default。只預配原文，唔批量呼叫翻譯服務；有已識別遊戲原錄音、動態文字或未確認講者嘅資料會按索引分類略過。可以暫停後續跑，完整有效快取會重用。

```powershell
dotnet run --project tools/FF14Sound.Batch -- prepare data/dialogue-ja.jsonl data/batch-ja
dotnet run --project tools/FF14Sound.Batch -- run data/batch-ja/plan.json
dotnet run --project tools/FF14Sound.Batch -- pause data/batch-ja
dotnet run --project tools/FF14Sound.Batch -- verify-cache data/batch-ja/plan.json
```

索引先按 [資源指南](game-resources.md) 建立。角色描述、seed 或配對改動後，先暫停再重建佇列，先會採用新設定。批量生成可耗用大量時間及空間，並非即時配音嘅前置要求。

## 翻譯與詞表

Ollama 接受本機或遠端 API base URL；唔需要加 `/api/chat`。如果服務使用 Cloudflare Access，可安裝 `cloudflared` 後按「網頁登入」，喺瀏覽器完成登入。Session 由 Windows DPAPI 按目前帳戶加密保存；「清除本機登入」只清 app 保存嘅 session。

Gemini 由本機 backend 使用 `google-genai` adapter 呼叫，GUI 只保存 key 檔路徑。key 放 `APIKey/` 或自己選嘅檔案；切勿放入公開 repo。舊環境可執行 `Setup-Gemini.cmd` 後重開 backend。

Ollama／Gemini 共用 `data/translation-glossary.json`，只附上命中詞條。此詞表由固定版本多語資料轉換而來，中文繁體字唔等於台服官方譯名；來源及 commit 記喺資料內。官方繁中對白模式命中時會直接用繁中原句，唔附詞表。Google 網頁翻譯引擎唔接受詞表提示。

自訂詞條可以加喺 `data/translation-glossary-overrides.json`：

```json
[
  {
    "Id": "custom:example",
    "Category": "PlaceName",
    "Names": {"ja": "例の場所", "en": "Example Place", "zh-TW": "示例地點", "yue": "示例地點"}
  }
]
```

重建詞表會下載來源資料至 `artifacts/glossary-source/`：

```powershell
VoxCPM/.venv/Scripts/python.exe tools/build_translation_glossary.py
```

## 開發與驗證

FF14 專用 Python 程式碼同測試喺 `tools/voxcpm/`，以 `VoxCPM/.venv` 執行，唔依賴子 repo 入面嘅未提交檔案。`app.py` 改編自上游 UI；圖片資源仍讀取 `VoxCPM/assets/`。

```powershell
dotnet build FF14Sound.slnx
VoxCPM/.venv/Scripts/python.exe -m pytest tools/voxcpm/tests -q
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-RestoreGameResources.ps1
```

現有 WPF smoke 測試需要已建立本機遊戲資源，可另外執行：

```powershell
dotnet run --project tests/FF14Sound.Smoke -- "$PWD/artifacts/gui-library.png"
```

資源工具測試使用暫存假資料及 mock CLI，唔需要遊戲、模型或 GPU。Backend 測試使用 fake model，唔代表真實模型音質或遊戲內端到端測試。實際 TTS 測試可在已啟動 backend 後，為 C# smoke 加 `--tts`；呢個選項會呼叫翻譯及真實模型。

常用本機 API：`GET /api/health`、`POST /api/warmup`、`POST /api/tts`、`POST /api/cancel`。例如：

```json
{"text":"你好呀","language":"yue","reference_audio":null,"control":"成年男聲，沉穩。"}
```

效能比較工具有 `tools/benchmark_tts.py` 同 `tools/benchmark_stream.py`；執行前暫停預配，並分開記錄冷啟動同預熱後結果。
