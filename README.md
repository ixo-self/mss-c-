# ✦ 永恆深淵 · 終端中樞 (AbyssCore C# .NET 8) ✦

高效能 Discord API 自動化管理與伺服器架構重組開源專案。

基於現代 C# (.NET 8) 架構打造，整合原生 `SocketsHttpHandler` 連線池化技術、主動式動態令牌桶（Proactive Token Bucket）與兩階段全並發流水線，專為極致吞吐量與低延遲通訊設計。

---

## ✦ 專案核心特色

* **主動式動態令牌桶（Proactive Token Bucket）**：
  * 本地主動解析 `X-RateLimit-Bucket` 狀態，預先在本地微秒級睡眠，告別 429 衝撞與 RTT 浪費。
* **原生連線池調度（SocketsHttpHandler）**：
  * 長效 TCP 連線池化復用（`PooledConnectionLifetime`），徹底根除 Windows 通訊埠耗盡（Socket Exhaustion）。
* **三段式階梯成員清理**：
  * ① 優先拔除所有 Webhook 通道 ➔ ② 優先並發封鎖外部低階 Bot ➔ ③ 全面處分身分組在機器人自身之下之成員。
* **兩階段全並發重構流水線（Two-Phase Pipeline）**：
  * **Phase 1**：舊頻道清理、身分組刪除、伺服器更名換頭像同步並發執行。
  * **Phase 2**：同步自動建立 **150 個迷宮頻道** 與 **50 個迷宮身分組**。
* **全量架構備份與極速還原（System.Text.Json）**：
  * 一鍵將伺服器身分組、類別、頻道、權限覆寫矩陣導出為 JSON，並支援多並發極速原樣還原。
* **全功能矩陣**：
  * 支援 7 大世界觀主題預設集切換。
  * 支援頻道/身分組/成員暱稱三向一鍵批量更名。
  * 支援文字頻道多輪廣播與全體禁言處分。

---

## ✦ 系統需求與環境

* 作業系統：Windows 10 / 11、Linux、macOS（跨平台支援）
* 執行環境：[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高版本

---

## ✦ 快速開始指南

### 1. 複製專案與建立設定檔
```bash
git clone https://github.com/your-username/AbyssCore.git
cd AbyssCore
```

將設定範本複製為正式設定檔：
* 複製 `config.example.json` 為 `config.json`，並填入您的 Discord 機器人 Token。
* 複製 `texts.example.json` 為 `texts.json`（可根據需求自訂頻道與身分組名稱清單）。

```json
// config.json
{
  "token": "YOUR_DISCORD_BOT_TOKEN"
}
```

### 2. 編譯與執行
使用 .NET CLI 直接啟動：
```bash
dotnet run
```
或在 Windows 環境下直接雙擊執行：
```cmd
啟動_CSharp終端.bat
```

---

## ✦ 終端操作功能代碼對照表

```text
═════════════════════════════════════════════════════════════════════
  [1] ◈ 批量建立迷宮頻道 (最高 500 個，主動令牌桶控速)
  [2] ❖ 批量建立迷宮身分組 (最高 100 個，自訂名稱池)
  [4] ✦ 全景深度重構 (三段式階梯清理 + 自動建150頻道+50身分組 + 改名換頭像)
  [5] ✧ 批量發送迷宮訊息 (全服文字頻道多輪廣播)
  [6] ◈ 全服批量禁言處分 (可處生成員設定禁言分鐘數)
  [7] ❖ 全服批量統一名稱 (一鍵批量修改頻道 / 身分組 / 成員暱稱)
  [E] ❖ 滿額表情包置換 (清理舊表情並灌入 50 個專屬可愛表情包)
  [M] ✦ 切換主題範本預設集 (7大世界觀：永恆深淵/廢墟城區/救贖序章...)
  [B] ✦ 立即備份伺服器完整架構 (身分組/頻道/排序 JSON 導出)
  [R] ✦ 一鍵架構復原 (從本地 backups/ 備份檔多併發極速還原)
  [8] ◈ 切換目標伺服器
  [0] ✖ 退出系統
═════════════════════════════════════════════════════════════════════
```

---

## ✦ 安全與隱私聲明 (Security & Privacy)

* 本開源專案**絕不內建任何 Token 或私密金鑰**。
* `.gitignore` 已嚴格配置排除 `config.json`、`texts.json` 與 `backups/` 目錄，確保上傳至 GitHub 時不會洩漏任何憑據或伺服器架構資料。
* 僅供軟體架構研究、分散式高並發連線控制與合法授權之伺服器維護測試使用。請勿用於任何違反 Discord 服務條款之未授權行為。

---

## ✦ 開源授權條款

本專案採用 [MIT License](LICENSE) 授權發布。
