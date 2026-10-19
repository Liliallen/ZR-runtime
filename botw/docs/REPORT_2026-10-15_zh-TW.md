# BOTW-PC 第四輪效能報告（2026-10-15）：模擬器架構診斷、逆向工程、Ryujinx 核心修改

## 結論（先講）

- **保留兩項核心修改，實測有效。** 用你剛玩過的存檔（W2 場景：初始台地東側戶外、黃昏），新舊版交錯各跑 4 次：
  **平均 FPS 85.2 → 87.0（+2.2 %，95 % CI +0.13 .. +3.57）**，**GPU 命令執行緒 9.38 → 8.95 ms/幀（−4.5 %）**，
  遊戲速度 1.000、畫面一致、存檔完整。
- **120 FPS 在這個場景用目前證實有效的手段到不了。** 還差約 3.2 ms/幀。GPU 命令執行緒是瓶頸（延遲 1.47 ms 會讓幀時間
  變長 1.14 ms），但 render 執行緒只剩約 1.4 ms 餘裕，而且兩種「把 GPU 前端拆成兩條執行緒」的架構原型都比原本慢。
  比較實際的下一個上限是 92–96 FPS，要靠好幾個各 0.1–0.3 ms 的單執行緒改進疊起來。
- **Ryujinx 架構確實有額外開銷，但它分散在每個命令字和每次 draw，沒有一個大熱點。** yuzu/Eden/Cemu 裡能搬的演算法，
  能搬的這輪已經搬了（buffer page table、binding memo），其餘不是實測無效，就是靠「跳過同步／讀舊資料」換速度，不能用。

---

## A. 模擬器架構診斷

**W2 場景**：你的存檔（05:55 快照，每次測試前還原到拋棄式資料夾），1080p、DLSS 關、120 FPS 模式。每幀約 5 000 次
draw、25 萬個 GPU 命令字、1.4 萬次 macro 呼叫。

### GPU 命令執行緒的時間花在哪（主機端取樣，忙碌時間的比例）

| 項目 | 佔比 | 來源 |
|---|---|---|
| Draw（幾乎都經 HLE draw macro） | 57.7 %（含內部） | BotW 的 draw 數量 × Ryujinx 每次 draw 的處理 |
| Buffer binding commit | 22.6 % | **Ryujinx**：每次 draw 重新解析/同步所有綁定 |
| GPFIFO 解碼 + method 分派 | 19.2 %（自身） | **Ryujinx**：每個命令字約 8.7 ns |
| Texture binding commit | 10.1 % | Ryujinx：每次 draw 從 constant buffer 重讀 handle |
| Shader 狀態更新 | 6.2 %（其中 shader 二進位逐位元組比對 2 %） | **Ryujinx**：每次換 shader 都比對整段程式碼 |
| Buffer 範圍搜尋（二分搜尋 + 寫鎖） | 4.5 % | **Ryujinx**（已用 page table 修掉） |
| VirtualProtect 重新保護頁面 | 3.3–4.7 % | Ryujinx 用頁面保護追蹤 CPU 寫入；BotW 每幀用 CPU 寫 GPU 資料 |

### 哪些是哪一方造成的

| 類別 | 內容 | 證據 |
|---|---|---|
| Ryujinx 架構的多餘工作（已移除） | 72 % 的 uniform buffer 設定是「設成原本就有的值」卻每次重新翻譯位址、查 buffer；buffer 查表 cache miss 走二分搜尋 | 計數器 + 堆疊取樣；修掉後 GPU 執行緒 −0.20 / −0.32 ms |
| Ryujinx 架構的固有成本（未移除） | 每命令字的分派、每 draw 的狀態/綁定提交、shader 比對、頁面保護 syscall | 取樣分佈；沒有單一熱點 |
| BotW 遊戲本身 | 每幀 5 000 draw；每幀回讀 GPU 算的曝光/亮度統計（8×8 RGBA32F、16×1/16×3 RGBA16F 貼圖與兩塊 buffer）；回讀後遊戲還有約 2–3 ms 的 CPU 工作 | sync log + 呼叫鏈 |
| 不可避免的同步 | 遊戲要讀「上一幀 GPU 算出的值」，必須等那段命令執行完；`Entry1Sync.Wait` 的 GPFIFO 條目必須等前面工作完成才能讀 | 保留精確資料（不讀舊值） |
| 已排除的懷疑 | threaded GAL 佇列的 false sharing（實測 ≤ 1–2 %）、guest page fault 處理（0.1–0.3 %）、GC（0.07 ms） | 取樣 |

### 誰在關鍵路徑上（因果測試：故意讓某執行緒慢一點，看幀時間變多少）

| 加的延遲 | 幀時間變化 | 意思 |
|---|---|---|
| GPU 命令執行緒 +1.47 ms | **+1.14 ms（77 % 傳遞）** | 它是瓶頸 |
| Render 執行緒 +1.5 ms | +0.11 ms | 約 1.4 ms 餘裕，暫時不是瓶頸 |

## B. 逆向工程結果

方法：讓模擬器在每次「CPU 等 GPU 資料」時記下 guest 呼叫鏈（frame pointer 往回走），再把遊戲模組在記憶體中的樣子
dump 出來（只在本機 `.local-re/`，不進 Git），用 Ghidra headless 反編譯呼叫鏈上的函式。

| 回讀的資源 | 執行緒 | 呼叫鏈（U-King.nss 位移，最內層呼叫者在前） | 函式角色 |
|---|---|---|---|
| 曝光/亮度統計貼圖（8×8 RGBA32F ×2、16×1、16×3 RGBA16F） | worker 53（也有 54） | `0x17f5654 ← 0x13ea518 ← 0x12bbbbc ← 0x12bf910` | `0x13e8a20`（7 KB）：渲染單一 view，走訪 0x400 位元組的 view 記錄、經 vtable +0x38 呼叫各 renderer、組 pass 描述；讀回在其下層 |
| 16×1 RGBA16F | 主執行緒 46 | `0x1459af4 ← 0x1826ca8 ← 0x17fd424 ← 0x12bbbbc` | 同一套渲染管線的主執行緒端 |
| Buffer（約 8 KB 與 124 KB 區段） | worker 53 | `0x1d6e30c ← 0x1d6ddac ← 0x1d57664 ← 0x1d74620` | `0x1d6dde0`：設定一次 draw 的 uniform block（綁定 slot、GPU 位址、16、大小），等待發生在它呼叫的下一層 |
| Buffer（24 KB 區段） | 主執行緒 46 | `0x1d5cc0c ← 0x1d5abb4 ← 0x1d5b5c4` | — |

- **GPU 命令產生**：遊戲在幀尾一次送出整幀的 GPFIFO；大多數 draw 走 HLE macro（DrawElements / Instanced / Indirect）。
- **和模擬器的關係**：回讀的生產者在整幀命令流的尾端，所以「GPU 執行緒處理完整幀 → 回讀 → 遊戲剩下的 CPU 工作」
  串成關鍵路徑。等待本身只有每幀 0.3–0.4 ms，重點是 GPU 執行緒必須先處理到那裡。
- **限制**：1.6.0 沒有符號，zeldaret/botw 對應的是 1.5.0，所以只做到「函式角色」層級，沒有類別名稱（KNOWN_ISSUES #33）。

## C. Ryujinx 核心修改

| 修改 | 子系統／檔案 | 做了什麼 | 結果 | Commit |
|---|---|---|---|---|
| **Binding memo** | `BufferManager`、`MemoryManager`、`BufferCache` | 每個 vertex/index/uniform slot 記住上次的（GPU 位址, 大小）＋頁表序號＋buffer 序號；兩個序號都沒變就跳過重新翻譯和查 buffer | GPU 執行緒 **−0.20 ms**（CI −0.33 .. −0.07） | `f7449f0b5` |
| **Buffer page table**（yuzu/Eden 演算法，重新實作） | `BufferPageTable`（新）、`BufferCache` | 每個 4 KiB 頁對應到覆蓋它的 buffer，查表 O(1)，不再二分搜尋＋寫鎖 | **+1.12 FPS**（CI +0.40 .. +1.85），GPU 執行緒 −0.32 ms | `d817adf00` |
| 量測工具 | GuestSampler、`AbPhase`、sync log 呼叫鏈、split probe、camera log | 主機端方法層級取樣、呼叫堆疊、指令層級反組譯、同一次執行內 A/B、遊戲端呼叫鏈、回歸測試 | — | `10404d903`、`9ac09cad5` 等 |

**撤回的實驗**（patch 與數據在 `botw/experiments/`）：

| 實驗 | 想法來源 | 結果 | 為什麼沒用 |
|---|---|---|---|
| 兩階段 GPU 前端（解碼＋guest macro 在另一條執行緒） | bbport | **−1.30 FPS** | GPU 執行緒執行封包的時間 = 原本解碼＋執行的時間；每個命令字只有幾 ns 的工作，跨核心搬運的成本一樣多 |
| Uniform buffer 每 slot dirty | yuzu/Eden | 無效果 | 剩下的成本不在這 |
| 合併相鄰頁的重新保護 | Eden #3288 | 無效果 | BotW 的 dirty 頁不連續 |

**正確性依據**：memo 只在「頁表和 buffer 集合都沒變」時跳過，所以翻譯結果與 buffer 存在性和原本完全一樣；page table 命中後
仍檢查範圍完整包含，過期項目只會退回原本的搜尋。兩者都有開關（`BOTWPC_BIND_MEMO=0`、`BOTWPC_BUFFER_PAGE_TABLE=0`）。
沒有跳過任何 GPU 命令、沒有移除同步、沒有改遊戲時間。

**重用的外部元件**：只有工具用到 ClrMD 與 Iced（MIT）；執行期沒有引入任何外部程式碼。yuzu/Eden（GPL）與 Cemu（MPL）只借演算法、
沒複製程式碼；FPS++ 禁止散布，只當檢查清單。

## D. 效能結果（W2，舊 `3eb80e7f8` vs 新，交錯各 4 次，配對比較）

| 指標 | 之前 | 之後 | 變化（95 % CI） |
|---|---|---|---|
| 平均 FPS | 85.16 | **87.01** | **+1.85（+0.13 .. +3.57），+2.2 %** |
| 中位數 FPS | 86.17 | 88.26 | +2.10（+0.26 .. +3.94） |
| 1 % Low | 63.89 | 65.11 | +1.22（−1.67 .. +4.12），不顯著 |
| 幀時間 P95 | 13.32 ms | 13.14 ms | −0.18（−0.57 .. +0.21），不顯著 |
| 幀時間 P99 | 14.94 ms | 14.67 ms | −0.27（−0.76 .. +0.22），不顯著 |
| GPU 命令執行緒時間 | 9.38 ms | **8.95 ms** | **−0.42（−0.73 .. −0.12），−4.5 %** |
| Guest CPU 時間 | 未在同條件下量測 | — | 取樣顯示主執行緒忙碌 57 %、worker 53 為 61 %（基準時） |
| Binding commit 時間 | 未在同條件下量測（需開 profiling 計時器） | — | 由 A/B 推得：memo 與 page table 共省約 0.5 ms GPU 執行緒時間，主要落在 binding |
| 同步等待（回讀） | 0.52 ms | 0.37 ms | −0.15（−0.30 .. +0.01） |
| GPU 實際執行時間 | 3.72 ms | 3.74 ms | 不變（GPU 只忙約 40 %，不是瓶頸） |
| CPU 使用 / 記憶體 | 4.6 核 / 7.97 GB | 4.6 核 / 7.95 GB | 不變 |
| 遊戲速度 | 1.000 | 1.000 | — |

註：當天第一次在這個場景的基準是 75.7 FPS，因為新區域的 shader/pipeline cache 還在建立，不能和之後比；所有比較都在同一個
時段內交錯進行。

## E. 跨平台可用性

| 修改 | 程式碼位置 | Windows | Linux | macOS / iPadOS |
|---|---|---|---|---|
| Binding memo | `Ryujinx.Graphics.Gpu`（共用前端） | **實測** | 同一份程式碼，**未實測** | 架構上適用（與後端、OS、GPU 廠牌無關），**未實測** |
| Buffer page table | `Ryujinx.Graphics.Gpu` | **實測** | 同上，未實測 | 同上，未實測 |
| 量測工具 | GuestSampler 僅 Windows；其餘共用 | 使用 | Linux 用 perf | — |

沒有新增任何 DLSS、NVIDIA、Windows 專屬或特定 Vulkan extension 的依賴。

## F. Git 與工程狀態

**Commits（branch `botw-pc`，未 push）**：
`10404d903` 量測工具 → `f7449f0b5` binding memo → `d817adf00` buffer page table → `9ac09cad5`、`9eff8a480` 回歸測試 →
`f2a45af80`、`f83ca0316` 撤回的實驗（patch）→ `7b96ce812` 文件 → `b8dcc4459` .gitignore。

**修改的核心檔案**：`Memory/BufferManager.cs`、`Memory/BufferCache.cs`、`Memory/BufferPageTable.cs`（新）、
`Memory/MemoryManager.cs`；工具與診斷：`AbPhase.cs`、`SyncWaitLog.cs`、`HleProcessDebugger.cs`、`Horizon.cs`、
`GPFifoProcessor.cs`/`Macro.cs`/`ThreedClass.cs`（probe）、`StateUpdater.cs`（camera log）、`InputScript.cs`。

| 驗證 | 結果 |
|---|---|
| 單元測試 | Ryujinx.Tests（記憶體追蹤＋BotW-PC）62/62、Ryujinx.Tests.Memory 84/84 通過 |
| 30 / 60 / 90 / 120 FPS 時序 | 30.0 / 60.0 / 89.9 / ~85–88（瓶頸限制），遊戲速度 0.997–1.001 |
| 遊戲行為回歸（攝影機軌跡） | 30 FPS 兩次完全相同；60/90/~85 FPS 時 Link 淨移動距離與 30 FPS 差 ≤ 0.5 %，走路 ≤ 0.2 %，投擲相同；高幀率下鏡頭轉速少 1.6–3 %、原地跳攝影機高點低約 0.03 m（**既有行為**，關掉本輪修改也一樣，KNOWN_ISSUES #32） |
| 畫面 | 各版本截圖一致；DLSS Quality 仍正常啟用（1600×900 → 1920×1080） |
| DLC | 兩個 DLC 都有載入 |
| 音訊 | log 無音訊錯誤（無法以程式判斷聽感） |
| Mods | 測試資料夾沒有安裝 mod，本輪**未測** |
| Xbox 手把 | 程式未改動，本輪**未實機測試** |
| 龍、馬、滑翔、游泳、神獸 | 你的存檔還在初始台地，**無法測**（FPS++ 的龍路徑問題要用後期存檔驗證） |
| 存檔完整性 | 你目前的存檔 **43/43** 與測試前快照一致；原始 19 檔清單 19/19（備份）；測試只用拋棄式副本 |
| 正式版 | 你在玩的 `botw-pc-win` 沒動；新版在 `botw-pc-exp`，要換成正式版再跟我說 |

**剩下的架構限制**：每命令字的分派成本、每 draw 的狀態與綁定提交、shader 二進位比對、頁面保護 syscall、
render 執行緒（約 10.3 ms 的工作）。

## G. 120 FPS 可行性

| 項目 | 數值 |
|---|---|
| 現在 | ~87 FPS = 11.5 ms/幀 |
| 目標 | 8.33 ms/幀 → **還差 3.2 ms** |
| 關鍵路徑 | GPU 命令執行緒（8.95 ms，77 % 傳遞）→ render 執行緒提交回讀生產者 → GPU 執行 → 遊戲讀回後剩下的 CPU 工作（2–3 ms） |
| GPU 執行緒單獨能省多少才有用 | 約 1.4 ms（之後換 render 執行緒當瓶頸），等於幀時間約 −1.1 ms → ~96 FPS |
| 剩下的候選 | shader 查找改成位址＋寫入追蹤失效（~2 %）、texture binding 快取（1.1 ms 裡的一部分）、每 draw 重跑的狀態群組；每項 0.1–0.3 ms |
| Ryujinx 架構還有沒有大開銷 | 有，但是分散的；拆執行緒的兩種做法實測都更慢 |
| 換整個 runtime 架構是否合理 | 目前證據不支持：yuzu/Eden 的 GPU 前端是 GPL、整套換掉風險太高，而且它們最快的部分（Normal accuracy）是靠跳過同步；Cemu 靠讀舊的回讀資料，都不能用 |
| 預期效益最大的下一步 | ① 幾個單執行緒小改進一起做（目標 92–96 FPS）② render 執行緒變成瓶頸時，GAL 改成批次交接（yuzu 的 chunk 模式）③ guest 尾段 2–3 ms：ARMeilleure 程式碼品質 |

**結論**：W2 這種重場景要穩定 120 FPS，需要 GPU 執行緒、render 執行緒和 guest 尾段同時大幅變快，目前沒有任何一個經過量測的
手段做得到；較輕的場景（例如開場過場）之前已量到 ~119 FPS。

完整英文技術細節：`PERFORMANCE.md`（Round 4）、`ARCHITECTURE_COMPARISON.md`、`OPTIMIZATIONS.md` #7–#8、
`botw/experiments/README.md`。
