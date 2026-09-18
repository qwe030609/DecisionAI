# DecisionAI — 多 LLM 決策系統（Rev2 架構，Phase 2）

> LLM 只負責「提出」；該不該信、該做什麼，全部由確定性程式碼計算。
> 架構依據：`DecisionAI_Architecture_Rev2.md`（Modular Monolith，.NET 8）。
> Rev2 的核心是 D9：**LLM 只提名，確定性程式決定**；以及 D10：**獨立性是有限資源，不足就降級**。

## 方案結構

```
src/
  DecisionAI.Core/           零依賴：Domain、Journal（事件 + 投影）、Assurance、Policy、Ports
  DecisionAI.Modules/        Routing / Agents / Workflow / Evidence / Verification / Catalog /
                             Probability / Decision / Assurance / Diversity / Subtext / Humans /
                             Evaluation / Journal（權限矩陣）
  DecisionAI.Adapters/       AnthropicLlm、OpenAiCompatibleLlm、SystemClock、SeededRandomSource、LocalEmbedder
  DecisionAI.Orchestration/  CaseOrchestrator + StandardHandlers（Intake 邊界）
  DecisionAI.Host/           CLI 離線 demo（全部走 ScriptedLlm，不連網）
tests/
  DecisionAI.Testing/        ScriptedLlm、FixedClock、ScriptedHumanGateway、AtsSimulator、TestSystem（組合根）
  DecisionAI.Tests/          golden（類別 1 / 2a / 2d / 4）、MR-1～19、Phase 2 守門件性質測試、
                             timeout 與未註冊 step、權限矩陣、可重放、架構規則
```

依賴方向：`Host → Orchestration → Modules → Core ← Adapters`；`Testing` 只依賴上面幾個。

```
dotnet test                                    # Phase 2 完成門檻（106 項，含 MR-1～19）
dotnet run --project src/DecisionAI.Host       # 離線 demo（互動終端機會真的問你核准）
dotnet run --project tools/DecisionAI.Benchmark -- out.json   # 32 案 benchmark + mutation 矩陣
```

## Benchmark（`tools/DecisionAI.Benchmark`）

32 個案例（沿用外部 router benchmark 的題目與維度編號）真的跑過 `CaseOrchestrator`，
每案比對十四項**可被推翻**的檢查，並跑 17 個 mutation switch 確認檢查有效。
Phase 2 新增的五項檢查刻意都是「獨立重算再比對」而不是「讀系統自己寫的數字」——
讀它自己寫的數字永遠會對，那種檢查抓不到任何壞掉的守門件：

| 檢查 | 怎麼被推翻 |
|---|---|
| Stability | 三擾動分開核對；穩定度與樣本數都必須能被重算出同一個值；宣稱 100% 時 leave-one-out 也不得翻盤 |
| Saturation | check 自己數一次 singleton / doubleton 再算 Chao1；覆蓋率低就必須降級能力層 |
| Coverage | 白名單外題型（反身、漂移）與零校準樣本都不得出現覆蓋層 |
| ExperimentSelection | check 自己重算 EVOI；登記的實驗必須有正的 EVOI，提名數 = 登記 + 跳過 |
| Ensemble | 權重與相關性折扣必須寫進事件流，且折扣不得全為 0 |



| Mutation | 換掉的守門件 | 受影響 | 抓到率 |
|---|---|---|---|
| M1 Triage 永遠放行 | `IVerifiabilityTriage` | 11 | 100%（由 StrategyRouter 的 fail-closed 接住） |
| M2 拒答閘永不拒答 | `IAbstentionGate` | 13 | 100% |
| M3 能力層浮報 | `IAssuranceService` | 32 | 100% |
| M4 證據不中性化 | `IInjectionGuard` | 29 | 100% |
| M5 越權 critic + 權限全開 | `IRolePermission` | 19 | 100% |
| M6 ★ Canonicalizer 永遠回新條目 | `IClaimCanonicalizer` | 4 | 100% |
| M7 ★ 拿掉預登記 hash 比對 | `IPreRegistrationGuard` | — | 本 benchmark 不可觀察，以單元測試覆蓋 |
| M8 ★ 停用情境對稱性檢查 | `IPayloadVerifier` | 3 | 100%（需搭配會寫歪的 briefer） |
| M9 ★ Tool/Model Router 從不轉介 | `IToolModelRouter` | 2 | 100% |
| M10 ★ 忽略降級階梯 | `IRoleAssigner` | 19 | 100%（需搭配單一模型家族） |
| M11 ★ critic 直接看 solver 原始輸出 | `CriticContextBuilder` | 2 | 100% |
| M12 ☆ 重抽樣永遠回報全穩 | `IDecisionStabilityAnalyzer` | 16 | 100% |
| M13 ☆ 似然敏感度永遠回報不敏感 | `ILikelihoodSensitivityAnalyzer` | 1 | 100% |
| M14 ☆ EVOI 改成一律執行 | `IExperimentSelector` | 7 | 100% |
| M15 ☆ Conformal 對任何題型都給保證 | `IConformalCalibrator` | 16 | 100% |
| M16 ☆ 相關性一律視為 0 | `ICorrelationEstimator` | 7 | 100% |
| M17 ☆ Thompson 改回 argmax mean | `IRoleAssigner` | — | 單案不可觀察，由 MR-19 覆蓋 |

受影響的分母以 baseline 實際發生什麼為準：實驗被 EVOI 跳掉的案例就沒有敏感度分析可以造假，
把它算進分母只會讓數字好看或難看，兩者都不誠實。

結果：32/32 通過金標；13 案拒答（11 案 Triage、2 案轉介專業數值模型）。
Phase 2 的行為變化：7 案有實驗提名被 EVOI 判定為「無論出哪個結果推薦行動都一樣」而跳過，
其中 2 案因此比 Phase 1 少登記實驗——那種實驗只是讓人心安，花的卻是真錢。
另含降級階梯實驗：同一題在 5 / 3 / 2 / 1 個模型家族下，solver 數、critic 取捨與能力層降級理由如何變化。

## Phase 1 落地的機制

| 機制 | 位置 |
|---|---|
| 事件日誌 + 投影（`Fold(events) → CaseState`） | `Core/Journal` |
| 唯一寫入口 + 角色權限矩陣（critic 寫不進 Claim、designer 寫不進實驗登記、LLM 寫不進效用矩陣） | `CaseJournal.Apply` + `Modules/Journal/RolePermissionMatrix` |
| ★ ClaimFrame 四元組（機制封閉集 / 位置 / 觸發 / 可觀察） | `Core/Domain/ClaimFrame` |
| ★ Catalog：版本化 append-only + Exact/Fuzzy/Ambiguous/New 對映，灰帶升級 arbiter | `Modules/Catalog` |
| ★ Likert 五級 → 確定性查表，取代 LLM 直接寫小數 | `Core/Domain/ClaimFrame`（`Likert`） |
| ★ 實驗提名與登記分離 + 似然 hash 前置比對 | `Modules/Probability/LikelihoodElicitor` + `Verification/PreRegistrationGuard` |
| ★ Tool/Model Router：專業數值模型領域轉介出去 | `Modules/Routing/ToolModelRouter` |
| ★ IndependenceBudget + 降級階梯 | `Core/Domain/Independence` + `Modules/Agents/RoleAssigner` |
| ★ 型別化 SubstitutePayload（ReasonCode 綁定於編譯期）+ payload 過 L2 + 情境對稱性 | `Core/Assurance` + `Modules/Assurance/PayloadRules` |
| ★ critic context 由 Journal 投影重建，餵原始輸出即拒 | `Modules/Verification/CriticContextBuilder` |
| 單寫者：handler 讀快照、回傳事件；引擎依宣告順序寫入 | `Modules/Workflow/WorkflowEngine` |
| 非決定性走 port（`IClock`、`IRandomSource.Fork`、`ILlm` + `LlmCallKey`） | `Core/Ports` |
| PolicySnapshot + Catalog 版本一併 pin | `Core/Policy` + `Modules/Evaluation` |
| Verifiability Triage 三問（先於任何 LLM 呼叫） | `Modules/Routing/VerifiabilityTriage` |
| Fail-closed：未註冊 step type 拒絕整個 DAG；`TimeoutPolicy` 降級 / 中止 | `WorkflowEngine` |
| InjectionGuard：證據中性化區塊 + 注入掃描，證據永不進 system prompt | `Modules/Evidence/InjectionGuard` |

## Phase 2 新增的機制

| 機制 | 位置 |
|---|---|
| 校準收縮（Brier / ECE）+ 三層錯誤相關性折扣（lineage / probe / instance） | `Modules/Probability/Probability`、`Correlation` |
| EVOI 實驗選擇：無論出哪個結果都不改行動的實驗不登記 | `Modules/Decision/ExperimentSelector` |
| 似然等級整體 ±1 級的敏感度旗標 | `Modules/Probability`（`LikelihoodSensitivityAnalyzer`） |
| 假設集重抽樣：列舉所有 solver 子集，確定性且可被獨立重算 | `Modules/Assurance/Stability` |
| Chao1 假設覆蓋率 → 覆蓋率低就降級能力層 | `Modules/Assurance/Stability`、`AssuranceService` |
| Conformal 覆蓋層：只對 TaskFamily 白名單，反身與漂移硬性禁止 | `Modules/Assurance/Conformal` |
| 角色資格探針（solver / critic / designer）+ Thompson 抽樣 + 10% 強制輪換 | `Modules/Agents/Probes`、`RoleAssigner` |
| 實驗歷史頻率跨案累積，優先於定性估計 | `Modules/Catalog/ExperimentCatalog` |
| Diversity：多指標崩塌偵測 + 預先登記門檻 + 四階處置階梯 | `Modules/Diversity` |
| Subtext：分歧最大的 top-k 送人評、盲評、主動學習只進校準集 | `Modules/Subtext` |
| `IPresentationPolicy`（伺服端）：證據與最壞情況先、系統建議後揭示 | `Modules/Humans/PresentationPolicy` |
| `IEmbedder` 本地實作（hashing trick，不呼叫 LLM） | `Adapters/Runtime/LocalEmbedder` |

## Phase 2 刻意不做（Phase 3）

Evidence Allocator、`IDriftAlarm`、序列型測試（跨 case 記憶成長與 conformal 覆蓋率實測）、
Postgres + pgvector、真模型錄製 / 重放比較。
