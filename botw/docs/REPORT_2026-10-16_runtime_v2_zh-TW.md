# BOTW-PC Runtime V2 工程報告（2026-10-16）

分支 `runtime-v2`（從 `gpu-frontend` `7dad73c8d` 分出），worktree `BOTW-PC-v2`，**未 push**。
英文技術文件：`RUNTIME_V2_ARCHITECTURE.md`、`CPU_V2_JIT.md`、`DYNARMIC_OPTIMIZATION.md`、`GPU_FRONTEND_V2.md`、
`MEMORY_TRACKING_V2.md`、`HLE_SCHEDULER_V2.md`、`CPU_GPU_SYNCHRONIZATION.md`、`RUNTIME_V2_DIFFERENTIAL_TESTS.md`、
`PERFORMANCE.md`（Runtime V2 段）。

## 結論（先講）

| 問題 | 答案 |
|---|---|
| Runtime V2 有沒有變快？ | **有。** 完整 V2 在 W2 場景 **+7.91 FPS（+10.8 %，95 % CI +6.17 .. +9.66）**，frame time 13.63 → 12.30 ms，guest CPU −16.9 %。 |
| 速度主要從哪來？ | **CPU V2**（JIT 程式碼產生器改寫）一個人就 +5.1 FPS；GPU V2 單獨不顯著，但和 CPU V2 疊加後有用；HLE V2 ≈ 0。 |
| Memory V2？ | **沒有保留任何東西。** 唯一的候選（streaming handles）量到 −1.10 FPS，已封存。 |
| 120 FPS 達到了嗎？ | **沒有。** 81.4 FPS（機器有其他遊戲在跑）；距 8.33 ms 還差約 2.2–4.0 ms。 |
| 預設會切到 V2 嗎？ | **不會，legacy 仍是預設。** 只驗證了初始台地存檔；龍、馬、神獸、DLC 內容、Linux/macOS/iPadOS 都沒測，未通過全部 gate。 |
| 要怎麼用 V2？ | `botw-pc-runtime-v2\Play-BOTW-RuntimeV2.bat`（另一個資料夾，不動正式版 `botw-pc-win`）。 |
| 存檔安全？ | **安全。** 105 個受保護存檔（devdata 43、原始 19 檔備份、玩家快照 43）測試前後 SHA-256 完全相同；所有測試都用隔離副本 `wintest`。 |

名詞（一句話版）：
- **JIT**：把 Switch 的 ARM 指令即時翻譯成 PC 的 x86 指令的編譯器。BOTW-PC 用的是 **ARMeilleure**（C#），不是 Dynarmic。
- **PPTC**：JIT 翻譯結果的磁碟快取，第二次開遊戲不用重翻。
- **guest**：被模擬的 Switch 遊戲程式本身；**host**：你的 PC。
- **95 % CI**：差異的 95 % 信賴區間；整個區間都在 0 的同一側才算「顯著」。
- **in-run A/B**：同一次執行中每 10 秒切換新舊做法，排除背景負載漂移。

---

## A. CPU V2

**它是什麼：** 在 ARMeilleure 高品質層（BotW 7.35 萬個函式全部跑這層）改寫暫存器配置與 IR 最佳化，讓每條 Switch 指令翻出的 x86 指令變少。規格書要求「Dynarmic JIT」，但 BOTW-PC 沒有 Dynarmic；Dynarmic 只作為設計參考，沒有複製任何程式碼（`DYNARMIC_OPTIMIZATION.md`）。

**問題在哪（量測，不是猜）：** 用 GuestSampler 抓 BotW 最熱的 4,482 個函式（佔 guest JIT 取樣 86.5 %），legacy 每條 guest 指令產生 **9.06 條 host 指令**，其中 move 17.7 %、spill/reload 27.9 %。

| 改動 | 作用 | host 指令 / guest 指令 |
|---|---|---|
| Legacy | — | 9.06（加權 8.86） |
| Value numbering | 去除重複計算（只對整數；浮點會被 FTZ 模式影響，已修） | 8.96 |
| NZCV ABI liveness | 依 AAPCS64，BL/BLR/RET 之後旗標視為已死，不再寫回 | 7.82 |
| Spill at definition | spill 只在定義處存一次 | 7.53 |
| Phi copy on edge | phi 複製放在邊上而不是區塊尾 | 6.74（加權 7.03） |
| Reverse copy hints | 配置器優先讓複製兩端同暫存器 | 6.64 |
| Code cleanup | 刪除死 move、可直落的 jump | **6.62（加權 6.91），−27 %** |

**實測（4 輪交錯矩陣）：** CPU V2 單獨 **+5.14 FPS（CI +1.42 .. +8.85）**，guest CPU 30.15 → 25.28 ms/frame。

**正確性：**

| 檢查 | 結果 |
|---|---|
| 對 Unicorn（獨立 AArch64 模擬器）的隨機程式 fuzzer，2,500 支 | **0 mismatch**（exact / flags / temporaries 三種模式） |
| Ryujinx 原有 CPU 測試套件，強制高品質層，逐方法比對 | exact 模式與 legacy 完全一致；flags 模式多出的失敗全部是「RET 後的 NZCV / FPSR」—— AAPCS64 下本來就未定義 |
| 找到的 bug | **legacy 本身的配置器缺陷**（條件分支跳到自己的下一個區塊 → 同一組 spill/fill 跑兩次，錯值、甚至原生 access violation）：只在 V2 修（`DuplicateEdgeFix`），legacy 保持位元不變 |

預設 ABI 模式 `Flags`；若某遊戲跨 call 讀旗標（編譯器不會產生），可改 `cpu_v2_abi: Exact`（收益少約 13 %）。

## B. GPU V2

**它是什麼：** 在 round 5 的最佳化 GPU 前端之上的第三種模式（`GpuFrontendMode.V2`），加上 HLE macro 差分驗證器。

| 項目 | 結果 |
|---|---|
| MacroVerifier（用 guest 自己的 macro 程式碼在參考解譯器重跑，比對暫存器） | 第一次：2.30 億次比對中 **21,328 個 mismatch**，全部是 `DrawArraysInstanced` 沒更新 `VertexBufferDrawState`（Ryujinx 原有 bug） |
| 修正後 | **2.38 億次比對 0 mismatch** |
| FastWrite 驗證（round 5 的寫入快路徑） | 5.856 億次 0 mismatch |
| GPU V2 單獨效能 | GPU thread −0.64 ms（顯著），FPS +1.08（不顯著）—— guest 是瓶頸時，GPU 變快不會反映到 frame |
| 和 CPU V2 疊加 | +6.95 FPS（CPU V2 單獨 +5.14）：guest 變快後，GPU thread 的節省才進到 frame |
| MRU buffer lookup | 8,100 萬次命中 0 錯，但 +0.20 FPS 不顯著 → 封存 |

沒有重做「細粒度雙執行緒 packet pipeline」（round 4 已證明 −1.30 FPS）。

## C. Shared Memory V2

**結論：沒有保留的元件。**

| 嘗試 | 結果 |
|---|---|
| 依資源種類分類的追蹤計數器（Buffer/Texture/Pool 的 Dirty/Rearm/Volatile/Stream） | 保留（只是量測工具，`BOTWPC_PROFILE=1`） |
| Streaming handles：連續 4 frame 被重新保護的 handle 改成 60 frame 不保護、一律視為 dirty | **−1.10 FPS（CI −1.44 .. −0.75）** —— 多出的複製/上傳比省下的 ~63 次 fault/重保護還貴 → 封存 `memory-streaming-handles.patch` |

量到的基準（profiled run）：每 frame 約 166 次 reprotect（0.54 ms）、73 次寫入 fault。

## D. HLE / Scheduler V2

**它是什麼：** 照 Horizon 真機核心（mesosphere 的公開重實作）加上「yield schedule count」：一個執行緒 `svcSleepThread(0)` 空轉時，如果沒有任何人需要切換，下一次 yield 直接返回，不進 kernel critical section。

| 項目 | 結果 |
|---|---|
| 每 frame yield 次數 | 2,958 |
| 走快路徑 | 2,939（99.3 %） |
| in-run A/B | +0.36 FPS（CI −0.80 .. +1.51），**不顯著** |
| 狀態 | 保留但預設關閉（貼近真機、移除工作，但 W2 量不到效益） |
| 舊的「HLE yield fast path」 | 沒有原樣恢復；新版本是 Horizon 語意，只跳過「不會改變排程狀態」的 yield，並以 in-run A/B 量測（舊版的量測洩漏問題 KNOWN_ISSUES #29 不適用） |

guest 執行緒非 JIT 時間：HLE/kernel 各項 < 0.3 %，CoreCLR 原生轉換 6.5 %——**HLE 不是 guest 時間的去處，JIT 程式碼才是**。

## E. 綜合效能

W2 場景（玩家存檔，初始台地東側），120 FPS 模式、DLSS off、immediate present，同一個 binary 啟動時選組態，4 輪交錯，每輪 5 組態。**機器上同時開著 Minecraft 與 League client**（沒有關掉使用者的程式），所以絕對值比 round 5 低 15–20 %；**只有配對差值可信**。

| 指標 | Legacy | CPU V2 | GPU V2 | CPU+GPU | CPU+GPU+Memory | **完整 V2**（+HLE） |
|---|---|---|---|---|---|---|
| 平均 FPS | 73.51 | 78.65 | 74.59 | 80.46 | = CPU+GPU（無保留元件，未另測） | **81.42** |
| Δ FPS（95 % CI） | — | +5.14（+1.42 .. +8.85） | +1.08（n.s.） | +6.95（+3.28 .. +10.62） | — | **+7.91（+6.17 .. +9.66）** |
| 1 % low | 48.6 | 50.1 | 48.1 | 48.5 | — | 53.6（+4.95，n.s.） |
| Frame time | 13.63 ms | 12.75 | 13.45 | 12.47 | — | **12.30（−1.33，顯著）** |
| P95 frame time | 15.88 ms | 15.13 | 15.84 | 15.00 | — | 14.44（−1.44，顯著） |
| P99 frame time | 18.42 ms | 17.48 | 18.44 | 17.37 | — | 16.60（−1.82，n.s.） |
| Guest CPU / frame | 30.15 ms | 25.28 | 30.10 | 25.31 | — | **25.06（−16.9 %）** |
| GPU command thread / frame | 10.64 ms | 10.42 | 10.00 | 9.73 | — | 9.72（−0.92，顯著） |
| Render thread | 矩陣中未量測 | | | | | |
| Memory tracking 時間 | 矩陣中未量測（profiled run：reprotect 0.54 ms/frame，fault 73 次/frame） | | | | | |
| HLE / scheduler 時間 | 矩陣中未量測（取樣：各項 < 0.3 % guest 執行時間） | | | | | |
| 遊戲速度 | 1.000 | 1.000 | 1.000 | 0.999 | — | 1.000 |

DLSS（完整 V2，3+3 交錯）：GPU thread −0.02 ms（CI −0.49 .. +0.46）——round 5 看到的 +1.5 ms **沒有重現**；DLSS Quality 每次都 active，截圖正常。

## F. 失敗 / 否決的實驗（全部封存或記錄）

| 實驗 | 量測 | 處置 |
|---|---|---|
| Memory V2 streaming handles | −1.10 FPS（顯著變慢） | 封存 `experiments/memory-streaming-handles.patch` |
| GPU MRU buffer lookup | +0.20 FPS（n.s.） | 封存 `experiments/gpu-mru-buffer-lookup.patch` |
| 配置器優先 caller-saved 暫存器 | host 指令 +0.07 % | 不採用（記錄於 `experiments/README.md`） |
| 函式層級 relocatable 常數 | `mov imm64` −14.9 萬，move/reload +13 萬，淨 0 | 不採用 |
| Temporaries ABI（X8–X17 跨 call 視為死） | 指令再 −9 %，500 支程式通過 | 保留為實驗模式，不預設：TLS descriptor 等特殊呼叫慣例會壞 |
| Spill at definition 門檻 1 | 靜態較好，實測選門檻 2 | 預設 2 |

## G. 遊戲正確性矩陣（完整 V2）

| 項目 | 結果 | 依據 |
|---|---|---|
| 開機、載入存檔（初始台地 W2） | PASS | 52 次含 CPU V2 的執行 |
| 走、跑、跳、丟擲、轉鏡頭（回歸腳本） | PASS | `regress-v2full-*` vs `regress-legacy-*`，`camera_track.py` |
| 30 FPS 模式 | PASS（30.0 FPS，速度 0.999） | 走路 10.70 m、轉向 −85.4°，與 round 5 legacy 三次完全一致 |
| 60 FPS 模式 | PASS（60.0 FPS，速度 1.000） | 總位移 18.47 m，與 round 5 legacy 相同 |
| 90 FPS 模式 | PASS（90.0 FPS；legacy 只有 89.1） | 與同場 legacy 差 < 0.1 m、1.2° |
| 120 FPS 模式 | PASS（實際 94.5 FPS；legacy 87.5） | 與同場 legacy 差 0.16 m、0.7°；高 FPS 下鏡頭轉角略小（−82.8°）是 round 5 就有的現象，legacy 也一樣 |
| 遊戲速度（不靠降速換 FPS） | PASS（1.000） | 所有矩陣執行 |
| 畫面（截圖） | PASS | 矩陣與 DLSS 截圖 |
| DLSS Quality | PASS，但 **1 次崩潰未重現**（KNOWN_ISSUES #43，未解） | `dlss-quality-2` |
| 敵人攻擊 / Game Over 畫面 | PASS（夜間敵人造成 Game Over，正常顯示並重試） | 偶發 |
| DLC | 部分：兩個 DLC 已安裝、隨遊戲載入；DLC 內容（試煉之劍、英傑之詩）**NOT TESTED** | |
| 遊戲內存檔 / 讀檔 | NOT TESTED（只載入隔離副本） | |
| 劇情動畫、神廟、馬、龍、神獸、城堡 | NOT TESTED | |
| 長時間遊玩（> 3 分鐘連續） | NOT TESTED（最長單次 330 s） | |
| 單元測試 | PASS | BotwPc 77 通過 / 0 失敗（4 個除錯用測試依設計略過）；Memory 84 通過 / 0 失敗 |
| 交付版 build 煙霧測試 | PASS | `regress-deliv-120`：V2 三個元件都啟用，95.0 FPS，速度 1.000 |
| Log 錯誤 | PASS（無新增） | 唯一錯誤是讀檔時一次 `WaitOnSyncpoint` 超過 1 秒：所有組態都有，round 4/5 的 77 次中 76 次也有，早就存在 |

## H. 跨平台就緒度

| 平台 | 狀態 |
|---|---|
| Windows x86-64 | **已建置、已量測**（本報告所有數字） |
| Linux x86-64 | 未建置、未測。程式碼路徑與 Windows 相同（全部是共用 C#，無平台專屬程式碼），預期行為一致，但**沒有證據** |
| macOS（Apple Silicon） | 未建置、未測。需 AArch64 host 後端：IR 層改動與共用配置器會套用，`CodeCleanup` 只接到 x86-64 後端 |
| iPadOS | 未建置、未測。iPadOS 不允許一般 App 寫入可執行記憶體，JIT 需除錯器/特殊 entitlement，正式版需要直譯器或 AOT；V2 不改變這個限制 |

## I. 工程狀態

| Commit | 內容 |
|---|---|
| `aeefa5425` | 量測工具：GuestSampler `--jit`、比例式 guest 延遲 |
| `a88e8c87b` | CPU V2 第 1 輪 + Runtime V2 設定 + 差分測試 |
| `2f37b5386` | GPU V2 模式、分資源追蹤計數器、封存 Memory/GPU 實驗 |
| `bb305d320` | CPU V2 第 2 輪（reverse copy hints、code cleanup） |
| `b9d941ae1` | HLE V2（yield schedule count） |
| `6ad74a0fe` | GPU V2 macro 驗證器 + `DrawArraysInstanced` 修正 |
| （最後一個 commit） | 文件、驗證結果與本報告 |

- 切換方式：`settings.json` 的 `runtime` 段或環境變數 `BOTWPC_CPU_CORE` / `BOTWPC_GPU_FRONTEND` / `BOTWPC_HLE_SCHEDULER`，**只在啟動時決定**。
- 預設：全部 legacy。V2 的 PPTC 快取用不同檔名（第一次啟動約 21 s 重新編譯），不覆蓋 legacy 快取。
- 授權：沒有複製 GPL 程式碼、沒有提交反編譯遊戲程式碼；遊戲映像只在 `.local-re/`（git 外）。
- 正式版 `botw-pc-win`、`devdata`、Ryubing 原始資料：未修改。
- 開放問題：KNOWN_ISSUES #43（V2 + DLSS 一次崩潰）、#38（Flags ABI 假設）。

## 交付

| 項目 | 位置 |
|---|---|
| Runtime V2 執行檔（從 `6ad74a0fe` 乾淨建置） | `C:\Users\<user>\Downloads\BOTW\botw-pc-runtime-v2\` |
| 啟動器 | `botw-pc-runtime-v2\Play-BOTW-RuntimeV2.bat`：先問 Y/N → 把 `devdata` 存檔複製到 `save-backup\runtime-v2-<時間>` → 設定 V2 環境變數與 crash dump → 啟動 |
| 正式版 | `botw-pc-win` + `Play-BOTW.bat` 沒動，仍是 legacy |
| 第一次用 V2 啟動 | 多約 20 秒（建立 V2 自己的 PPTC 快取，不碰 legacy 快取） |

## J. 120 FPS 可行性

| | |
|---|---|
| 完整 V2（本次，有背景負載） | 81.4 FPS = 12.30 ms |
| 套用到 round 5 閒置機器 legacy（84.1 FPS） | 約 10.6 ms ≈ 94 FPS（**推估，未量測**） |
| 目標 | 8.33 ms → **還差約 2.2–4.0 ms** |
| 第一瓶頸 | GPU command thread（延遲它 1.5 ms，有 78 % 傳到 frame） |
| 第二瓶頸 | guest（guest 慢 20 % → frame +1.42 ms） |

**判斷：現有架構下 120 FPS 不可行，需要兩條鏈同時縮短**——GPU thread 約 −3 ms（結構性改動：批次化、離執行緒的 binding/state 處理）加上 guest 再 −15～20 %（更多 JIT 配置器工作）。只改其中一邊，另一邊馬上變成瓶頸。不使用插幀或降速。
