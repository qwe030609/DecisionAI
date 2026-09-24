// ============================================================================
//  PolicySnapshot — 學習狀態與決策讀取解耦（D7）。
//  一個 case 從頭到尾只讀它 pin 的那一份；Evaluation 只產生新版本。
//  Rev2 Phase 2：加入校準曲線、錯誤相關矩陣、conformal 分位數與角色資格後驗。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;

namespace DecisionAI.Core.Policy;

/// <summary>Beta(a, b) 後驗。先驗 Beta(1,1)：樣本少時自然收縮到 0.5。</summary>
public sealed record BetaPosterior(double A = 1, double B = 1)
{
    public int N => (int)(A + B - 2);
    public double Mean => A / (A + B);
    public double Variance => A * B / ((A + B) * (A + B) * (A + B + 1));
    public BetaPosterior Observe(bool success) => success ? this with { A = A + 1 } : this with { B = B + 1 };
    public static readonly BetaPosterior Prior = new();

    /// <summary>
    /// Thompson 抽樣：從後驗抽一個樣本，而不是取均值。
    /// 冷啟動時等同隨機（正確行為——此時確實不知道誰行），不確定性大的 arm 自然被多探索。
    /// 用 Beta 的 Gamma 表示法，兩個 Gamma 由呼叫端提供的均勻亂數導出（保持可重放）。
    /// </summary>
    public double Sample(double u1, double u2)
    {
        double x = GammaFromUniform(A, u1), y = GammaFromUniform(B, u2);
        return x + y <= 0 ? Mean : x / (x + y);
    }

    /// <summary>形狀參數小時用 Wilson–Hilferty 近似；只需要決定性與單調性，不需要精確的 Gamma 抽樣。</summary>
    private static double GammaFromUniform(double shape, double u)
    {
        u = Math.Clamp(u, 1e-6, 1 - 1e-6);
        double z = InverseNormal(u);
        double d = shape - 1.0 / 3.0;
        if (d <= 0) return Math.Max(1e-9, shape * u);
        double v = 1 + z / Math.Sqrt(9 * d);
        return Math.Max(1e-9, d * v * v * v);
    }

    /// <summary>Acklam 近似的簡化版：單調、決定性，足夠做抽樣。</summary>
    private static double InverseNormal(double p)
    {
        double q = p - 0.5;
        if (Math.Abs(q) <= 0.425)
        {
            double r = 0.180625 - q * q;
            return q * (((2509.08 * r + 33430.6) * r + 67265.8) * r + 45921.95) /
                       (((5226.5 * r + 28729.1) * r + 39307.9) * r + 21213.79);
        }
        double s = Math.Sqrt(-2 * Math.Log(q < 0 ? p : 1 - p));
        double t = -(2.30753 + 0.27061 * s) / (1 + (0.99229 + 0.04481 * s) * s) + s;
        return q < 0 ? -t : t;
    }
}

public readonly record struct WeightKey(string Agent, string Domain, string Role);
public readonly record struct RoleFitKey(string Agent, string Role);
public readonly record struct PairKey(string A, string B)
{
    /// <summary>無序對：永遠以字典序較小者在前，讓查表決定性。</summary>
    public static PairKey Of(string x, string y)
        => string.CompareOrdinal(x, y) <= 0 ? new PairKey(x, y) : new PairKey(y, x);
}

/// <summary>某個 agent 的（自報機率, 實際結果）紀錄，算 Brier / ECE 並回推收縮係數。</summary>
public sealed record CalibrationCurve(ImmutableArray<(double P, bool Y)> Records)
{
    public static readonly CalibrationCurve Empty = new(ImmutableArray<(double, bool)>.Empty);

    public int N => Records.Length;

    public double? Brier => N == 0 ? null : Records.Average(r => Math.Pow(r.P - (r.Y ? 1 : 0), 2));

    /// <summary>Expected Calibration Error：分 bins，|平均自報 − 實際命中率| 的加權平均。</summary>
    public double? Ece(int bins = 5)
    {
        if (N == 0) return null;
        double e = 0;
        foreach (var g in Records.GroupBy(r => Math.Min(bins - 1, (int)(r.P * bins))))
            e += g.Count() / (double)N * Math.Abs(g.Average(r => r.P) - g.Average(r => r.Y ? 1 : 0));
        return e;
    }

    /// <summary>
    /// 整體收縮係數：樣本 &lt; 5 一律打七折（沒證明自己之前不信）；
    /// 之後依「自報 − 實際」的過度自信程度決定（Platt scaling 的一維簡化版）。
    /// 注意它是「所有機率值平均起來」的過度自信——Phase 3 的 Bucket 才是分桶的那個。
    /// </summary>
    public double ShrinkFactor
    {
        get
        {
            if (N < 5) return 0.7;
            double over = Records.Average(r => (r.P - 0.5) * ((r.P - 0.5) - ((r.Y ? 1 : 0) - 0.5)) / 0.25);
            return Math.Clamp(1 - over, 0.3, 1.0);
        }
    }

    public CalibrationCurve Add(double p, bool y) => new(Records.Add((Math.Clamp(p, 0.01, 0.99), y)));

    /// <summary>
    /// Phase 3：某個自報機率「附近」的實際命中率。
    ///
    /// 全域的 ECE 會把不同機率值的誤差平均掉：一個 agent 可能在 0.5 那一帶很準、
    /// 在 0.9 那一帶嚴重灌水，平均起來看起來只是「稍微樂觀」。真正要修的是後者，
    /// 而要修它就得看它自己那一桶的資料。這是 Platt / isotonic 的核心，
    /// 只是這裡用最樸素的鄰域平均——樣本少的時候，複雜的擬合只會擬合到雜訊。
    /// </summary>
    public (double Rate, int N)? Bucket(double p, double halfWidth = 0.1)
    {
        var rows = Records.Where(r => Math.Abs(r.P - p) <= halfWidth).ToList();
        return rows.Count == 0 ? null : (rows.Count(r => r.Y) / (double)rows.Count, rows.Count);
    }
}

/// <summary>錯誤相關矩陣：phi 係數 + 共同案例數。共同案例少於 3 → 視為無資訊。</summary>
public sealed record CorrelationMatrix(ImmutableDictionary<PairKey, (double Phi, int Shared)> Pairs)
{
    public static readonly CorrelationMatrix Empty = new(ImmutableDictionary<PairKey, (double, int)>.Empty);

    public (double Phi, int Shared) Get(string a, string b)
        => a == b ? (1.0, int.MaxValue) : Pairs.GetValueOrDefault(PairKey.Of(a, b), (0.0, 0));
}

/// <summary>Conformal 的 (1−α) 分位數，逐 TaskFamily 保存。</summary>
public sealed record ConformalQuantile(double Alpha, double Threshold, int CalibrationSize, int AuditSize);

/// <summary>角色資格：探針量到的表現。critic 的能力就是「抓得到植入的錯」，可精確量測。</summary>
public sealed record RoleFitPosterior(BetaPosterior Posterior, double? LastProbeScore, int ProbeRuns)
{
    public static readonly RoleFitPosterior Unknown = new(BetaPosterior.Prior, null, 0);
    public double Mean => Posterior.Mean;
}

public sealed record PolicySnapshot(
    long Version,
    ImmutableDictionary<WeightKey, BetaPosterior> Weights,
    long ClaimCatalogVersion = 0,
    long ExperimentCatalogVersion = 0)
{
    public ImmutableDictionary<string, CalibrationCurve> Calibration { get; init; }
        = ImmutableDictionary<string, CalibrationCurve>.Empty;
    public CorrelationMatrix Correlation { get; init; } = CorrelationMatrix.Empty;
    public ImmutableDictionary<string, ConformalQuantile> Conformal { get; init; }
        = ImmutableDictionary<string, ConformalQuantile>.Empty;
    public ImmutableDictionary<RoleFitKey, RoleFitPosterior> RoleFit { get; init; }
        = ImmutableDictionary<RoleFitKey, RoleFitPosterior>.Empty;

    public static readonly PolicySnapshot Initial = new(0, ImmutableDictionary<WeightKey, BetaPosterior>.Empty);

    public BetaPosterior Weight(string agent, string domain, string role)
        => Weights.GetValueOrDefault(new WeightKey(agent, domain, role), BetaPosterior.Prior);

    public CalibrationCurve Curve(string agent) => Calibration.GetValueOrDefault(agent, CalibrationCurve.Empty);

    public RoleFitPosterior Fit(string agent, string role)
        => RoleFit.GetValueOrDefault(new RoleFitKey(agent, role), RoleFitPosterior.Unknown);

    public PolicySnapshot Apply(PolicyDelta delta)
    {
        var w = Weights.ToBuilder();
        foreach (var o in delta.Observations.Where(o => VerifierTrust.UpdatesWeights(o.Level)))
        {
            var key = new WeightKey(o.Agent, o.Domain, o.Role);
            w[key] = w.GetValueOrDefault(key, BetaPosterior.Prior).Observe(o.Success);
        }

        var cal = Calibration.ToBuilder();
        foreach (var c in delta.CalibrationPoints)
            cal[c.Agent] = cal.GetValueOrDefault(c.Agent, CalibrationCurve.Empty).Add(c.Stated, c.Truth);

        var corr = Correlation.Pairs.ToBuilder();
        foreach (var p in delta.PairObservations)
        {
            var key = PairKey.Of(p.A, p.B);
            var (phi, shared) = corr.GetValueOrDefault(key, (0.0, 0));
            int n = shared + 1;
            // 線上更新 phi 的平均：兩人同時錯或同時對記 +1，一對一錯記 −1
            double signal = p.BothWrong || p.BothRight ? 1 : -1;
            corr[key] = ((phi * shared + signal) / n, n);
        }

        var fit = RoleFit.ToBuilder();
        foreach (var p in delta.ProbeResults)
        {
            var key = new RoleFitKey(p.Agent, p.Role);
            var prev = fit.GetValueOrDefault(key, RoleFitPosterior.Unknown);
            fit[key] = prev with
            {
                Posterior = prev.Posterior.Observe(p.Passed),
                LastProbeScore = p.Score,
                ProbeRuns = prev.ProbeRuns + 1
            };
        }

        var conf = Conformal.ToBuilder();
        foreach (var q in delta.ConformalUpdates) conf[q.Key] = q.Value;

        return this with
        {
            Version = Version + 1,
            Weights = w.ToImmutable(),
            ClaimCatalogVersion = delta.CatalogVersionAfter ?? ClaimCatalogVersion,
            ExperimentCatalogVersion = delta.ExperimentCatalogVersionAfter ?? ExperimentCatalogVersion,
            Calibration = cal.ToImmutable(),
            Correlation = new CorrelationMatrix(corr.ToImmutable()),
            RoleFit = fit.ToImmutable(),
            Conformal = conf.ToImmutable()
        };
    }

    public string Report()
    {
        var lines = new List<string>
        {
            $"policy v{Version}（claim catalog v{ClaimCatalogVersion}、experiment catalog v{ExperimentCatalogVersion}）",
            "agent            domain/role                      mean   n   Brier  ECE"
        };
        foreach (var kv in Weights.OrderBy(k => k.Key.Agent).ThenBy(k => k.Key.Role))
        {
            var c = Curve(kv.Key.Agent);
            lines.Add($"{kv.Key.Agent,-16} {kv.Key.Domain + "/" + kv.Key.Role,-32} {kv.Value.Mean:F2}   {kv.Value.N}" +
                      $"   {(c.Brier is { } b ? b.ToString("F3") : "  —  ")}  {(c.Ece() is { } e ? e.ToString("F3") : "  —  ")}");
        }
        return string.Join("\n", lines);
    }
}

public sealed record WeightObservation(string Agent, string Domain, string Role, bool Success, VerifierLevel Level);
public sealed record CalibrationPoint(string Agent, double Stated, bool Truth);
public sealed record PairObservation(string A, string B, bool BothWrong, bool BothRight);
public sealed record ProbeResult(string Agent, string Role, bool Passed, double Score);
public sealed record CatalogEntryCandidate(string DraftId, ClaimFrame Frame, string Domain, bool ConfirmedTrue);

public sealed record PolicyDelta(
    string CaseId,
    ImmutableArray<WeightObservation> Observations,
    ImmutableArray<CatalogEntryCandidate> CatalogEntries,
    ImmutableArray<string> Notes,
    long? CatalogVersionAfter = null)
{
    public ImmutableArray<CalibrationPoint> CalibrationPoints { get; init; } = ImmutableArray<CalibrationPoint>.Empty;
    public ImmutableArray<PairObservation> PairObservations { get; init; } = ImmutableArray<PairObservation>.Empty;
    public ImmutableArray<ProbeResult> ProbeResults { get; init; } = ImmutableArray<ProbeResult>.Empty;
    public ImmutableDictionary<string, ConformalQuantile> ConformalUpdates { get; init; }
        = ImmutableDictionary<string, ConformalQuantile>.Empty;
    public long? ExperimentCatalogVersionAfter { get; init; }
}
