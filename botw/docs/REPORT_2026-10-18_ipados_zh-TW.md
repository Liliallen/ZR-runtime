# BOTW-iPad 報告（2026-10-18）

目標裝置：iPad Pro 11 吋第 3 代（M1、8 GB、iPadOS 26）。遊戲：Switch 版《薩爾達傳說 曠野之息》1.6.0 + DLC 3.0，使用者原有存檔。
分支 `ipados`（自 `cemu-switch` 分出），共 22 個獨立提交。英文技術文件：`botw/docs/IPADOS.md`。

## 結論

- **在 iPad 上實際可玩、不需要 JIT**：遊戲程式碼在 Mac 上預先翻譯成 ARM64（AOT 映像，271,614 個函式），
  簽進 App 的 `__TEXT`，啟動時不需要除錯器、StikDebug 或任何越權手段。已在裝置上從標題畫面載入存檔、
  進入初始台地實際遊玩（腳本輸入：走、跑、跳、轉鏡頭、地圖、背包，之後待機）。
- **30 FPS（預設）在正常溫度下穩定**：平均 30.0、中位數 30.2、1% low 約 20、P95 約 44 ms、P99 約 46 ms。
- **長時間遊玩會因溫度降頻**：充電中約 8–17 分鐘後溫度狀態到 serious，降到 24–27 FPS（遊戲速度 0.8–0.93）。
- **60 FPS 模式達不到**：實測平均 35、中位數 37；瓶頸是 CPU 端的渲染執行緒鏈，不是 GPU。90/120 未嘗試。

## 架構選擇（依證據）

| 候選 | 判斷 |
|---|---|
| BOTW-PC Runtime V2（本專案，Ryujinx 衍生） | 採用：能直接用使用者的 Switch 1.6.0 檔案與存檔，已有 BotW 專屬修正與 macOS 實測 |
| MeloNX | 證明 Ryujinx 在 iOS 的路線可行；授權不明確，只參考設計、不複製程式碼 |
| Eden / yuzu（dynarmic） | iOS 移植沒有能跑的遊戲紀錄、無直譯器，GPL |
| Cemu / MeloCafe | 需要 Wii U 版檔案（手上沒有），與 Switch 存檔不通用 |

圖形：Vulkan → MoltenVK 1.2.0 → Metal（原生 Metal 後端在 M4 上慢 25–30%）。

## iPadOS 的 JIT 問題與解法

- iPadOS 不給開發中 App JIT 權限；只有除錯器附加過（CS_DEBUGGED）才能執行動態產生的程式碼。
- 實作三種模式：**AOT（無 JIT，預設）**、JIT（RX + RW 別名對映，需 StikDebug 或 Mac 上的 `run.sh`）、
  混合（AOT + 未涵蓋函式用 JIT）。
- AOT 未涵蓋的函式：遊戲暫停並提示需要 JIT，而不是當機。本輪在裝置上抓到兩個（都是 switch 跳轉表），
  已修正偵測邏輯；修正後 15 + 5 + 5 + 5 分鐘的裝置測試、以及 Mac 上兩個 10 分鐘場景都沒有再遇到。
- 新增 `botw/ipados/aot_image.sh`：一個指令從使用者自己的遊戲重建 AOT 映像（約 5 分鐘），
  已驗證能重現與裝置上使用的完全相同的映像。

## 本輪的關鍵修正

| 問題 | 原因 | 修正 | 效果 |
|---|---|---|---|
| 載入存檔時被系統殺掉（7.72 GB） | Ryujinx 對新的 guest heap 頁面全部寫零，使約 2.7 GB 的 heap 全部常駐 | 改成解除對映時清零，新頁面本來就是零 | macOS 9.06 → 6.53 GB；iPad 穩定 6.2–6.6 GB，剩餘 1.4–1.8 GB；FPS 不變 |
| 畫面全黑 | FXAA / 縮放濾鏡在 iOS 輸出黑畫面 | iPad 預設關閉（也省 GPU） | 正常顯示；根因未找到，列為已知問題 |
| 執行緒空轉耗電 | .NET 事件預設先自旋 35 輪才睡 | iOS 改為直接睡（桌面不變） | 同樣 30 FPS 下 CPU 2.53 → 2.31 核（−9%） |
| 無法分析 CPU 用量 | NativeAOT 在 iOS 不設原生執行緒名稱 | 主要執行緒自行命名 + `BOTWPC_THREADLOG` | 可逐執行緒看 CPU |
| 開啟 Vulkan 除錯即崩潰 | 委派在 NativeAOT 缺少封送資料 | 改用函式指標 | 已在裝置驗證 |
| 先前已完成 | MoltenVK 在 iOS 未啟用 argument buffers（BotW 需要超過 31 個 buffer）；shader 快取吃記憶體 | 兩個 MoltenVK 修補檔（`botw/ipados/patches`） | 能渲染；−690 MB |

## 裝置實測數據

測試條件：無 JIT、使用者存檔、掌機模式 720p、腳本化戶外流程；iPad 全程接著充電（所以沒有電池耗電數據，且充電會額外發熱）。

| 模式 | 平均 FPS | 中位數 | 1% low | P95 | P99 | 每幀 GPU 忙碌 | CPU | 記憶體 |
|---|---|---|---|---|---|---|---|---|
| 30 FPS，正常溫度 | 30.0 | 30.2 | 20.1–20.4 | 44.3–44.5 ms | 45.7–46.1 ms | 24.4–24.5 ms | 2.31 核 | 6.25–6.35 GB |
| 30 FPS，降頻後 | 24–27 | 26.6–28.4 | 14–17 | 50–53 ms | 56–60 ms | 約 24 ms | 約 4.2–4.5 核 | 約 6.3–6.5 GB |
| 60 FPS 模式 | 35.0 | 37.2 | 18.6 | 41.5 ms | 46.5 ms | 19.3 ms | 3.55 核 | 6.48 GB |

- 30 FPS 的 P95/P99 偏高：遊戲大約每 10 幀會有一幀晚交出（45 ms），接著兩幀補回（28、27 ms）；macOS 上同樣有（P99 39.7 ms），屬遊戲本身的節奏。
- CPU 分布（正常溫度）：GUI.RenderLoop（Vulkan 後端 + MoltenVK 編碼）40–44%、GPU.MainThread（Maxwell 命令處理）37–40%、主要 guest 執行緒 25–28 / 21–25 / 15–19%。
- 降頻時 CPU 時間上升，是因為時脈降低、同樣工作耗時更久，不是空轉。

## 交付物

- `/Applications/codes/BOTW/dist-ipados/BOTW-iPad.ipa`（117 MB，已簽名，可安裝到這台 iPad）
  與 `dist-ipados/build/BOTWiPad.app`。**IPA 內含由你的遊戲翻譯出的程式碼，只供自己的裝置使用，不可散布。**
- 免費開發者帳號的描述檔 **2026-10-25 到期**，到期後重跑 `build.sh` 即可。
- 腳本：`build.sh`（建置與打包）、`aot_image.sh`（重建 AOT 映像）、`push_data.sh`（把金鑰、存檔、遊戲複製到 iPad，來源只讀不寫）、
  `run.sh`（Mac 端 JIT 啟動）、`aot_cover.sh`（擴充 AOT 涵蓋）。
- 原有存檔、BOTW-PC 現有版本、Cemu 安裝都沒有被覆蓋；任天堂的程式碼、資源、金鑰都沒有提交進 git。

## 未在裝置上驗證（需要有人實際拿著 iPad）

- 實體手把（Xbox / PlayStation / MFi）、震動、陀螺儀瞄準：已實作，但沒有實體測試。
- 觸控按鍵與設定選單（L3+R3）的實際操作：畫面有顯示，操作未測。
- 裝置上的 JIT / 混合模式：程式碼已在 Mac 上測過；透過 Wi-Fi 用除錯器啟動時，lldb 讀取 700 MB 映像卡住，未完成量測。
- 電池耗電：測試全程接著充電。
- FXAA / 縮放濾鏡的黑畫面根因。
- AOT 長尾：在沒測過的區域仍可能遇到未涵蓋的函式（會暫停並提示），把位址加進 `aot_image.sh` 的額外清單重建即可。

## 建議的下一步

1. 你實際拿著 iPad 玩一段：確認手把、觸控、陀螺儀、震動；若遇到「未涵蓋函式」提示，把畫面上的位址告訴我。
2. 不接充電線玩 20–30 分鐘，看電量下降和降頻時間點（App 的 `Documents/botw_stats.csv` 會記錄）。
3. 要提升持續效能，下一個目標是 CPU 端的渲染鏈（GPU.MainThread → RenderLoop → MoltenVK），這也是 60 FPS 的唯一路徑。
