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

### WSL2 / vLLM-Omni 加速（已於 RTX 3090 實測）

2026-10-14 已在現有 Ubuntu 24.04 / WSL2 建立獨立環境 `~/.local/share/ff14sound-omni/venv`：Python 3.12.3、vLLM 0.30.0、vLLM-Omni 0.30.0、VoxCPM 2.0.3、PyTorch 2.13.0。Windows 原本 Python 環境保留；模型沿用 H 槽既有 snapshot，冇重下載權重。安裝參考 [官方 GPU 文件](https://docs.vllm.ai/projects/vllm-omni/en/latest/getting_started/installation/gpu/)，部署設定按 v0.30.0 實際程式核對。

- 首次安裝：`Setup-VoxCPM-Omni.cmd`（需要已有 Ubuntu WSL2）。
- 啟動：`Start-VoxCPM-Omni.cmd`。WSL 模型服務用 localhost:8809；Windows 相容 API／clone＋language web UI 仍用 localhost:8808，C# 設定唔使改。首次編譯約數分鐘，之後仍需載入模型及重建 CUDA Graph；啟動器會等 ready。
- 停止／切回：`Stop-VoxCPM-Omni.cmd` 先等日文預配目前一句保存，再停止本專案 Omni 服務；之後可開原有 `Start-VoxCPM.cmd`，再續跑預配。唔會刪音檔或停止整個 WSL／其他 Linux 程序。
- 有原錄音略過、REF 優先、取消過期對白、背景讓即時對白先行。default 首句用描述生成，之後沿用首句聲線 REF；角色 reference 用音檔內容傳入 WSL，唔靠 Linux 解讀 Windows 路徑。
- Omni 0.30.0 固定 10 steps / CFG 2，web UI 會鎖定這兩個控制及關閉未接入嘅降噪／文字正規化；語言及 Voice description 用同一套文字指令，transcript continuation 仍可用。
- C#「串流播放」預設開啟，設定會自動儲存。Omni 收到首段 PCM 就開始播放（48 kHz mono PCM16、120 ms 播放緩衝）；換下一句／停止即取消 HTTP 及清空舊播放緩衝。只有收到完整結束標記先保存 WAV 快取；原有完整快取仍可重播。原生 Windows backend 未支援此串流 API 時會自動用完整 WAV。已識別遊戲原錄音的句子，串流／快取／手動配音都會略過。
- 2026-10-15 串流實測（同一批日文樣本、RTX 3090、模型已預熱）：clone 首段 PCM 0.224–0.228 s，完整生成約 2.01 s／10.4 s 音訊；default 首段 0.157–0.162 s，完整生成 1.26–1.28 s／6.56 s 音訊。切換串流後第一個 clone 請求首段 0.537 s。首段時間不包含喇叭／音效驅動緩衝、翻譯及遊戲讀取輪詢時間；冷啟動仍要載入／編譯。資料在 `artifacts/omni-benchmark/stream-results.json`，用 `tools/benchmark_stream.py` 重測。
- 全 client 預配目前已暫停；即時串流唔需要先完成預配。固定角色 seed 更新前建立嘅舊佇列需先重建再續跑，舊音檔保留，但不會混入新 seed 嘅快取。
- GUI 頂部「配音音量」可調 0–400%，100% 為原音量，200% 按鈕可快速放大；播放中調整即時生效並自動儲存。串流、快取及完整生成的配音共用此控制，只改播放訊號，不改生成／reference／快取音檔或遊戲音量。超過 100% 時以 PCM 飽和限制避免溢位；若聽到失真請調低。角色原聲樣本試聽仍用獨立播放器。
- `tools/wsl/voxcpm2-3090.yaml` 限制一次一個請求、KV cache 2 GiB、GPU memory utilization 0.65，保留顯存供遊戲。實際顯存及速度受遊戲負載影響。
- 日誌：`artifacts/omni-server.log`、`omni-gateway.stdout.log`、`omni-gateway.stderr.log`。本機舊模型 config 缺 `model_type`，Omni 啟動會印相關警告並自動補建暫存 config；此組合已完成生成驗證，冇改寫原權重。

相同日文文字、相同角色 REF、兩邊 10 steps，測量完整 HTTP 回傳 WAV（唔經 C# 音檔快取）：

| 樣本 | 原生 Windows | WSL2 Omni 預熱後 | 輸出長度 |
|---|---:|---:|---:|
| ヤ・シュトラ clone | 17.39–19.35 s | 2.09–2.13 s | 10.4 s |
| default＋Voice description | 9.12–11.66 s | 1.28–1.31 s | 原生 6.40 s／Omni 6.56 s |

Omni 首次 clone 請求另有編譯成本（首輪 16.68 s）；重啟後同樣本首句 5.04 s，第二句 2.11 s。以上係兩個樣本嘅效能結果，唔保證所有句長／角色／遊戲負載同速，亦未以人工逐句驗證音色。可試聽 `artifacts/omni-benchmark/gateway-clone-2.wav` 同 native 對照。輸入、每次時間／RTF、WAV、WSL 套件版本清單均保存於 `artifacts/omni-benchmark/`；重測用 `tools/benchmark_tts.py`，執行時先暫停預配。

連接遊戲出現「存取被拒」時，按 GUI「以管理員重啟」，或先關閉舊 GUI 再執行 `Start-FF14Sound-Admin.cmd`，接受 Windows UAC 後重新連接。一般啟動保持普通權限；VoxCPM 不需要跟隨提升。管理員視窗標題會顯示「管理員」。完整連接錯誤保存到 `%LOCALAPPDATA%/FF14Sound/connection-error.log`；如果管理員模式仍然失敗，應檢查該錯誤而非重複提升權限。

## 角色聲音庫：已接入自動配音

本機 `data/game-voices/catalog.json` 已建立，GUI 會自動載入。2026-10-13 掃描結果：**341 個說話者代號、1,364 個 reference（日／英／德／法各 341）**，約 396 MiB；全部已驗證為 24 kHz、mono、PCM16 WAV。157 個代號有從 NPC 資料表建立嘅名稱別名，其餘保留代號供手動配對。

1. 選遊戲文字語言同翻譯目標；廣東話字幕請選「廣東話」。再選「配音內容」：翻譯配音或原文配音。
2. 選「Clone 原聲版本」，預設日語。呢個選項决定 reference 聲優版本；原文配音跟遊戲文字語言，翻譯配音跟翻譯目標（繁體中文會用普通話）。
3. 開 FF14，按「連接 FF14」。對白會自動翻譯，自動配音預設開啟。
4. 每次新對白：**Tataru reader 讀 NPC + 原文 → 所選翻譯引擎 → 名稱匹配 reference → VoxCPM → 播放**。新設定預選本機 Ollama；Google 選項繼續引用 Tataru Google provider。
5. 未配對嘅名字先用預設聲音繼續配音，並記入 NPC 聲音表。亦可喺右邊選角色，按「配對目前 NPC → 所選角色」。優先次序：自訂 profile → 已配對遊戲原聲 → 預設聲音。
6. 唔開遊戲亦可以填角色名（例如 `Y'shtola`）同原文，按「翻譯 + 配音」測試。一般設定改動約 0.4 秒後自動儲存，關閉／管理員重啟前亦會儲存；語言下方顯示儲存狀態。角色 profile 及 NPC 表編輯仍有各自儲存按鈕。

左邊角色名稱下面會顯示 **NPC → 使用中 voice**，包括代號／音檔、自動或手動配對、自訂覆蓋、clone 原聲語言；未有可用配對時顯示 `default`。滑鼠停喺該行可看完整 reference 路徑及風格描述。顯示同配音共用一個解析結果；右邊只選擇／瀏覽代號唔會改變使用中聲音，必須按配對按鈕。

配對下面直接顯示 **Voice description**，唔使靠 tooltip。Fallback／有風格指令嘅自訂 profile 顯示真正送去 TTS 嘅內容；遊戲 reference clone 無風格指令時，顯示 NPC 建議描述並標明「未套用，使用 reference 音色」。换 NPC 或清空名字會即時更新，左邊內容可捲動，長描述亦可獨立捲動。

同一角色固定使用同一粒 **Seed**，左邊聲音配對行會顯示數值。遊戲原聲配對按 voice token、無 REF 角色按唯一 NPC key（無法唯一識別時按正規化顯示名稱）產生穩定 seed；唔受句子、配音語言或程式重開影響。同名但無法識別 ID 嘅路人會共用 seed。自訂 profile 與新預配佇列會儲存 Seed，原文、翻譯、串流、完整 WAV 及背景預配都會傳入同一數值。修改自訂 profile 保留原 seed。

固定 seed 減少隨機變化，唔保證跨句音色完全一致。未配對 NPC 會另外保存首次完整生成聲音作 REF（見下文）。快取識別包含角色、seed 及描述；此版本唔會重播舊有共用 seed 42 嘅快取。舊預配佇列會提示重建，唔會自動開始大量重配。

### 原文配音與字幕視窗

「原文配音」用遊戲原文重新生成 TTS，語言跟「遊戲文字語言」；例如日文原文配日語，同時將字幕翻成廣東話。原文生成與翻譯同步開始，翻譯失敗亦唔會阻止原文配音。即使自訂 profile 指定咗其他語言，原文模式仍使用來源語言及一般 timbre clone；profile 本身唔會被修改。翻譯模式保持原本設定。

語言、配音模式、自動配音、主視窗置頂、clone 版本、聲音庫路徑、預設描述及 API 地址都會保存。已連接後更改「遊戲文字語言」會停止舊 reader，需要再按「連接 FF14」。其他配音語言／模式更改會取消舊配音，之後新對白按新選項處理。

按上方「字幕視窗」開獨立視窗，預設放主螢幕下方：

- 拖標題或字幕文字移動，右下角調大小；位置、大小會記住。
- 可選翻譯、原文或雙語，調整字體、底色透明度及獨立置頂。
- 按「精簡」隱藏控制列；雙擊字幕或右鍵可恢復。右鍵亦可移回主螢幕下方。
- 新一句出現即清走舊翻譯，收到新翻譯後更新；遊戲對白關閉時清空字幕。手動編輯原文／翻譯亦會同步。
- 主視窗縮小時字幕仍然保留。關閉主程式會關閉字幕；下次啟動恢復之前開啟狀態。直接關閉字幕則記住為關閉。

字幕有獨立 taskbar 視窗，適合視窗／無邊框模式；可拖到遊戲視窗下面或另一個螢幕。設定仍在 `%LOCALAPPDATA%/FF14Sound/settings.json`，旧設定檔會補入新選項嘅預設值。

下一句對白、關閉對白或按「停止 / 略過配音」會取消舊請求及播放。Backend 會跳過排隊中已過期嘅句子；已經喺 GPU 計算嘅一句仍要完成，但結果唔會再播放。重複文字及同一個 reference 會使用本機 WAV 快取；修改 reference 內容或語音設定會使快取失效。

「即時」指對白更新後自動開始生成，仍有翻譯及推理延遲。長句會較慢。設定位於 `%LOCALAPPDATA%/FF14Sound`；配音快取通常位於 `data/speech-cache`，可在「配音快取」頁查看實際路徑及釋放空間。

**涵蓋範圍：** 341 係今次 VoiceMan 索引搵到嘅說話者代號，包含群眾／多人代號，唔等於遊戲全部 NPC。原本完全冇聲嘅角色無法憑空取得原角色 reference。部分角色只得很短嘅語音，GUI 會提示；音色相似度仍要試聽。未識別、名稱有歧義或欠缺所選語言原聲時會用 default 文字生成，標明「無 clone / 無 reference」。

### 未配對 NPC：首句生成並保存聲線

`default` 第一句只用對白、配音語言、固定 seed 及 Voice description 生成，忽略舊設定嘅通用 default reference。完整成功後，會把最長頭 12 秒保存到快取下 `learned-voices/`，之後同一 NPC 以此作 timbre clone，唔會每句覆蓋或串接上句 transcript。串流、完整 WAV 同背景預配共用此 reference，重開仍保留。短於 0.3 秒、靜音、損壞、失敗或取消嘅結果唔會成為 reference；下次完整有效生成再定聲。無角色名嘅手動測試不會建立共用聲線。

首句 REF 按角色身份、seed 同描述保存，配音語言改動仍沿用；改 Voice description 會建立另一個聲線版本。已有遊戲／自訂 REF 仍優先使用，唔會被生成聲覆蓋。文字生成係創作聲線，唔代表 NPC 官方聲線；配音前仍檢查原錄音索引，已有錄音嘅句子交返 GAME。

### 配音快取管理

主視窗按「配音快取」，可搜尋 NPC，查看已配句數、首句 REF 數及容量。支援清除所選角色或全部快取；預設只清生成對白，保留已定好嘅聲線。勾選「同時重設首句 reference」先會刪埋該角色／全部自動 REF，下次再由描述定聲。遊戲原聲庫、自訂 reference 原檔及其他檔案不會刪除。

新音檔附有角色 metadata；舊音檔會嘗試由目前語言預配佇列還原角色，無法識別嘅列為「舊快取／未識別角色」。只管理畫面顯示嘅快取根目錄，唔會自動搬回其他資料夾嘅舊快取。開管理頁會停止當前即時配音及暫停讀取新對白，關閉後恢復；背景預配仍在跑時須先暫停，避免清除後即被生成工作寫返。

已測試首句建立、第二句帶同 REF、串流取消、同角色併發、按人／全部清除及保留自訂原檔；另完成真實 Omni 兩句日文串流測試。畫面：`artifacts/speech-cache-window.png`。

### 全 client 原文預配

主視窗按「全 client 預配／進度…」，或者開 `Start-Batch-Dubbing.cmd`。揀「掃描全部／重建佇列」會按目前遊戲文字語言索引整個已安裝 client；按「開始／續跑」喺背景逐句生成。呢個功能目前係原文配音，唔會批量呼叫翻譯 provider。

佇列預設 **有可用 REF 先、default 後**，同一組內保持原有順序，包含遊戲原聲同自訂 reference。排除 11,037 個已有遊戲原錄音工作後，當前佇列有 38,210 個 REF 配音、119,598 個 default，合計 157,808 個，0 個缺失 reference。進度會標示目前係 REF 定 default。舊佇列可先暫停，再執行 `dotnet run --project tools/FF14Sound.Batch -- ref-first data/batch-ja` 重排；保留舊佇列快照同原有快取，唔使重新生成。

2026-10-14 日語掃描：檢查 7,912 張資料表，索引 6,542 張文字表、619,350 行，0 個讀取錯誤。文字來源包括 `quest/`、`custom/`、`cut_scene/` 等 TEXT key 表，以及 `DefaultTalk`、`Balloon`、`NpcYell`、`ContentTalk`、`PublicContentTextData`。呢個係支援格式嘅文字索引，唔等於全部行都係可直接配音嘅 NPC 對白。

按當時角色設定去重後有 **168,845 個配音工作**，合併 6,120 行相同文字／音色。保留但唔生成：22,189 行動態文字、88,495 行未確認講者，另外有空白、非語音、測試／未使用資料、系統／選項及未辨識 key。動態 SeString 保留 macro 原文，唔會刪走玩家名／條件變數後配出殘句。讀取格式及 speaker 連結依據 [EXDSchema 固定版本](https://github.com/xivdev/EXDSchema/tree/e773c41a90aed788cf4c1c48469fa85618ef01fb)，ENpcBase/NpcYell 欄位有 layout 驗證；未支援嘅表同錯誤會寫入報告。

- 索引及分類：`data/dialogue-ja.jsonl`、`data/dialogue-ja.jsonl.report.json`。
- 佇列／狀態：`data/batch-ja/plan.json`、`status.json`；失敗紀錄 `errors.jsonl`。每次重建保存獨立 jobs 檔及當時音色／描述，修改角色設定後重建可沿用仍有效嘅快取。
- 音檔：`data/speech-cache/`，同新版 GUI 即時配音共用。相同角色、文字、語言、seed、音色、描述命中時唔呼叫 TTS；CRLF/LF 差異及多餘空白唔會重做。舊 LocalAppData 快取保留但不會自動搬回。可用 settings.json 嘅 `SpeechCacheFolder` 指定其他資料夾。
- 關閉進度視窗會繼續背景跑。按「暫停」或執行 `Pause-Batch-Dubbing.cmd`，目前一句完成並保存後停止。重新開機／程式中斷後按「續跑」，略過有效音檔；損壞／缺失音檔會重做。唔會自動註冊開機啟動。
- Backend 排隊會讓即時對白優先；已開始嘅背景一句需要完成。剩餘磁碟不足 5 GiB、連續 3 句失敗會停止，可修正後續跑。GPU 長時間預配期間可以隨時暫停。
- GUI 依本輪生成樣本顯示時間及容量粗估；短句、長句、首次模型載入同遊玩負載都會影響估算。呢個規模係長時間工作，唔會假裝一次操作就全部完成。

CLI（原文日語，使用已保存角色設定）：

```powershell
dotnet run --project tools/FF14Sound.VoiceLibrary -- --dialogue-index '<FF14 game/sqpack>' data/dialogue-ja.jsonl ja
dotnet run --project tools/FF14Sound.Batch -- prepare data/dialogue-ja.jsonl data/batch-ja
dotnet run --project tools/FF14Sound.Batch -- run data/batch-ja/plan.json
dotnet run --project tools/FF14Sound.Batch -- pause data/batch-ja
# 驗證真實預生成音檔可由即時配音直接使用，全程禁止 HTTP 請求
dotnet run --project tools/FF14Sound.Batch -- verify-cache data/batch-ja/plan.json
```

驗證覆蓋對白／選項分類、動態 macro 保留、去重、首次遇見 NPC 同預配描述一致、default 無 reference、暫停續跑、損壞快取重做、磁碟保護及背景讓位。C# smoke 及 10 個 Python 測試通過；實機首 3 句生成成功，另用禁止 HTTP 嘅測試確認即時角色選擇直接重用呢 3 個 WAV。亦以實際 FF14Sound.exe 背景入口跑完 3 個已快取工作，再驗證全佇列收到暫停後會完成第 4 句、保存並退出，之後已恢復背景預配。GUI render：`artifacts/batch-window.png`。未將全佇列跑完，亦未逐句試聽。

按「NPC 聲音表 / 無原聲標記」可搜尋名稱／ID、篩選未找到原聲／遇見過／手動無原聲、編輯描述與備註、標記 CV 狀態及匯出 CSV。編輯後按「儲存描述 / 標記」，下一句會套用新描述；快取會隨描述改變失效。

已由 client `ENpcResident` 建立 **31,143 個 NPC ID / 10,648 個相同多語言名稱組**，其中 **10,358 組未有名称配對原聲**。同一組保留所有 NPC IDs；僅有角色顯示名稱嘅 reader 無法區分同名個體。新遇到或有歧義嘅名字另記為顯示名稱設定。

- `data/game-voices/npc-voice-index.json`：GUI 使用嘅完整索引。
- `data/game-voices/npc-voices.csv`：全表，包含四語名稱、NPC IDs、原聲索引狀態、CV 標記、voice description 及描述依據。
- `data/game-voices/npcs-without-matched-voice.csv`：未有名稱配對原聲嘅表。
- `%LOCALAPPDATA%/FF14Sound/npc-voices.json`：你改過嘅描述／標記、遊戲遇見記錄；獨立於生成索引，重建索引唔會覆蓋呢個檔案。

「未找到原聲」唔等於確定冇 CV；初始 CV 標記統一為「未確認」，可手動標「確認無原聲」或「有原聲待配對」。所有未配對名稱已有 `Voice description`：31 個已知角色設定按已核對身份提供專屬演繹，例如酒館店主、行會導師、偵探及助手；身份來源 URL 保存於「角色身份來源」。音高、音質、節奏等聲線仍屬創作建議，唔係官方聲優模仿資料。

其他名字先按明確職務及情緒詞設計（例如受傷守衛、接待員、商人），無身份線索嘅專有名字則穩定分配創作聲線。描述依據會分清「已知身份」、「名稱職務線索」同「身份未確認」。相同名字重建仍得到同一設計；遇見記錄裏舊嘅自動描述會升級，GUI 手動編輯過嘅描述、CV 標記及備註會保留。例：`イベント案内窓口 / GATE Keeper` 用明亮清楚、規則逐點講清嘅活動接待演繹。

2026-10-14：所有生成描述開頭加入 **男／女聲 + 成年／老年／幼年／少年**。按 ENpcResident ID 讀取本機 `ENpcBase` 外觀，Gender 0/1 參照男／女聲，BodyType 3/4 參照老年／年幼模型；一般模型 1 暫用成年演繹，唔等於已確認實際年齡，亦唔會將矮小種族當成兒童。欄位依據：[EXDSchema 固定版本](https://github.com/xivdev/EXDSchema/blob/e773c41a90aed788cf4c1c48469fa85618ef01fb/ENpcBase.yml)、[Anamnesis 外觀編碼](https://github.com/imchillin/Anamnesis/blob/master/Anamnesis/Memory/ActorCustomizeMemory.cs)。讀取時檢查欄位形狀及 byte offsets，版本不相容會報錯而唔讀錯欄位。

同名多個 NPC 必須全部具有效外觀且性別一致，先標為 client 性別依據。資料缺失／混合時先看名字有冇明確男／女、老／幼稱謂；否則穩定分配創作男／女聲及成年演繹，並記錄依據。CSV 追加「聲線性別」、「聲線年齡感」、「性別年齡依據」、「NPC 外觀資料」；保留每個 ID 嘅 Race／Gender／BodyType／Tribe 以便核對。手改過嘅描述繼續優先，唔會自動加詞或覆蓋。

今次 10,358 筆未配對名稱有 6,352 筆男聲、4,006 筆女聲；年齡感分成年 9,678、幼年／少年 485、老年 195。8,055 筆有一致嘅 client 性別欄位，其餘按稱謂或創作選角。例：`ウジェノア / Eugennoix`（1006433）讀到 Race=2、Gender=0、BodyType=1，使用「成年男聲」。舊索引備份在 `artifacts/npc-table-before-traits-20261014`。呢啲係 TTS 風格指令，fallback 按描述直接生成，唔使用 reference。

2026-10-13 描述更新：10,358 筆未配對名稱全部補齊，其中 43 筆名稱組有已知身份來源、2,823 筆有職務線索、7,492 筆屬名字創作；合計 6,831 款不同描述。已核對原有 NPC IDs、四語名稱、原聲索引、CV 標記及備註無改動。更新前 JSON／CSV 備份在 `artifacts/npc-table-before-description-update`。

NPC 表新增「使用 Voice / 配對」欄，按目前遊戲文字語言、clone 版本及自訂設定解析；滑鼠停喺配對欄可看音檔路徑。GUI 匯出 CSV 包括 `Voice mapping`、`Mapping speaker`、`Mapping basis`、`Reference audio`，係開表時嘅實際設定快照。資料夾內由工具產生嘅 CSV 則標明為索引候選／default，唔包含使用者之後設定嘅別名或自訂音檔。CSV 新欄加喺原有欄位之後，保持 UTF-8 BOM、引號轉義及四語名稱。

執行 `Build-NpcTable.cmd` 可單獨重建 NPC 表；`Build-VoiceLibrary.cmd` 亦會建立。索引 CSV 係生成時快照；要包含你之後嘅修改，請從 GUI 匯出。

### 原聲代號顯示「需配對名稱」

呢啲代號已經有 reference，只係未連到遊戲顯示名稱。`Match-VoiceNames.cmd` 額外查 `BNpcName` 戰鬥 NPC 名稱，並用 `Quest` 嘅 script actor／NPC ID 尋找任務候選；重建完整聲音庫時亦會執行。只有英文完整名稱匹配會加入自動別名；部分名稱、同任務出場列表及任務具名 actor 留作候選，唔會直接配到所有同名 NPC。

今次補到 `IMPERIALENGINEER`、`SUSANO`、`ZENOS` 三個代號，具名稱嘅代號由 157 增至 160。例如 `SUSANO` 對到 `スサノオ`；`SOLDIER03608` 嘅任務 actor 指向 `衛兵団の負傷兵 / wounded guard`（ENpcResident 1029397，Small Favors），但需要確認場景。`THORDAN` 有 Archbishop Thordan VII / King Thordan 候選。`TRAITORC02236` 目前只查到該任務出場名單，唔足以確定說話者。

GUI 選擇代號後會顯示 reference 對白，提供「試聽所選角色原聲」及可展開嘅「名稱／任務配對證據」。核對後用原有「配對目前 NPC」記住選擇。完整證據保存在 `data/game-voices/voice-name-candidates.json`，包括來源表／NPC ID／任務識別及是否已套用。資料庫原聲配對仍有歧義時，維持預設聲音。

**已有原錄音交返 GAME：** `data/game-voices/native-voiced-lines.json` 由已驗證音訊存在嘅 VoiceMan 掃描建立，現有 44,402 行四語本地化資料，其中 43,573 行係靜態文字。即時模式按來源語言、角色身份及完整原文比對；配音按鈕、原文／翻譯模式都喺讀取快取前略過已識別原錄音，唔生成、唔播放舊配音、亦唔代播原音檔。翻譯／字幕照常。角色 REF 仍用於佢其他冇聲嘅句子；自訂音色及手動 voice 配對唔會改變原錄音判斷。

新預配佇列同執行中 worker 都會排除原錄音；舊佇列先暫停，再用 `dotnet run --project tools/FF14Sound.Batch -- exclude-native data/batch-ja data/game-voices/catalog.json` 過濾，保留原佇列快照及音檔。重建索引用 `dotnet run --project tools/FF14Sound.VoiceLibrary -- --native-index data/game-voices/catalog.json artifacts/game-voices/voice-clips.json`；完整 `Build-VoiceLibrary.cmd` 亦會建立。client 更新後應先重掃錄音資料。

**現有限制：** reader 未提供即時原聲播放訊號或場景 row ID，以上係資料索引比對，唔係攔截遊戲 sound system。未收錄嘅語音、未辨識／有歧義角色及動態文字未保證識別；唔會將刪咗玩家名等 macro 嘅殘句用作比對。預配知道來源 row 時可直接略過動態原錄音。VoiceMan 以外嘅語音未完整涵蓋。同一角色重複相同句子會一併略過。遊戲讀取沿用 sub repo reader，FF14 更新後可能要更新 offset；尚未完成實際遊戲內端到端驗證。

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

Backend 綁定 `127.0.0.1`。Ollama 可在 GUI 自行輸入本機或遠端 HTTP／HTTPS base URL（預設 `http://localhost:11434`），NPC 名稱及本句原文會傳到所填服務；Google 模式會把原文傳到 Google。語音生成由本機模型處理。

### Ollama 翻譯

GUI「翻譯引擎」選 Ollama，在右側「Ollama API URL（本機／遠端）」自行輸入地址，再按「更新模型」讀取該服務已安裝模型，不會下載。支援自訂 domain、port 及反向代理路徑；填 base URL，唔需要加 `/api/chat`。預選模型為 `qwen3.5:9b`，模型、地址及引擎會自動儲存。改引擎／模型／地址會取消舊對白工作；Ollama 失敗會顯示錯誤，不會悄悄轉用 Google。

Cloudflare Access 使用[官方瀏覽器登入流程](https://developers.cloudflare.com/cloudflare-one/tutorials/cli/)：填 HTTPS URL，按「網頁登入」，在彈出嘅預設瀏覽器完成 email OTP／SSO／MFA。程式透過 `cloudflared access login --no-verbose` 接收該應用嘅使用者 session，成功後自動讀取模型清單，翻譯請求帶 `cf-access-token`。唔需要 Client ID／Secret 或 Service Auth policy；Access application 要允許你嘅使用者登入。已有有效 Cloudflare SSO／cloudflared 快取時，可能直接完成登入。

此機已安裝 cloudflared 2026.2.0；其他電腦可先安裝 Cloudflare 官方 cloudflared（例如 `winget install --id Cloudflare.cloudflared --exact`）。FF14Sound 從標準安裝位置及 PATH 尋找程式，隱藏背景命令視窗，認證網頁照常顯示。可按「取消登入」，或在五分鐘後逾時。

FF14Sound 使用 Windows DPAPI CurrentUser 加密儲存 session 及其 URL，重開可沿用；session 只會經 HTTPS 傳去登入時嘅 base URL 範圍，唔會跟隨 redirect。過期／拒絕時按「網頁登入」更新。搬去另一部機或 Windows 帳戶需重新登入。`cloudflared` 另有其官方使用者快取；「清除本機登入」只清除 FF14Sound 保存嘅 session，唔會登出瀏覽器、刪除 cloudflared 快取或撤銷雲端 session。舊 Service Token 設定不再讀取，下一次儲存設定會移除舊欄位。已通過模擬 API session、URL／有效期檢查及加密／GUI 儲存測試；真實身份認證由你在瀏覽器完成。

使用 [Ollama Chat API](https://docs.ollama.com/api/chat)，`think:false`、4K context、temperature 0.1、keep_alive 10 分鐘。提示指定 FF14 對白、說話者、主客體及否定句；廣東話使用香港口語繁體字。傳送本句、說話者及命中嘅 FF14 詞條，尚未加跨句劇情記憶。不完整輸出／只有思考內容會拒絕配音。首句含模型載入成本，翻譯延遲另加於 TTS；共用顯卡時速度視遊戲及 VoxCPM 負載而定。

### FF14 翻譯詞表

Ollama 及 Gemini 共用 `data/translation-glossary.json`，目前 94,994 項，涵蓋 NPC、敵人、地名、職業、軍團、種族、物品、技能及補充世界觀名稱。每句按原文及講者匹配，最多附上 20 個詞條／2,200 字元 JSON；唔會把整份詞表塞入模型。長名稱優先、英文單字有邊界檢查，同一原文有多個不同譯名時會略過，避免自行猜測。模型收到詞條類別，物品名稱只應用於物品語境。翻譯狀態顯示命中數，tooltip 可看本句對照。Google Translate 網頁引擎不支援詞表提示。

來源為 [InfSein/ffxiv-datamining-mixed](https://github.com/InfSein/ffxiv-datamining-mixed/tree/935169e14dfa0525bd4f8f41cea53ce5d21a5c6d)，固定 commit，以資料表及 row ID 對齊日／英／德／法／國服中文。繁體名稱由國服中文經 Windows NLS 轉字，**唔係台服官方譯名表**；粵語沿用繁體名稱，句子仍用香港口語。來源版本可能落後本機 client；缺少／未命中嘅名稱會交由既有提示處理，唔保證模型一定遵從詞表。`data/translation-lore.json` 收錄帶來源連結嘅補充名稱。

可在 `data/translation-glossary-overrides.json` 加入自訂譯名；檔案改動後下一句重讀，唔使重開。獨立部署可在 `%LOCALAPPDATA%/FF14Sound/translation-glossary-overrides.json` 放同格式檔案，再重開一次選用。`yue` 可另外指定；未填則沿用 `zh-TW`。例：

```json
[
  {
    "Id": "custom:steel-vigil",
    "Category": "PlaceName",
    "Names": {"ja": "スチールヴィジル", "en": "Steel Vigil", "zh-TW": "鋼衛塔", "zh-CN": "钢卫塔"}
  }
]
```

重建詞表：`VoxCPM/.venv/Scripts/python.exe tools/build_translation_glossary.py`。下載只在重建時發生，原始表放 `artifacts/glossary-source/`，JSON 記錄 commit、欄名及來源 SHA-256。詞表會隨 C# build/publish 複製到 `Glossary/`。測試涵蓋真實名稱對照、長詞／字界、歧義略過、長度上限、自訂覆蓋重載，以及兩個引擎實際請求 payload。

2026-10-15 本機 qwen3.5:9b 已實測三句日文 → 廣東話，預熱後 0.42、0.46、3.22 秒；Ollama 駐留時，VoxCPM 串流另測首段 0.161 秒。首輪含模型載入的請求約 92 秒，不代表穩態速度。輸出存於 `artifacts/ollama-translation-results.json`；短句主客體／否定意思有保留，但長句仍出現不標準人地名音譯及不自然用詞，未當成翻譯品質已解決。可在 GUI 改試其他已安裝模型；大型模型可能無法與遊戲及 TTS 同時全放入顯存。

### Gemini（google-genai SDK）

GUI 翻譯引擎選「Gemini · Google」，預選 `gemini-3.8-flash`；「更新模型」透過已授權 key 讀取可用文字模型。Ollama、Gemini 的模型設定各自保留並自動儲存。C# 只呼叫本機 `8808/api/gemini/*`，Python backend 使用官方 [google-genai SDK](https://googleapis.github.io/python-genai/) `Client.aio.models.generate_content`，唔係自行拼 Google REST 請求。

Key 預設讀取專案 `APIKey/qqDragon.txt`（單行純 key），亦可在右側選擇其他 key 檔案；設定只儲存路徑。`APIKey/` 已加入 `.gitignore`，key 不會寫入日誌、網址或 GUI。SDK 固定使用 Google 官方端點；本句原文及角色名稱會傳到 Google。廣東話沿用 FF14／香港口語提示；Gemini 3 使用 LOW thinking，思考部分及未完成結果唔會送去配音。換句取消會關閉舊 SDK 請求；已送出嘅雲端請求是否計費仍由 Google 決定。

依賴版本：`google-genai==2.30.1`、`pydantic==2.12.5`；既有環境安裝用 `Setup-Gemini.cmd`，之後重啟本機 backend。`Setup-VoxCPM.cmd` 亦會安裝呢組依賴。WSL 模型本身唔使改。

2026-10-15 已驗證 SDK 認證及模型清單；實際生成回覆 **HTTP 402 / RESOURCE_EXHAUSTED：預付額度已用完**。需在 [AI Studio 專案／計費](https://ai.studio/projects) 補充額度後重試，尚未取得 Gemini 譯文或延遲數據。GUI 會顯示額度錯誤，唔會自動重試或轉用其他引擎。SDK 模擬回應、思考過濾、半截輸出拒絕、取消清理、key 不外洩及 C# UI／設定測試另有覆蓋。

- `GET /api/health`：server、model 狀態及支援語言。
- `POST /api/warmup`：預載模型。
- `POST /api/tts`：例如 `{ "text": "你好呀", "language": "yue", "reference_audio": "H:/voices/npc.wav", "session_id": "client-a", "revision": 1 }`，回傳 PCM16 WAV。
- 文字生成可省略 `reference_audio` 或設為 `null`，例如 `{ "text": "你好呀", "language": "yue", "reference_audio": null, "control": "成年男聲，沉穩。" }`；冇 reference 時唔可提供 transcript。
- `POST /api/cancel`：例如 `{ "session_id": "client-a", "revision": 2 }`；較舊 revision 回 409。每個 app instance 使用獨立 session。

```powershell
dotnet build FF14Sound.slnx
dotnet run --project tests/FF14Sound.Smoke -- H:/PJ/FF14Sound/artifacts/gui-library.png
# 開 backend 後，加 --tts 實測 Tataru 翻譯 + 遊戲 Y'shtola reference + 廣東話生成
dotnet run --project tests/FF14Sound.Smoke -- H:/PJ/FF14Sound/artifacts/gui-library.png --tts
VoxCPM/.venv/Scripts/python.exe -m pytest VoxCPM/tests/test_ff14_speech.py -q
```

C# smoke 驗證名稱歧義、語言配對、自訂覆蓋、快取失效、取消結果及 WPF render；Python 測試用 fake model 驗證 WAV API、語言／clone 輸入、預載及同時請求嘅過期處理。真實模型測試與單元測試分開。

新增 fallback 驗證：C# smoke 覆蓋未知／歧義名稱 fallback、無 catalog 仍可文字生成、忽略舊 default reference／transcript、API 傳送 null reference、NPC 描述與標記重啟後保留、多語名稱共用描述、CSV 匯出與描述改動後快取失效。Python 測試覆蓋省略／null／空白 reference 嘅文字生成。加 `--fallback-tts` 可實測 Eugennoix 日語及廣東話配音，輸出 `artifacts/default-no-clone-eugennoix-ja.wav`、`artifacts/default-no-clone-eugennoix-yue.wav`；畫面見 `artifacts/gui-default-no-clone.png`。

設定／字幕驗證：C# smoke 以獨立測試設定檔驗證 GUI 語言與配音模式改動會自動寫入、舊設定相容、原文配音唔等翻譯、翻譯失敗時嘅兩種配音行為、字幕換句清除舊翻譯、外觀回呼及移除螢幕後位置回復。視窗渲染：`artifacts/gui-subtitles.png`、`artifacts/subtitle-window.png`；呢啲係 WPF render 測試，唔係遊戲內覆蓋視窗嘅實測。

配對／描述驗證：C# smoke 覆蓋自訂／手動／自動／default 嘅顯示優先次序、缺少原聲語言時顯示 default、NPC 表按來源語言解析別名、舊通用描述升級及手動修改保留、已知角色來源／受傷守衛演繹／未知名字穩定分配。左邊 voice 顯示及 NPC 表已渲染檢查（`artifacts/gui-voice-mapping.png`、`artifacts/npc-table.png`）。兩份 CSV 經讀回核對欄位、行數、轉義及資料保留，另以 Artifact Tool 匯入及抽樣渲染描述欄。

2026-10-13 驗證：solution build 0 warnings / 0 errors；8 個 Python 測試及 C# smoke 通過。RTX 3090 實測已完成「Tataru 英文 → 廣東話翻譯 → 自動配對 Y'shtola 日語 reference → VoxCPM WAV」，模型預載後該句生成用時 11.93 秒，輸出 `artifacts/integrated-yshtola-yue.wav`。此數字唔包括翻譯／首次模型載入，亦唔代表所有句子嘅速度。WPF 畫面渲染見 `artifacts/gui-library.png`；尚未試聽評估音色相似度，亦未連接運行中 FF14 驗證。

TataruHelper 使用 MIT license，build 複製到 `Licenses/TataruHelper.txt`；VoxCPM 使用其 sub repo 嘅 Apache-2.0 license，模型另依模型授權使用。
