// ============================================================================
//  32 個案例（沿用 ChatGPT 版的題目與維度編號），改用 Rev2 事實登記。
//
//  對照重點：ChatGPT 版把「可驗證性 = 0.95」當成輸入；Rev2 要求登記的是
//  「這個案子現場附了哪一級 verifier」「屬不屬於某個專業數值模型的領域」——
//  前者是估計，後者是事實。因此同一題在兩邊會走到不同結論。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;

namespace DecisionAI.Benchmark;

public static class BenchmarkCases
{
    private static EvidenceDraft E(EvidenceSource s, string origin, string content) => new(s, origin, content);

    private static HypSpec H(string mechanism, string locus, string trigger, string observable,
                             LikertBelief conf, int[]? forIdx = null, int[]? against = null)
        => new(new ClaimFrame(mechanism, locus, trigger, observable), conf,
               forIdx ?? new[] { 1 }, against ?? Array.Empty<int>());

    private static ActionOption A(string id, string desc, params double[] utilities)
        => new(id, desc, utilities.Select((u, i) => (k: $"H{i + 1}", u)).ToImmutableDictionary(x => x.k, x => x.u));

    private static ImmutableArray<StageSpec> Stages(params (string Name, string Task, VerifierLevel V, string Contract)[] s)
        => s.Select(x => new StageSpec(x.Name, x.Task, x.V, x.Contract)).ToImmutableArray();

    /// <summary>用假設本身的四元組當 Catalog 種子（去掉 Trigger/Observable 的細節，模擬既有 FMEA 條目）。</summary>
    private static ImmutableArray<ClaimFrame> SeedFrom(params HypSpec[] hyps)
        => hyps.Select(h => h.Frame with { Trigger = "既有條目", Observable = "既有條目" }).ToImmutableArray();

    public static ImmutableArray<BenchCase> All => Build();

    private static ImmutableArray<BenchCase> Build()
    {
        // ── V2 ATS（Catalog 有前兩條，第三條是新的）──
        var v2 = new[]
        {
            H(Mechanisms.RaceCondition, "AmqpClient.KeepAliveTick/_link",
              "併發存取：timer callback 與 CloseLink 同時碰同一個連線物件",
              "thread log 上兩者的時序重疊", LikertBelief.AlmostCertain, new[] { 1, 2 }),
            H(Mechanisms.LifecycleMisuse, "AmqpClient._link channel",
              "長時間執行後 channel 被提前回收", "斷線前 channel 已進入 Closed", LikertBelief.EvenOdds, new[] { 2 }),
            H(Mechanisms.EnvironmentalStress, "switch-01 網路路徑",
              "閒置逾時", "交換機 port flap 或 ICMP 丟包", LikertBelief.Unlikely, new[] { 1 }, new[] { 3 }),
        };

        var d2 = new[]
        {
            H(Mechanisms.BlockingIo, "MainWindow.OnButtonClick/client.Read",
              "在 UI thread 上執行同步 I/O", "ETW trace 顯示 UI thread 停在 WaitOne", LikertBelief.AlmostCertain, new[] { 1, 2 }),
            H(Mechanisms.ResourceLeak, "GC Gen2", "大物件堆積後的回收停頓", "GC log 出現 Gen2 停頓", LikertBelief.Unlikely, new[] { 1 }, new[] { 3 }),
        };

        var b2 = new[]
        {
            H(Mechanisms.ConfigurationError, "Decoder.cs 曝光時間",
              "環境光下降時曝光時間仍固定", "失敗影像平均亮度低於成功影像 38%", LikertBelief.AlmostCertain, new[] { 1, 2, 3 }),
            H(Mechanisms.TimingDrift, "鏡頭對焦機構", "長時間運轉後對焦漂移", "影像 MTF 隨時間下降", LikertBelief.Unlikely, new[] { 1 }),
        };

        var c2 = new[]
        {
            H(Mechanisms.DesignTradeoff, "VISA session 擁有權",
              "四個 Station 各自開啟同一台儀器", "VISA error -1073807246 每班約 3 次", LikertBelief.AlmostCertain, new[] { 1, 2, 3 }),
            H(Mechanisms.ConfigurationError, "重試與退避設定", "衝突時直接重試", "重試次數上升但錯誤未消失", LikertBelief.Unlikely, new[] { 2 }, new[] { 1 }),
        };

        var r2 = new[]
        {
            H(Mechanisms.DesignTradeoff, "ATS 軟體平台選型",
              "需同時滿足 MES 整合與單元測試", "既有 C# 驅動層可重用 11 個儀器類別", LikertBelief.Likely, new[] { 1, 2, 3 }),
            H(Mechanisms.DesignTradeoff, "團隊熟悉度優先",
              "以首次交期為主要指標", "歷史專案交期短 22% 但維護工時多 40%", LikertBelief.EvenOdds, new[] { 1, 2 }),
        };

        var r3 = new[]
        {
            H(Mechanisms.WearOut, "K3 安全繼電器接點",
              "累積動作次數導致接點老化", "回饋延遲由 12 ms 漂移到 46 ms", LikertBelief.AlmostCertain, new[] { 1, 2, 3 }),
            H(Mechanisms.ConfigurationError, "自檢逾時設定",
              "逾時門檻設得過嚴", "自檢超時但未觸發跳脫", LikertBelief.Unlikely, new[] { 1 }, new[] { 2 }),
        };

        var x7 = new[]
        {
            H(Mechanisms.SpecificationFact, "保護接地連續性量測",
              "依 IEC 61010 在 25 A 測試電流下量測", "實測 0.043 Ω ≤ 0.1 Ω 上限", LikertBelief.AlmostCertain, new[] { 1, 2, 3 }),
            H(Mechanisms.SpecificationFact, "單點量測的代表性",
              "只量一個點", "多點掃描可能出現更高阻抗", LikertBelief.Unlikely, new[] { 1 }),
        };

        return ImmutableArray.Create(
        // ══════════════════ V · 可驗證性 ══════════════════
        new BenchCase
        {
            Id = "V1", Dimension = "V", Level = "High", Title = "演算法正確性", Domain = "algorithm",
            Prompt = "給定 100 萬筆整數，找出出現次數最多的前 10 個數字，並證明時間複雜度。",
            Risk = RiskLevel.Low, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.SourceCode, "spec.md", "輸入為 int32 陣列，長度 1e6，允許 O(n) 額外記憶體"),
                E(EvidenceSource.Database, "bench-history", "同機器上 Dictionary 計數法實測 1e6 筆耗時 42 ms"),
                E(EvidenceSource.Paper, "CLRS ch.9", "選擇演算法可在期望 O(n) 時間取得第 k 大元素")),
            Hypotheses = ImmutableArray.Create(
                H(Mechanisms.AlgorithmicChoice, "計數 + 大小為 k 的最小堆",
                  "n 遠大於 k 時", "實測時間隨 n 線性成長，與 log k 無關", LikertBelief.AlmostCertain, new[] { 1, 2 }),
                H(Mechanisms.AlgorithmicChoice, "整體排序後取前 k",
                  "任何 n", "實測時間隨 n log n 成長", LikertBelief.Unlikely, new[] { 3 }, new[] { 2 })),
            Expected = Expected.Proceed(Strategy.SingleAgent, "solver_verifier"),
            WhyRev2 = "附了可執行測試（L3）且風險低 → 單一 agent + 驗證就夠，不為多 agent 而多 agent。"
        },
        new BenchCase
        {
            Id = "V2", Dimension = "V", Level = "Medium", Title = "ATS 偶發斷線", Domain = "csharp_concurrency",
            Prompt = "ATS 每運行約 300 次會出現一次 AMQP ReceiverLink 斷線，請分析根因。",
            Risk = RiskLevel.Medium, HasL3 = true, HasL4 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.RuntimeLog, "ATS-PC-03", "斷線前 20 ms 內 KeepAlive timer callback（thread 12）與 CloseLink（thread 7）交錯執行"),
                E(EvidenceSource.SourceCode, "AmqpClient.cs", "KeepAliveTick() 直接讀寫 _link，沒有取得 _linkLock"),
                E(EvidenceSource.RuntimeLog, "switch-01", "同時段交換機無 port flap、無 ICMP 丟包")),
            Hypotheses = v2.ToImmutableArray(),
            CatalogSeed = SeedFrom(v2[0], v2[1]),
            Actions = ImmutableArray.Create(
                A("A", "為 KeepAlive timer 加鎖（根因修復）", 100, -20, -30),
                A("B", "移除 KeepAlive（workaround）", 60, 10, -30),
                A("C", "外層 retry 包裝（治標）", 20, 20, 30)),
            Expected = Expected.Proceed(Strategy.SolverCriticVerifier, "engineering_root_cause_nocritic"),
            WhyRev2 = "有模擬器（L3）與實驗環境（L4）；風險中等 → Rev2 的規則說 critic 是可選角色，機器已經判過了，省一次模型呼叫。"
        },
        new BenchCase
        {
            Id = "V3", Dimension = "V", Level = "Low", Title = "海外工作與家人", Domain = "life_values",
            Prompt = "高薪海外工作與陪伴家人之間，哪個選擇更值得？",
            GroundTruth = GroundTruthStatus.Undefined, Risk = RiskLevel.High,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "user", "海外職位薪資為現職 2.1 倍，合約 3 年"),
                E(EvidenceSource.UserInput, "user", "家中有 6 歲子女與需照護的長輩")),
            Expected = Expected.Abstain(ReasonCode.NoGroundTruth),
            WhyRev2 = "「更值得」沒有各方同意的判準。Rev2 拒答時附 ConsiderationMap：整理各方考量與衝突，但不評分、不下結論。"
        },

        // ══════════════════ D · 可分解性 ══════════════════
        new BenchCase
        {
            Id = "D1", Dimension = "D", Level = "High", Title = "完整 ATS 系統", Domain = "full_system",
            Prompt = "設計包含儀器、PLC、LabVIEW/C#、MES、資料庫的產線測試系統。",
            Risk = RiskLevel.High, HasL3 = true,
            Stages = Stages(
                ("儀器層", "選型並定義 VISA/SCPI 指令集與時序", VerifierLevel.L3_ExecutableTest, "輸出：儀器清單 + 指令表 + 逾時策略"),
                ("PLC 層", "定義 Modbus TCP 暫存器映射與安全連鎖", VerifierLevel.L3_ExecutableTest, "輸出：暫存器表 + 連鎖矩陣"),
                ("軟體層", "測試序列引擎與例外處理", VerifierLevel.L3_ExecutableTest, "輸出：狀態機 + 錯誤碼表"),
                ("MES/DB", "資料模型與追溯", VerifierLevel.L2_Rule, "輸出：schema + 保存策略"),
                ("整合", "端到端驗收條件", VerifierLevel.L3_ExecutableTest, "輸出：驗收測試清單")),
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "user", "節拍要求 45 秒/件，每站 4 個測項"),
                E(EvidenceSource.Database, "現有產線", "既有 MES 以 REST 介接，資料保存 5 年")),
            Expected = Expected.Proceed(Strategy.MultiDomainPipeline, "multi_domain_pipeline"),
            WhyRev2 = "五段跨領域 → 流水線；每段之間有人類邊界閘，介面契約沒過就不往下游傳。"
        },
        new BenchCase
        {
            Id = "D2", Dimension = "D", Level = "Medium", Title = "UI 偶發卡頓", Domain = "ui_debug",
            Prompt = "為什麼這個程式有時候 UI 卡頓？",
            Risk = RiskLevel.Medium, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.RuntimeLog, "ETW trace", "卡頓時 UI thread 停在同步 WaitOne，平均 780 ms"),
                E(EvidenceSource.SourceCode, "MainWindow.cs", "按鈕事件內直接呼叫 client.Read()（同步 I/O）"),
                E(EvidenceSource.RuntimeLog, "GC log", "同時段無 Gen2 回收")),
            Hypotheses = d2.ToImmutableArray(), CatalogSeed = SeedFrom(d2[0]),
            Expected = Expected.Proceed(Strategy.SolverCriticVerifier, "engineering_root_cause_nocritic"),
            WhyRev2 = "有 trace 可重現（L3）；風險中等 → 走假設—驗證，但省掉 L1 critic。"
        },
        new BenchCase
        {
            Id = "D3", Dimension = "D", Level = "Low", Title = "一句話的幽默", Domain = "humor",
            Prompt = "為什麼「我不是遲到，只是時間先到了」讓人覺得好笑？",
            GroundTruth = GroundTruthStatus.Undefined, Risk = RiskLevel.Low,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "user", "這句話出現在辦公室群組，收到 12 個笑臉回應"),
                E(EvidenceSource.Paper, "幽默研究", "違反預期後的重新框架是常見的笑點機制")),
            Expected = Expected.Abstain(ReasonCode.NoGroundTruth),
            WhyRev2 = "「為什麼好笑」沒有共識判準。Rev2 不是沉默拒答，而是給出考量地圖——這正是 Rev1 被批評過於貧乏的地方。"
        },

        // ══════════════════ U · 不確定性 ══════════════════
        new BenchCase
        {
            Id = "U1", Dimension = "U", Level = "Low", Title = "歐姆定律", Domain = "calculation",
            Prompt = "一個 24V、12Ω 電阻負載的功率是多少？",
            Risk = RiskLevel.Low, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "user", "電源 24 V DC，負載電阻 12 Ω，純電阻"),
                E(EvidenceSource.Paper, "電路學", "P = V² / R（純電阻穩態）")),
            Hypotheses = ImmutableArray.Create(
                H(Mechanisms.SpecificationFact, "純電阻穩態功率",
                  "直流穩態", "P = 576/12 = 48 W，可用功率計驗證", LikertBelief.AlmostCertain, new[] { 1, 2 })),
            Expected = Expected.Proceed(Strategy.SingleAgent, "solver_verifier"),
            WhyRev2 = "可由規則直接檢核（L3）且風險低 → 單一 agent。不需要集成。"
        },
        new BenchCase
        {
            Id = "U2", Dimension = "U", Level = "Medium", Title = "繼電器故障概率", Domain = "reliability",
            Prompt = "某繼電器已運行 80 萬次，未來一個月發生故障的概率是多少？",
            Risk = RiskLevel.Medium, HasL3 = true,
            Specialist = new SpecialistDomain("weibull_reliability",
                "壽命分布有成熟的參數模型（Weibull / Kaplan-Meier），LLM 算不出也不該算",
                "取 shape / scale 與 80 萬次處的條件失效機率，看 95% 信賴區間寬度",
                "樣本 37 顆、右設限比例高時區間會很寬；外推超過觀測範圍要特別小心",
                ImmutableArray.Create("同型號失效循環數清單", "目前累計循環數", "月增循環數")),
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Database, "維修紀錄", "同型號 37 顆的失效循環數：均值 118 萬，標準差 21 萬"),
                E(EvidenceSource.Sensor, "計數器", "該顆目前 802,410 次，月增約 6 萬次")),
            Expected = Expected.Abstain(ReasonCode.DelegatedToExternalModel),
            WhyRev2 = "★ Rev2 新增的 Tool/Model Router：這是可靠度模型的領域，該交給 Weibull 擬合，不是交給語言模型算。Rev1 會自己做假設—驗證。"
        },
        new BenchCase
        {
            Id = "U3", Dimension = "U", Level = "High", Title = "股票月度預測", Domain = "market_forecast",
            Prompt = "NVDA 下個月上漲的概率是多少？",
            Reflexive = true, Risk = RiskLevel.High,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Web, "財經新聞", "近一週分析師調升目標價"),
                E(EvidenceSource.Database, "價量", "30 日歷史波動率 42%"),
                E(EvidenceSource.Database, "資金流", "近五日 ETF 淨流入轉正")),
            Expected = Expected.Abstain(ReasonCode.ReflexiveDirection),
            WhyRev2 = "反身系統 → 拒答，且 payload 型別強制為 ScenarioBriefing：只能給事實、對稱情境與可觀察的領先指標，不能給機率。"
        },

        // ══════════════════ S · 主觀性 ══════════════════
        new BenchCase
        {
            Id = "S1", Dimension = "S", Level = "Low", Title = "TCP 與 UDP", Domain = "protocol_fact",
            Prompt = "TCP 與 UDP 哪一個保證資料順序？",
            Risk = RiskLevel.Low, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Paper, "RFC 793", "TCP 提供有序、可靠的位元流傳遞"),
                E(EvidenceSource.Paper, "RFC 768", "UDP 不保證送達、不保證順序、不去重")),
            Hypotheses = ImmutableArray.Create(
                H(Mechanisms.SpecificationFact, "傳輸層順序保證",
                  "任何傳輸", "以亂序封包測試：TCP 交付有序，UDP 不保證", LikertBelief.AlmostCertain, new[] { 1, 2 })),
            Expected = Expected.Proceed(Strategy.SingleAgent, "solver_verifier"),
            WhyRev2 = "規格可直接檢核 → 單一 agent + 規則驗證。"
        },
        new BenchCase
        {
            Id = "S2", Dimension = "S", Level = "Medium", Title = "工業軟體導航", Domain = "ux_choice",
            Prompt = "工業軟體首頁用左側導航還是頂部導航比較好？",
            Risk = RiskLevel.Low, HasL3 = true, HasL4 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Database, "使用記錄", "現版頂部導航下，切換站別的平均點擊數 3.4 次"),
                E(EvidenceSource.UserInput, "現場", "操作員在手套下使用觸控，誤觸率高"),
                E(EvidenceSource.Paper, "可用性研究", "側邊導航在項目 > 7 時搜尋時間較短")),
            Hypotheses = ImmutableArray.Create(
                H(Mechanisms.DesignTradeoff, "側邊導航常駐可見",
                  "站別數 > 7 時", "切換站別的平均點擊數下降", LikertBelief.Likely, new[] { 1, 3 }),
                H(Mechanisms.DesignTradeoff, "頂部導航保留垂直空間",
                  "資料表很長時", "捲動次數下降", LikertBelief.EvenOdds, new[] { 2 })),
            Expected = Expected.Proceed(Strategy.SingleAgent, "solver_verifier"),
            WhyRev2 = "「比較好」在這裡被操作化成可量測指標並附了 A/B 實驗（L4）→ 有 ground truth。風險低 → 單一 agent。"
        },
        new BenchCase
        {
            Id = "S3", Dimension = "S", Level = "High", Title = "自動駕駛倫理", Domain = "ethics",
            Prompt = "自動駕駛必須二選一時，應優先保護車內乘客還是行人？",
            GroundTruth = GroundTruthStatus.Contested, Risk = RiskLevel.High,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Paper, "各國法規", "德國倫理準則禁止以人數計算生命價值"),
                E(EvidenceSource.Paper, "調查研究", "受訪者多數支持保護行人，但自己買車時傾向保護乘客"),
                E(EvidenceSource.Paper, "產業實務", "多數廠商不公開此類決策邏輯")),
            Expected = Expected.Abstain(ReasonCode.DefinitionalDispute),
            WhyRev2 = "爭議在「應優先」的定義本身 → payload 型別強制為 DefinitionMap：列出各定義與其推論後果，不表示傾向。"
        },

        // ══════════════════ B · 領域廣度 ══════════════════
        new BenchCase
        {
            Id = "B1", Dimension = "B", Level = "Low", Title = "SemaphoreSlim", Domain = "concurrency_howto",
            Prompt = "如何用 C# SemaphoreSlim 限制並發任務數量？",
            Risk = RiskLevel.Low, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Paper, "官方文件", "SemaphoreSlim(initialCount, maxCount) 以 WaitAsync/Release 控制進入數"),
                E(EvidenceSource.SourceCode, "sample.cs", "在 finally 中 Release 可避免例外導致的計數洩漏")),
            Hypotheses = ImmutableArray.Create(
                H(Mechanisms.SpecificationFact, "SemaphoreSlim 併發上限",
                  "工作項超過上限時", "同時執行數不超過 n，可用計數器斷言", LikertBelief.AlmostCertain, new[] { 1, 2 })),
            Expected = Expected.Proceed(Strategy.SingleAgent, "solver_verifier"),
            WhyRev2 = "單一領域、可寫測試驗證、風險低 → 單一 agent（不做不必要的領域展開）。"
        },
        new BenchCase
        {
            Id = "B2", Dimension = "B", Level = "Medium", Title = "QR Code 識別失敗", Domain = "vision_debug",
            Prompt = "相機偶發識別 QR Code 失敗，應該怎麼改善？",
            Risk = RiskLevel.Medium, HasL3 = true, HasL4 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.RuntimeLog, "vision-log", "失敗影像的平均亮度比成功影像低 38%，且集中在早班"),
                E(EvidenceSource.Sensor, "照度計", "早班環境光 180 lux，午班 450 lux"),
                E(EvidenceSource.SourceCode, "Decoder.cs", "曝光時間寫死 8 ms，未依環境光調整")),
            Hypotheses = b2.ToImmutableArray(), CatalogSeed = SeedFrom(b2[0]),
            Actions = ImmutableArray.Create(
                A("A", "加裝同軸光源並改為自動曝光", 90, 20),
                A("B", "只改自動曝光", 60, 10),
                A("C", "增加重試次數", 15, 15)),
            Expected = Expected.Proceed(Strategy.SolverCriticVerifier, "engineering_root_cause_nocritic"),
            WhyRev2 = "光學 + 軟體跨領域，但有可重現的影像資料集（L3）與現場實驗（L4）→ 不需展開成流水線。"
        },
        new BenchCase
        {
            Id = "B3", Dimension = "B", Level = "High", Title = "新工廠自動化", Domain = "factory_design",
            Prompt = "從零設計新能源汽車電子產品測試產線，含上下料、測試、追溯、MES、品質與 ROI。",
            Risk = RiskLevel.High, HasL3 = true,
            Stages = Stages(
                ("機構/上下料", "節拍與機構方案", VerifierLevel.L3_ExecutableTest, "輸出：節拍計算 + 機構清單"),
                ("電氣/測試", "測項與儀器配置", VerifierLevel.L3_ExecutableTest, "輸出：測項矩陣 + 不確定度預算"),
                ("追溯/MES", "資料流與序號綁定", VerifierLevel.L2_Rule, "輸出：資料流圖 + schema"),
                ("品質/ROI", "Cpk 目標與投資回收", VerifierLevel.L3_ExecutableTest, "輸出：Cpk 推估 + ROI 模型")),
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "user", "年產能 120 萬件，稼動率目標 85%"),
                E(EvidenceSource.Database, "既有廠", "同類產線單站不良率 0.8%，返修成本每件 42 元")),
            Expected = Expected.Proceed(Strategy.MultiDomainPipeline, "multi_domain_pipeline"),
            WhyRev2 = "四段跨領域，每段的 verifier 等級不同 → 流水線；MES 段只有 L2，整體能力層被最弱一段壓住。"
        },

        // ══════════════════ C · 創造性 ══════════════════
        new BenchCase
        {
            Id = "C1", Dimension = "C", Level = "Low", Title = "NullReference 修復", Domain = "bug_fix",
            Prompt = "NullReferenceException 出現在這一行，請找原因。",
            Risk = RiskLevel.Low, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.RuntimeLog, "stack trace", "在 DeviceList[0].Name 取值處拋出，DeviceList.Count == 0"),
                E(EvidenceSource.SourceCode, "Startup.cs", "列舉裝置的非同步初始化未 await，UI 先讀取集合")),
            Hypotheses = ImmutableArray.Create(
                H(Mechanisms.RaceCondition, "Startup 裝置列舉/UI 讀取",
                  "非同步初始化未 await 即被讀取", "集合在讀取當下 Count == 0", LikertBelief.AlmostCertain, new[] { 1, 2 })),
            Expected = Expected.Proceed(Strategy.SingleAgent, "solver_verifier"),
            WhyRev2 = "有 stack trace 可重現、風險低 → 單一 agent + 可執行測試，不需要生成大量候選。"
        },
        new BenchCase
        {
            Id = "C2", Dimension = "C", Level = "Medium", Title = "LabVIEW QMH 重構", Domain = "architecture",
            Prompt = "如何重構 QMH，讓多個測試 Station 共用儀器？",
            Risk = RiskLevel.Medium, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.SourceCode, "現有 VI", "每個 Station 各自開啟 VISA session，同一台電源被重複開啟"),
                E(EvidenceSource.RuntimeLog, "錯誤記錄", "VISA error -1073807246（資源被佔用）每班約 3 次"),
                E(EvidenceSource.UserInput, "現場", "四個 Station 共用一台可程式電源與一台 DMM")),
            Hypotheses = c2.ToImmutableArray(),
            Actions = ImmutableArray.Create(
                A("A", "導入儀器仲裁者 + 請求佇列", 95, -10),
                A("B", "只加重試與退避", 30, 35),
                A("C", "為每站各配一台儀器", 40, 40)),
            Expected = Expected.Proceed(Strategy.SolverCriticVerifier, "engineering_root_cause_nocritic"),
            WhyRev2 = "可用原型驗證（L3），風險中等 → 多 solver 產生方案，但 L1 critic 在機器驗證器面前邊際價值低。"
        },
        new BenchCase
        {
            Id = "C3", Dimension = "C", Level = "High", Title = "旅行 App 廣告", Domain = "creative_campaign",
            Prompt = "為年輕人旅行 App 設計可在 TikTok 傳播的廣告概念。",
            GroundTruth = GroundTruthStatus.Undefined, Risk = RiskLevel.Low,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Database, "投放數據", "過去六支影片的完播率介於 11%–38%"),
                E(EvidenceSource.UserInput, "行銷", "目標客群 18–24 歲，預算 60 萬")),
            Expected = Expected.Abstain(ReasonCode.NoGroundTruth),
            WhyRev2 = "投放前沒有可檢核的判準，且 Phase 1 沒有 Diversity 模組 → 拒答並給考量地圖，而不是假裝做創意搜尋。"
        },

        // ══════════════════ T · 時間不穩定性 ══════════════════
        new BenchCase
        {
            Id = "T1", Dimension = "T", Level = "Low", Title = "經典自由落體", Domain = "physics",
            Prompt = "忽略空氣阻力，自由落體物體 3 秒後速度是多少？",
            Risk = RiskLevel.Low, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Paper, "力學", "v = g·t，標準重力 g = 9.80665 m/s²"),
                E(EvidenceSource.UserInput, "user", "初速為零，忽略空氣阻力")),
            Hypotheses = ImmutableArray.Create(
                H(Mechanisms.SpecificationFact, "等加速度運動",
                  "初速為零且忽略阻力", "v = 9.80665 × 3 ≈ 29.4 m/s，可用落體實驗驗證", LikertBelief.AlmostCertain, new[] { 1, 2 })),
            Expected = Expected.Proceed(Strategy.SingleAgent, "solver_verifier"),
            WhyRev2 = "時間不變的物理題，可規則檢核 → 單一 agent。不需要新鮮證據。"
        },
        new BenchCase
        {
            Id = "T2", Dimension = "T", Level = "Medium", Title = "工業 PC 轉 ARM", Domain = "trend",
            Prompt = "未來三年工業 PC 是否應該全面轉向 ARM？",
            Risk = RiskLevel.Medium,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Web, "產業報導", "主要工控廠商已推出 ARM 平台但生態仍以 x86 為主"),
                E(EvidenceSource.Database, "現有資產", "現場 214 台工業 PC，其中 68 台跑 Windows 專用驅動")),
            Expected = Expected.Abstain(ReasonCode.NoVerifier),
            WhyRev2 = "三年後的產業走向只有未來的真實結果（L5）能檢核 → 拒答，並交還「缺什麼才做得了」的清單。"
        },
        new BenchCase
        {
            Id = "T3", Dimension = "T", Level = "High", Title = "BTC 七日預測", Domain = "market_forecast",
            Prompt = "根據目前宏觀、新聞和資金流，預測 BTC 未來 7 天。",
            Reflexive = true, Risk = RiskLevel.High,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Web, "新聞", "本週 ETF 淨流入轉正"),
                E(EvidenceSource.Database, "鏈上資料", "交易所餘額連續 5 日下降"),
                E(EvidenceSource.Database, "衍生品", "永續合約資金費率維持正值")),
            Expected = Expected.Abstain(ReasonCode.ReflexiveDirection),
            WhyRev2 = "反身系統 → 禁止方向預測。情境必須對稱：樂觀寫 5 條、悲觀寫 1 條會被 L2 退回。"
        },

        // ══════════════════ R · 風險 ══════════════════
        new BenchCase
        {
            Id = "R1", Dimension = "R", Level = "Low", Title = "今晚看哪部電影", Domain = "entertainment",
            Prompt = "晚上看《星際效應》還是《全面啟動》？",
            GroundTruth = GroundTruthStatus.Undefined, Risk = RiskLevel.Low,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "user", "兩部都沒看過，今晚有 3 小時"),
                E(EvidenceSource.Web, "片長", "兩部片長分別為 169 與 148 分鐘")),
            Expected = Expected.Abstain(ReasonCode.NoGroundTruth),
            WhyRev2 = "品味問題，沒有 ground truth。拒答成本很低，但仍給出可用的考量整理。"
        },
        new BenchCase
        {
            Id = "R2", Dimension = "R", Level = "Medium", Title = "LabVIEW 或 C#", Domain = "architecture_choice",
            Prompt = "新 ATS 專案應該使用 LabVIEW 還是 C#？",
            Risk = RiskLevel.Medium, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "團隊", "3 位工程師熟 LabVIEW，1 位熟 C#；專案需 MES 整合與單元測試"),
                E(EvidenceSource.Database, "歷史專案", "LabVIEW 專案平均交期短 22%，但後續維護工時多 40%"),
                E(EvidenceSource.SourceCode, "既有程式庫", "既有 C# 驅動層可重用 11 個儀器類別")),
            Hypotheses = r2.ToImmutableArray(),
            Actions = ImmutableArray.Create(
                A("A", "採用 C#", 85, 25),
                A("B", "採用 LabVIEW", 35, 80),
                A("C", "混合：LabVIEW 前端 + C# 驅動層", 60, 55)),
            Expected = Expected.Proceed(Strategy.SolverCriticVerifier, "engineering_root_cause_nocritic"),
            WhyRev2 = "兩個選項都能做原型量測（L3）；效用矩陣由人給，系統只算 EU / 最壞情況 / 最大後悔。"
        },
        new BenchCase
        {
            Id = "R3", Dimension = "R", Level = "High", Title = "高壓保護回路", Domain = "safety",
            Prompt = "懷疑耐壓 Station 保護回路偶發失效，是否可以繼續生產？",
            Risk = RiskLevel.High, HasL3 = true, HasL4 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.RuntimeLog, "安全 PLC", "過去 30 天有 2 次保護回路自檢超時，未觸發跳脫"),
                E(EvidenceSource.Sensor, "繼電器回饋", "K3 接點回饋延遲由 12 ms 漂移到 46 ms"),
                E(EvidenceSource.Paper, "IEC 61508", "安全功能需在規定時間內完成診斷，否則視為失效")),
            Hypotheses = r3.ToImmutableArray(), CatalogSeed = SeedFrom(r3[0]),
            Actions = ImmutableArray.Create(
                A("A", "停機更換 K3 並重做安全驗證", 90, 40),
                A("B", "降載繼續生產並加密監測", -40, 30),
                A("C", "照常生產", -100, 20)),
            Expected = Expected.Proceed(Strategy.SolverCriticVerifier, "engineering_root_cause", critic: true),
            WhyRev2 = "高風險 → 這是 Rev2 仍然保留 L1 critic 的情況；且「照常生產」的最壞情況超過可接受損失會被判為不可採。"
        },

        // ══════════════════ 組合壓力案例 ══════════════════
        new BenchCase
        {
            Id = "X1", Dimension = "Composite", Level = "Stress", Title = "數學路線優化", Domain = "optimization",
            Prompt = "給定 100 個城市及距離矩陣，求近似最短巡迴路線並驗證誤差。",
            Risk = RiskLevel.Low, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Database, "距離矩陣", "100×100 對稱矩陣，滿足三角不等式"),
                E(EvidenceSource.Paper, "Christofides", "度量 TSP 可在多項式時間得到 1.5 近似保證"),
                E(EvidenceSource.Database, "bench", "同資料集上 2-opt 起始解平均超出下界 7.8%")),
            Hypotheses = ImmutableArray.Create(
                H(Mechanisms.AlgorithmicChoice, "Christofides + 2-opt",
                  "度量 TSP 且需要誤差界時", "以 Held-Karp 下界計算的超出比例可量測", LikertBelief.AlmostCertain, new[] { 1, 2, 3 }),
                H(Mechanisms.AlgorithmicChoice, "純貪婪最近鄰",
                  "任何 TSP", "解的品質無界，超出比例波動大", LikertBelief.Unlikely, new[] { 3 }, new[] { 2 })),
            Expected = Expected.Proceed(Strategy.SingleAgent, "solver_verifier"),
            WhyRev2 = "誤差界可由程式算出（L3）、風險低 → 單一 agent。註：正式環境的 Tool/Model Router 很可能把這題轉介給 OR solver；這裡刻意保留在內部，因為交付物是可驗證的程式碼。"
        },
        new BenchCase
        {
            Id = "X2", Dimension = "Composite", Level = "Stress", Title = "臨終醫療倫理", Domain = "ethics",
            Prompt = "針對抽象臨終醫療倫理衝突，整理觀點、風險與決策邊界。",
            GroundTruth = GroundTruthStatus.Contested, Risk = RiskLevel.High,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Paper, "生命倫理", "自主原則與不傷害原則在此類情境直接衝突"),
                E(EvidenceSource.Paper, "臨床指引", "多數指引要求先確認病人本人的預立醫療決定")),
            Expected = Expected.Abstain(ReasonCode.DefinitionalDispute),
            WhyRev2 = "★ 這題正是 Rev1 的缺口：題目只要求「整理」，Rev1 卻只能回一句拒答。Rev2 的 DefinitionMap 直接把要求的東西交出來。"
        },
        new BenchCase
        {
            Id = "X3", Dimension = "Composite", Level = "Stress", Title = "大額股票投資", Domain = "high_risk_forecast",
            Prompt = "評估大額資金投入單一股票在未來一個月的行動方案。",
            Reflexive = true, Risk = RiskLevel.High,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "user", "可投入金額為總資產 35%"),
                E(EvidenceSource.Database, "價量", "該標的 30 日波動率 51%"),
                E(EvidenceSource.Database, "財報", "最近一季營收年增 28%，毛利率持平")),
            Expected = Expected.Abstain(ReasonCode.ReflexiveDirection),
            WhyRev2 = "高風險 + 反身 → 拒答。ScenarioBriefing 必須附風險曝險與退出條件，但不得暗示方向。"
        },
        new BenchCase
        {
            Id = "X4", Dimension = "Composite", Level = "Stress", Title = "ATS 新產線設計", Domain = "factory_design",
            Prompt = "設計並驗證跨機械、電氣、軟體、MES 與品質的新 ATS 產線。",
            Risk = RiskLevel.High, HasL3 = true, HasL4 = true,
            Stages = Stages(
                ("機械", "夾具與搬送設計", VerifierLevel.L3_ExecutableTest, "輸出：公差鏈 + 節拍"),
                ("電氣", "配電與安全連鎖", VerifierLevel.L3_ExecutableTest, "輸出：連鎖矩陣 + 線徑計算"),
                ("軟體", "序列引擎與復歸邏輯", VerifierLevel.L3_ExecutableTest, "輸出：狀態機 + 錯誤碼"),
                ("MES", "追溯與資料保存", VerifierLevel.L2_Rule, "輸出：schema"),
                ("品質", "量測系統分析", VerifierLevel.L4_Experiment, "輸出：GR&R 報告")),
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "user", "五個領域須整合交付，驗收含 GR&R"),
                E(EvidenceSource.Database, "既有線", "跨領域介面未定義造成上次專案延遲 7 週")),
            Expected = Expected.Proceed(Strategy.MultiDomainPipeline, "multi_domain_pipeline"),
            WhyRev2 = "五段流水線，每段結束都有人類邊界閘檢查介面契約 → 正面對應「獨立輸出但沒整合」這個禁止行為。"
        },
        new BenchCase
        {
            Id = "X5", Dimension = "Composite", Level = "Stress", Title = "政治諷刺喜劇", Domain = "creative_campaign",
            Prompt = "創作一組政治諷刺喜劇候選，並交由人類選擇。",
            GroundTruth = GroundTruthStatus.Undefined, Risk = RiskLevel.Medium,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.UserInput, "編劇", "節目長度 4 分鐘，需避免指名特定人士"),
                E(EvidenceSource.Database, "過往節目", "同時段節目的觀眾回饋以「意外性」為最常見的正面評語")),
            Expected = Expected.Abstain(ReasonCode.NoGroundTruth),
            WhyRev2 = "沒有 ground truth，且 Phase 1 沒有 mode collapse 偵測 → 若硬做創意搜尋，會悄悄崩塌成同一個點子的變體。"
        },
        new BenchCase
        {
            Id = "X6", Dimension = "Composite", Level = "Stress", Title = "五年生存率模型", Domain = "medical_statistics",
            Prompt = "僅測試抽象架構：建立癌症五年生存率統計模型，不提供醫療建議。",
            Risk = RiskLevel.High, HasL3 = true,
            Specialist = new SpecialistDomain("cox_survival",
                "存活分析有成熟的統計模型（Cox 比例風險 / Kaplan-Meier），不該由語言模型估計",
                "看 C-index、校準曲線，以及 Schoenfeld 殘差檢定的比例風險假設",
                "右設限比例 31% 時外推超過追蹤中位數的區間會很寬",
                ImmutableArray.Create("去識別化 cohort", "追蹤時間與設限旗標", "共變量清單")),
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Database, "去識別化 cohort", "n = 8,412，追蹤中位數 6.2 年，右設限比例 31%"),
                E(EvidenceSource.Paper, "方法學", "比例風險假設須以 Schoenfeld 殘差檢定"),
                E(EvidenceSource.Database, "holdout", "預留 20% 作為驗證集，未參與擬合")),
            Expected = Expected.Abstain(ReasonCode.DelegatedToExternalModel),
            WhyRev2 = "★ Tool/Model Router：存活分析是統計模型的領域。Rev1 會自己跑假設—驗證並給出信心上限 0.90；Rev2 認為那是用錯工具。"
        },
        new BenchCase
        {
            Id = "X7", Dimension = "Composite", Level = "Stress", Title = "高壓設備接地測試", Domain = "safety",
            Prompt = "按明確標準驗證高壓設備接地測試是否合格。",
            Risk = RiskLevel.High, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Paper, "IEC 61010", "保護接地連續性須在 25 A 測試電流下阻抗 ≤ 0.1 Ω"),
                E(EvidenceSource.Sensor, "微歐計", "實測 0.043 Ω（25 A，4 線法）"),
                E(EvidenceSource.RuntimeLog, "校驗記錄", "微歐計校驗有效期至 2027-03")),
            Hypotheses = x7.ToImmutableArray(),
            Actions = ImmutableArray.Create(
                A("A", "判定合格並放行（附量測記錄）", 90, -30),
                A("B", "補做多點掃描後再放行", 60, 70),
                A("C", "直接放行不留記錄", -100, -100)),
            Expected = Expected.Proceed(Strategy.SolverCriticVerifier, "engineering_root_cause", critic: true),
            WhyRev2 = "標準明確、可量測（L3）→ 這是規則判定，不是預測。高風險 → 保留 critic 與人類核准。"
        },
        new BenchCase
        {
            Id = "X8", Dimension = "Composite", Level = "Stress", Title = "最快演算法探索", Domain = "creative_algorithm",
            Prompt = "創造性設計多種最快演算法，並用可重複 Benchmark 驗證。",
            Risk = RiskLevel.Low, HasL3 = true,
            Evidence = ImmutableArray.Create(
                E(EvidenceSource.Database, "benchmark 套件", "固定輸入集與固定亂數種子，重跑變異 < 1.5%"),
                E(EvidenceSource.SourceCode, "baseline", "現行實作 1e6 筆耗時 42 ms"),
                E(EvidenceSource.Paper, "cache 最佳化", "分塊可降低 L2 miss 率")),
            Hypotheses = ImmutableArray.Create(
                H(Mechanisms.AlgorithmicChoice, "分塊 + SIMD 熱路徑",
                  "資料量超過 L2 容量時", "benchmark 上的加速比與 L2 miss 率", LikertBelief.AlmostCertain, new[] { 1, 2, 3 }),
                H(Mechanisms.AlgorithmicChoice, "PLINQ 平行化",
                  "核心數充足時", "加速比隨核心數變化", LikertBelief.EvenOdds, new[] { 2 })),
            Expected = Expected.Proceed(Strategy.SingleAgent, "solver_verifier"),
            WhyRev2 = "創意 + 可驗證：benchmark 是真 verifier（L3）。但 Phase 1 沒有 Diversity 模組，低風險下退化成單一 agent —— 已知缺口，不是正確結果。"
        });
    }
}
