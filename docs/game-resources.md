# 重建本機遊戲資源

Fresh clone 冇遊戲音檔係正常嘅。資源工具會由你已安裝嘅 FF14 client 讀取 SqPack，重建 app 需要嘅檔案；唔需要向作者索取聲音庫。

## 最簡單用法

先準備 .NET 10 SDK、PATH 上嘅 `ffmpeg`，同已安裝嘅國際版 client。喺 repo 根目錄執行，會自動偵測預設安裝位置：

```powershell
.\Restore-GameResources.cmd
```

偵測順序係 Square Enix 版，再試 Steam 版；使用 Windows 嘅 `ProgramFiles(x86)` 位置，通常係：

| 版本 | `game\sqpack` 路徑 |
| --- | --- |
| Square Enix | `C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack` |
| Steam | `C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XIV Online\game\sqpack` |

亦可以明確指定：

```powershell
.\Restore-GameResources.cmd -SqPack "C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"
```

路徑內應該有 `ffxiv/*.index`，以及已安裝資料片嘅 `ex1/` 等目錄。亦可先設定 `$env:FF14_SQPACK = 'C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack'`，之後直接執行 `.\Restore-GameResources.cmd`。

`-SqPack` 優先於環境變數，兩者都優先於自動偵測。未指定且預設位置搵唔到先詢問；`-CheckOnly` 則直接報錯，唔會等待輸入。明確指定嘅路徑無效會報錯，唔會靜靜地改用另一個 client。自訂磁碟／Steam library 請明確指定。

只檢查路徑、SDK 同 ffmpeg，唔掃描或寫入資源：

```powershell
.\Restore-GameResources.cmd -SqPack "C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack" -CheckOnly
```

`CheckOnly` 唔會讀完整遊戲資料，成功唔代表 client schema 一定相容。正式掃描先會驗證資源能否讀取。

每次完整重建都會重新掃描 client，再匯出 reference、名稱配對、NPC 表同原錄音索引。唔會重用另一個 client 嘅舊 `voice-clips.json`。完成後重開 FF14 Voice；正常放喺 repo `data/` 嘅 catalog 會自動找到。

## 會生成啲乜？

| 檔案／資料夾 | 用途 |
| --- | --- |
| `artifacts/game-voices/voice-clips.json` | 已驗證存在嘅遊戲語音、來源路徑及對白 |
| `artifacts/game-voices/scan-summary.json` | 掃描範圍、數量及錯誤報告 |
| `artifacts/game-voices/characters.json` | 按說話者代號分組嘅掃描結果 |
| `data/game-voices/catalog.json` | 聲線、別名、reference 及來源 client |
| `data/game-voices/references/<代號>/<語言>.wav` | 24 kHz、mono、PCM16 reference；通常挑選完整 5–12 秒片段，最長 12 秒 |
| `data/game-voices/native-voiced-lines.json` | 略過已有遊戲原錄音嘅對白索引 |
| `data/game-voices/npc-voice-index.json` | NPC 名稱、多語別名及聲線資料 |
| `data/game-voices/npc-voices.csv` | NPC 表 |
| `data/game-voices/npcs-without-matched-voice.csv` | 未有名稱配對原聲嘅 NPC；唔等於證實無 CV |
| `data/game-voices/voice-name-candidates.json` | 自動／待確認名稱配對證據 |

以上輸出已由 `.gitignore` 排除。模型喺 `.cache/`；生成配音喺 `data/speech-cache/`；個人設定、手動配對及 NPC 修改喺 `%LOCALAPPDATA%\FF14Sound/`。

## 官方繁中 → 廣東話

需要同時安裝國際版同繁中版 client。國際版提供日文對白及 ID，繁中版提供可配對嘅官方繁中句子：

```powershell
.\Restore-GameResources.cmd -SqPack "C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack" -TraditionalSqPack "E:\Games\FFXIV-TC\game\sqpack"
```

除基本聲音庫外，會自動建立：

- `data/dialogue-ja.jsonl` 同 `.report.json`：日文對白索引及來源 client。
- `data/dialogue-ja-tc.jsonl` 同 `.report.json`：日文／繁中配對及未配對原因。

之後喺 app 揀「日文 ID → 官方繁中 → 廣東話」，遊戲文字選日本語、翻譯目標選廣東話。版本差異、動態文字及有歧義嘅句子會回退到日文直接翻譯。未有繁中 client 可以跳過此功能。

## 原文預配對白索引

單一語言可以用 CMD 入口：

```powershell
.\Restore-GameResources.cmd -SqPack "C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack" -DialogueLanguages ja
```

多語言用 PowerShell 腳本（陣列參數）：

```powershell
& .\tools\Restore-GameResources.ps1 -SqPack 'C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack' -DialogueLanguages ja,en,de,fr
```

此步只建立索引，唔會開始大量 TTS。預配喺 app「全 client 預配／進度…」開始；詳見 [進階指南](advanced.md)。

## 更新與單獨重建

遊戲更新、轉 client 或重新安裝後，重新執行完整命令。完成前唔好開 app 使用同一份正在更新嘅資料；亦先暫停背景預配。工具會覆寫生成嘅 reference 同索引；如果手動挑選過 reference，先另存備份。中途失敗會回傳非零 exit code，請查看報告後重新執行；輸出可能只完成部分，唔會顯示成功。

掃描可能遇到讀唔到嘅候選 SCD。預設會警告並保留詳細報告，用其餘已驗證音訊繼續建庫；呢啲略過項目唔代表已驗證無原聲。加 `-StrictScan` 則遇到任何掃描錯誤都停止。零筆有效音訊、catalog 錯誤或下游工具失敗，兩種模式都會停止。

原有捷徑仍然可用，全部接受自訂路徑，亦支援 `FF14_SQPACK`：

```powershell
.\Build-VoiceLibrary.cmd "C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"
.\Build-NpcTable.cmd "C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"
.\Match-VoiceNames.cmd "C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"
```

第一個等同完整聲音庫重建；後兩個需要已有 `catalog.json`，只更新指定部分，唔代替 client 更新後嘅完整重掃。

可用 `-OutputRoot 'E:\ff14-voice-resources'` 將生成資料放另一個根目錄（其下仍分 `data/` 同 `artifacts/`）。聲音庫要喺 GUI 指定新 catalog；對白／繁中索引需放回 repo `data/` 或使用對應 CLI 明確指定，唔會自動跟隨 catalog 路徑。工具拒絕把輸出放入遊戲 `game/` 之內。

## 低階 CLI

適合只想重建對白、或自行串接流程嘅開發者；第一行掃描必須用同一個 client：

```powershell
dotnet run --project tools/FF14Sound.VoiceScan -- '<game/sqpack>' artifacts/game-voices
dotnet run --project tools/FF14Sound.VoiceLibrary -- '<game/sqpack>' artifacts/game-voices/voice-clips.json data/game-voices
dotnet run --project tools/FF14Sound.VoiceLibrary -- --dialogue-index '<game/sqpack>' data/dialogue-ja.jsonl ja
dotnet run --project tools/FF14Sound.VoiceLibrary -- --traditional-dialogue data/dialogue-ja.jsonl '<TC game/sqpack>' data/dialogue-ja-tc.jsonl
```

繁中配對會讀取日文索引旁嘅 `.report.json`，並再次核對來源 client，請保留兩份檔案及原安裝位置。

## 排錯及範圍

- `dotnet`／SDK 找不到：確認安裝 .NET 10 **SDK**，重開 terminal，再執行 `dotnet --list-sdks`。
- `ffmpeg` 找不到：將其 `bin` 資料夾加入 PATH，確認 `ffmpeg -version` 成功。
- 找不到 `ffxiv/*.index`：請選 `game/sqpack`，唔係遊戲根目錄／launcher。
- 掃描或 catalog 有 errors：查看畫面指出嘅 JSON 報告。掃描候選讀取失敗預設會警告並略過；catalog／對白建立錯誤會停止。需要嚴格檢查整份掃描時加 `-StrictScan`。
- 掃描覆蓋 VoiceMan cutscene sheets，唔保證涵蓋全部 NPC 或原錄音。數量隨 client 版本及已安裝資料片而變，唔應以作者開發機嘅數量作必須相等嘅標準。
- 唔會下載或解鎖未安裝遊戲內容，亦唔會代下載／生成全 client AI 配音。請只提交程式碼、文件同你要分享嘅示範，唔好把生成聲音庫加入 Git。
