# 階段報告：nxnative 原生路線，BotW 1.6.0 在 iPad 上穩定 30 FPS（2026-10-19）

## 結論
- **iPad（M1 iPad Pro 11"）**：nxnative 原生版（NSO 離線改寫 + 宿主 HLE + NVN→Metal 4）跑你的 1.6.0 存檔，
  **連續 23 分鐘、42,000 幀零崩潰、零掉幀**，每幀 33.3 ms（p95 35.3 ms、p99 33.35 ms），GPU 23 ms，
  沒有熱降頻；記憶體 2.9 GB（Ryujinx 路線在同一台 iPad 是 6.3 GB）。
- **Mac（M4 Air）**：同樣穩定 30 FPS，15 分鐘長測試 25,500 幀零崩潰；GPU 從 25 ms 降到 13–16 ms。
- 對照 Ryujinx 路線（BOTW-PC iPad 版）：30 FPS 模式會在 8–17 分鐘後降到 24–27 FPS；原生版沒有這個問題。

## 修好的問題（每一項都有直接證據）
| 問題 | 證據 | 修正 |
|---|---|---|
| 記憶體順序：遊戲用無 acquire 語意的 `LDXR/STXR` 自旋鎖領取工作，Apple 晶片把鎖後的讀取提前，兩條執行緒領到同一份工作 | 硬體觀察點 + 事件時間軸：同一項目被兩條執行緒在 4 µs 內各處理一次 | 改寫器把所有 exclusive 存取改成 `LDAXR/STLXR`（main 36,687 處） |
| 每核心互斥：2,845 處用 `GetCurrentCoreNumber` 索引無鎖的每核心資料 | 同一核心的 bump arena 被兩條宿主執行緒同時配置 | 每核心排程（Horizon 規則：SVC 返回即讓給高優先權、優先權繼承、宿主端等待不佔核心） |
| 0.3–1 秒卡頓：核心 0 搶不到工作領取鎖（活鎖） | 卡頓當下的執行緒傾印 | 重試前退避約 1 µs |
| GPU 過慢：每次 `UpdateUniformBuffer` 都切斷 render pass，一幀 370 個 pass | 逐 pass 傾印：同一目標連開上百個只有 1 次繪製的 pass；handheld 也只省 4% | 常數緩衝改名（更新寫進新版本、提交結束寫回）+ TBDR 優化；pass 降到 142 個 |

## 實測
| 平台 | 設定 | 每幀 p50 / p95 | GPU | 結果 |
|---|---|---|---|---|
| iPad M1 | 修正前 | 66.7 ms（15 FPS） | 48 ms | 可玩但慢 |
| iPad M1 | 全部修正 | 33.3 / 35.3 ms | 23 ms | 23 分鐘零崩潰零掉幀 |
| Mac M4 Air | 修正前 | — | 25 ms | 每 30–50 秒崩潰 |
| Mac M4 Air | 全部修正 | 33.4 / 35.0 ms | 15–16 ms | 7 次測試、最長 15 分鐘零崩潰 |

畫面：標題、遊戲中、背包選單逐一截圖確認與修正前一致。

## iPad 上的使用方式
- 主畫面的 **BotW Native**（bundle ID `local.botwpc.nxnative`，與 BOTW-PC 並存）。
- 遊戲資料在 app 的 `Documents/romfs`，存檔是你的 1.6.0 存檔副本（`Documents/save`），日誌 `Documents/log.txt`。
- 免費開發者描述檔約 7 天到期，到期後需在 Mac 上重新簽章安裝（遊戲資料與存檔會保留）。

## 已知限制
- 記憶體緩慢增長（23 分鐘 +90 MB），更長時間遊玩待觀察。
- 遊戲內按鍵提示是 Nintendo 標示（Xbox 手把的 A/B 位置與 Switch 相反，依 Switch 位置操作）。
- 60 FPS 尚未移植到 1.6.0；音效有輸出，但尚未實際聆聽確認正確性。

## 變更（nxnative 本機分支 `botw-1.6.0`，未推送）
排程與診斷、改寫器的順序修正與退避、常數緩衝改名、`tbdr` / `core_lock` 設定檔鍵、`build_ipa.sh` 的自簽章（`DM_SIGN_TEAM`、`DM_IOS_CPU`、`DM_NO_ROMFS`）。
沒有提交任何任天堂的二進位檔、資源或金鑰；原始存檔、BOTW-PC 與 Cemu 都沒有動。
