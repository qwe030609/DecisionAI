# DecisionAI — 多 LLM 決策系統（Rev1 架構，Phase 1）

> LLM 只負責「提出」；該不該信、該做什麼，全部由確定性程式碼計算。
> 架構依據：`DecisionAI_Architecture_Rev1.md`（Modular Monolith，.NET 8）。

## 方案結構

```
src/
  DecisionAI.Core/           零依賴：Domain、Journal（事件 + 投影）、Assurance、Policy、Ports
  DecisionAI.Modules/        Routing / Agents / Workflow / Evidence / Verification /
                             Probability / Decision / Assurance / Humans / Evaluation / Journal（權限矩陣）
  DecisionAI.Adapters/       AnthropicLlm、OpenAiCompatibleLlm、SystemClock、SeededRandomSource
  DecisionAI.Orchestration/  CaseOrchestrator + StandardHandlers（Intake 邊界）
  DecisionAI.Host/           CLI 離線 demo（全部走 ScriptedLlm，不連網）
tests/
  DecisionAI.Testing/        ScriptedLlm、FixedClock、ScriptedHumanGateway、AtsSimulator、TestSystem（組合根）
  DecisionAI.Tests/          golden（類別 1 / 2a / 2d / 4）、timeout 與未註冊 step、權限矩陣、可重放、架構規則
```

依賴方向：`Host → Orchestration → Modules → Core ← Adapters`；`Testing` 只依賴上面幾個。

```
dotnet test                              # Phase 1 完成門檻
dotnet run --project src/DecisionAI.Host # 離線 demo（互動終端機會真的問你核准）
```

## Phase 1 落地的機制

| 機制 | 位置 |
|---|---|
| 事件日誌 + 投影（`Fold(events) → CaseState`） | `Core/Journal` |
| 唯一寫入口 + 角色權限矩陣（critic 寫不進 Claim、LLM 寫不進效用矩陣） | `CaseJournal.Apply` + `Modules/Journal/RolePermissionMatrix` |
| 單寫者：handler 讀快照、回傳事件；引擎依宣告順序寫入 | `Modules/Workflow/WorkflowEngine` |
| Claim ID 決定性（依 agent 選擇順序吸收） | `Orchestration/StandardHandlers.Absorb` |
| 非決定性走 port（`IClock`、`IRandomSource.Fork`、`ILlm` + `LlmCallKey`） | `Core/Ports` |
| PolicySnapshot 版本化：case 只讀 pin 住的版本，Evaluation 產生新版本 | `Core/Policy` + `Modules/Evaluation` |
| Verifiability Triage 三問（先於任何 LLM 呼叫） | `Modules/Routing/VerifiabilityTriage` |
| 策略規則表（兩條：流水線、有機器驗證器） | `Modules/Routing/StrategyRouter` |
| Fail-closed：未註冊 step type 拒絕整個 DAG；`TimeoutPolicy` 降級 / 中止 | `WorkflowEngine` |
| InjectionGuard：證據中性化區塊 + 注入掃描，證據永不進 system prompt | `Modules/Evidence/InjectionGuard` |
| L1 critic / L2 rule / L3 executable / L4 experiment（回傳事件） | `Modules/Verification` |
| Assurance 三層 + 拒絕通道（拒答時不輸出信心；覆蓋層 Phase 1 為 null） | `Core/Assurance` + `Modules/Assurance` |

## Phase 1 刻意不做（Phase 2 / 3）

校準（Brier / ECE / Platt）、錯誤相關性折扣、Conformal 覆蓋層、Diversity、Subtext、
`IPresentationPolicy`、Tool/Model Router、Evidence Allocator、`IDriftAlarm`、Postgres + pgvector。
