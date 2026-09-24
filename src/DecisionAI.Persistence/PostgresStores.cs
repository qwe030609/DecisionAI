// ============================================================================
//  Postgres + pgvector 實作（Rev2 §8「持久化躲在 port 後面」/ Phase 3）
//
//  呼叫端零改動：這些類別實作的是 Phase 1 就定好的那幾個 port。
//  能做到這件事，是因為當初沒有讓任何模組直接碰 Dictionary。
//
//  兩個刻意的決定：
//   · PolicySnapshot 存 delta 不存快照。存快照的話，欄位一加舊快照就讀不回來；
//     存 delta 是「發生過什麼」，加欄位不會讓歷史失效。代價是讀取要摺疊，
//     所以這裡快取摺疊結果——快取的是純函數的輸出，不會有一致性問題。
//   · 嵌入向量只用來縮小候選。是不是同一個故障仍由四元組比對決定，
//     否則「相似度 0.83」會悄悄變成「這是同一個故障」。
// ============================================================================

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Catalog;
using DecisionAI.Modules.Evaluation;
using DecisionAI.Modules.Retrieval;
using Npgsql;

namespace DecisionAI.Persistence;

public static class Db
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>建 schema。冪等：整份 SQL 都是 IF NOT EXISTS。</summary>
    public static void EnsureSchema(string connectionString, string? schemaSql = null)
    {
        schemaSql ??= File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Schema.sql"));
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(schemaSql, conn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>測試用：清空所有資料表但保留 schema。</summary>
    public static void Truncate(string connectionString)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "TRUNCATE case_event, policy_delta, claim_catalog, experiment_outcome, conformal_sample, human_decision;", conn);
        cmd.ExecuteNonQuery();
    }

    internal static string Vector(float[] v)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < v.Length; i++) { if (i > 0) sb.Append(','); sb.Append(v[i].ToString("G9")); }
        return sb.Append(']').ToString();
    }
}

// ── PolicyStore ─────────────────────────────────────────────────────────

/// <summary>
/// append-only 的 delta 表 + 摺疊快取。Pin(version) 讀得到任何歷史版本，
/// 因為歷史是「發生過什麼」的序列，不是某個當時的欄位集合。
/// </summary>
public sealed class PostgresPolicyStore : IPolicyStore
{
    private readonly string _cs;
    private readonly Dictionary<long, PolicySnapshot> _cache = new() { [0] = PolicySnapshot.Initial };
    private readonly object _lock = new();

    public PostgresPolicyStore(string connectionString)
    {
        _cs = connectionString;
        lock (_lock) Rebuild();
    }

    public PolicySnapshot Current { get { lock (_lock) return _cache[_cache.Keys.Max()]; } }

    public PolicySnapshot Pin(long? version = null)
    {
        lock (_lock)
        {
            if (version is null) return _cache[_cache.Keys.Max()];
            if (_cache.TryGetValue(version.Value, out var snap)) return snap;
            Rebuild();
            return _cache.TryGetValue(version.Value, out var again)
                ? again : throw new InvalidOperationException($"policy v{version} 不存在");
        }
    }

    public PolicySnapshot Commit(PolicyDelta delta)
    {
        lock (_lock)
        {
            long next = _cache.Keys.Max() + 1;
            using var conn = new NpgsqlConnection(_cs);
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "INSERT INTO policy_delta (version, case_id, payload) VALUES (@v, @c, @p::jsonb)", conn);
            cmd.Parameters.AddWithValue("v", next);
            cmd.Parameters.AddWithValue("c", delta.CaseId);
            cmd.Parameters.AddWithValue("p", JsonSerializer.Serialize(delta, Db.Json));
            cmd.ExecuteNonQuery();

            var snapshot = _cache[next - 1].Apply(delta);
            _cache[next] = snapshot;
            return snapshot;
        }
    }

    private void Rebuild()
    {
        _cache.Clear();
        _cache[0] = PolicySnapshot.Initial;
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand("SELECT version, payload FROM policy_delta ORDER BY version", conn);
        using var r = cmd.ExecuteReader();
        var current = PolicySnapshot.Initial;
        while (r.Read())
        {
            long v = r.GetInt64(0);
            var delta = JsonSerializer.Deserialize<PolicyDelta>(r.GetString(1), Db.Json)
                        ?? throw new InvalidOperationException($"policy_delta v{v} 無法還原");
            current = current.Apply(delta);
            _cache[v] = current;
        }
    }
}

// ── ClaimCatalog ────────────────────────────────────────────────────────

/// <summary>
/// append-only 的 Catalog。既有 entry 只能累加命中次數，語意不可就地修改——
/// 改掉一條 entry 的意思，會讓所有引用過它的歷史似然默默失效。
/// </summary>
public sealed class PostgresClaimCatalog : IClaimCatalog
{
    private readonly string _cs;
    private readonly IEmbedder? _embedder;
    private readonly object _lock = new();

    public PostgresClaimCatalog(string connectionString, IEmbedder? embedder = null)
    { _cs = connectionString; _embedder = embedder; }

    public long CurrentVersion
    {
        get
        {
            using var conn = new NpgsqlConnection(_cs);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT COALESCE(MAX(added_at_version), 0) FROM claim_catalog", conn);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public ClaimCatalogView View(long version)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            @"SELECT catalog_id, domain, mechanism, locus, trigger_, observable, added_at_version, confirmed_true_count
              FROM claim_catalog WHERE added_at_version <= @v ORDER BY added_at_version, catalog_id", conn);
        cmd.Parameters.AddWithValue("v", version);
        using var r = cmd.ExecuteReader();
        var entries = ImmutableArray.CreateBuilder<CatalogEntry>();
        while (r.Read())
            entries.Add(new CatalogEntry(r.GetString(0),
                new ClaimFrame(r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)),
                r.GetString(1), r.GetInt64(6), r.GetInt32(7)));
        return new ClaimCatalogView(version, entries.ToImmutable());
    }

    public CatalogEntry Register(ClaimFrame frame, string domain, bool confirmedTrue)
    {
        lock (_lock)
        {
            using var conn = new NpgsqlConnection(_cs);
            conn.Open();

            // 同一個 Mechanism|Locus 視為同一條，與記憶體實作一致
            using (var find = new NpgsqlCommand(
                @"SELECT catalog_id, mechanism, locus, trigger_, observable, added_at_version, confirmed_true_count
                  FROM claim_catalog WHERE domain = @d AND mechanism = @m AND locus = @l", conn))
            {
                find.Parameters.AddWithValue("d", domain);
                find.Parameters.AddWithValue("m", frame.Mechanism);
                find.Parameters.AddWithValue("l", ClaimFrame.Norm(frame.Locus));
                using var rr = find.ExecuteReader();
                if (rr.Read())
                {
                    var existing = new CatalogEntry(rr.GetString(0),
                        new ClaimFrame(rr.GetString(1), rr.GetString(2), rr.GetString(3), rr.GetString(4)),
                        domain, rr.GetInt64(5), rr.GetInt32(6));
                    rr.Close();
                    if (!confirmedTrue) return existing;

                    using var bump = new NpgsqlCommand(
                        "UPDATE claim_catalog SET confirmed_true_count = confirmed_true_count + 1 WHERE catalog_id = @id", conn);
                    bump.Parameters.AddWithValue("id", existing.CatalogId);
                    bump.ExecuteNonQuery();
                    return existing with { ConfirmedTrueCount = existing.ConfirmedTrueCount + 1 };
                }
            }

            long version = CurrentVersionOn(conn) + 1;
            string id = $"CLM-{domain}-{frame.Mechanism}-{ClaimFrame.Hash(ClaimFrame.Norm(frame.Locus))[..6]}";
            using var ins = new NpgsqlCommand(
                @"INSERT INTO claim_catalog
                    (catalog_id, domain, mechanism, locus, trigger_, observable, added_at_version, confirmed_true_count, embedding)
                  VALUES (@id, @d, @m, @l, @t, @o, @v, @n, CAST(@e AS vector))", conn);
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("d", domain);
            ins.Parameters.AddWithValue("m", frame.Mechanism);
            ins.Parameters.AddWithValue("l", ClaimFrame.Norm(frame.Locus));
            ins.Parameters.AddWithValue("t", frame.Trigger);
            ins.Parameters.AddWithValue("o", frame.Observable);
            ins.Parameters.AddWithValue("v", version);
            ins.Parameters.AddWithValue("n", confirmedTrue ? 1 : 0);
            ins.Parameters.AddWithValue("e", (object?)(_embedder is null ? null : Db.Vector(_embedder.Embed(frame.Render()))) ?? DBNull.Value);
            ins.ExecuteNonQuery();

            return new CatalogEntry(id, frame with { Locus = ClaimFrame.Norm(frame.Locus) }, domain, version, confirmedTrue ? 1 : 0);
        }
    }

    private static long CurrentVersionOn(NpgsqlConnection conn)
    {
        using var cmd = new NpgsqlCommand("SELECT COALESCE(MAX(added_at_version), 0) FROM claim_catalog", conn);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}

// ── pgvector 索引 ───────────────────────────────────────────────────────

/// <summary>Catalog 大到線性掃描不划算時用。它只縮小候選，不做判定。</summary>
public sealed class PgVectorIndex : IVectorIndex
{
    private readonly string _cs;
    private readonly IEmbedder _embedder;

    public PgVectorIndex(string connectionString, IEmbedder embedder)
    { _cs = connectionString; _embedder = embedder; }

    public string Name => $"pgvector/{_embedder.Name}";

    public int Count
    {
        get
        {
            using var conn = new NpgsqlConnection(_cs);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT count(*) FROM claim_catalog WHERE embedding IS NOT NULL", conn);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Upsert(string id, string domain, string text)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE claim_catalog SET embedding = CAST(@e AS vector) WHERE catalog_id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("e", Db.Vector(_embedder.Embed(text)));
        cmd.ExecuteNonQuery();
    }

    public ImmutableArray<VectorHit> Search(string text, string domain, int k)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            @"SELECT catalog_id, 1 - (embedding <=> CAST(@e AS vector)) AS sim, domain
              FROM claim_catalog
              WHERE domain = @d AND embedding IS NOT NULL
              ORDER BY embedding <=> CAST(@e AS vector), catalog_id
              LIMIT @k", conn);
        cmd.Parameters.AddWithValue("e", Db.Vector(_embedder.Embed(text)));
        cmd.Parameters.AddWithValue("d", domain);
        cmd.Parameters.AddWithValue("k", Math.Max(1, k));
        using var r = cmd.ExecuteReader();
        var hits = ImmutableArray.CreateBuilder<VectorHit>();
        while (r.Read()) hits.Add(new VectorHit(r.GetString(0), Math.Round(r.GetDouble(1), 6), r.GetString(2)));
        return hits.ToImmutable();
    }
}

// ── 實驗歷史頻率 ────────────────────────────────────────────────────────

public sealed class PostgresExperimentCatalog : IExperimentCatalog
{
    private readonly string _cs;
    public PostgresExperimentCatalog(string connectionString) => _cs = connectionString;

    public long CurrentVersion
    {
        get
        {
            using var conn = new NpgsqlConnection(_cs);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT COALESCE(SUM(n), 0) FROM experiment_outcome", conn);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public ExperimentCatalogView View(long version)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            @"SELECT experiment_key, claim_catalog_id, outcome_index, outcome_count, n
              FROM experiment_outcome ORDER BY experiment_key, claim_catalog_id, outcome_index", conn);
        using var r = cmd.ExecuteReader();

        var rows = new Dictionary<(string, string), int[]>();
        while (r.Read())
        {
            var key = (r.GetString(0), r.GetString(1));
            if (!rows.TryGetValue(key, out var counts)) rows[key] = counts = new int[r.GetInt32(3)];
            int idx = r.GetInt32(2);
            if (idx < counts.Length) counts[idx] = r.GetInt32(4);
        }

        return new ExperimentCatalogView(version, rows
            .Select(kv => new HistoricalLikelihood(kv.Key.Item1, kv.Key.Item2, kv.Value.ToImmutableArray()))
            .OrderBy(h => h.ExperimentKey, StringComparer.Ordinal).ToImmutableArray());
    }

    public void Record(string experimentKey, string claimCatalogId, int outcomeIndex, int outcomeCount)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            @"INSERT INTO experiment_outcome (experiment_key, claim_catalog_id, outcome_index, outcome_count, n)
              VALUES (@k, @c, @i, @n, 1)
              ON CONFLICT (experiment_key, claim_catalog_id, outcome_index)
              DO UPDATE SET n = experiment_outcome.n + 1", conn);
        cmd.Parameters.AddWithValue("k", experimentKey);
        cmd.Parameters.AddWithValue("c", claimCatalogId);
        cmd.Parameters.AddWithValue("i", outcomeIndex);
        cmd.Parameters.AddWithValue("n", outcomeCount);
        cmd.ExecuteNonQuery();
    }
}

// ── conformal 樣本庫 ────────────────────────────────────────────────────

public sealed class PostgresConformalSampleStore : IConformalSampleStore
{
    private readonly string _cs;
    public PostgresConformalSampleStore(string connectionString) => _cs = connectionString;

    public void Add(ConformalSample s)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            @"INSERT INTO conformal_sample (task_family, true_claim, beliefs, is_audit)
              VALUES (@f, @t, @b::jsonb, @a)", conn);
        cmd.Parameters.AddWithValue("f", s.TaskFamily);
        cmd.Parameters.AddWithValue("t", s.TrueClaimId);
        cmd.Parameters.AddWithValue("b", JsonSerializer.Serialize(s.Beliefs, Db.Json));
        cmd.Parameters.AddWithValue("a", s.IsAudit);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ConformalSample> For(string taskFamily)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT task_family, true_claim, beliefs, is_audit FROM conformal_sample WHERE task_family = @f ORDER BY id", conn);
        cmd.Parameters.AddWithValue("f", taskFamily);
        using var r = cmd.ExecuteReader();
        var list = new List<ConformalSample>();
        while (r.Read())
        {
            var beliefs = JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(2), Db.Json)
                          ?? new Dictionary<string, double>();
            list.Add(new ConformalSample(r.GetString(0), beliefs.ToImmutableDictionary(), r.GetString(1), r.GetBoolean(3)));
        }
        return list;
    }

    public ImmutableDictionary<string, ConformalQuantile> RebuildAll(double alpha = 0.10)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand("SELECT DISTINCT task_family FROM conformal_sample", conn);
        var families = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) families.Add(r.GetString(0));

        return families.Where(TaskFamilies.Whitelist.Contains)
                       .OrderBy(f => f, StringComparer.Ordinal)
                       .ToImmutableDictionary(f => f, f => ConformalCalibrationBuilder.Build(For(f), alpha));
    }

    public double EmpiricalCoverage(string taskFamily, ConformalQuantile q)
        => ConformalCalibrationBuilder.EmpiricalCoverage(For(taskFamily), q);
}

// ── 人類決策稽核 ────────────────────────────────────────────────────────

/// <summary>把 automation bias 的資料存下來。跨重啟的監控才有意義——橡皮圖章化是幾個月養成的。</summary>
public sealed class PostgresOversightLog
{
    private readonly string _cs;
    public PostgresOversightLog(string connectionString) => _cs = connectionString;

    public void Record(HumanDecision d)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            @"INSERT INTO human_decision (case_id, role, approver, approved, seconds, saw_recommendation_first)
              VALUES (@c, @r, @a, @ok, @s, @f)", conn);
        cmd.Parameters.AddWithValue("c", d.CaseId);
        cmd.Parameters.AddWithValue("r", d.Role.ToString());
        cmd.Parameters.AddWithValue("a", d.Approver);
        cmd.Parameters.AddWithValue("ok", d.Approved);
        cmd.Parameters.AddWithValue("s", (object?)d.Seconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("f", d.SawRecommendationFirst);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<HumanDecision> All()
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT case_id, role, approver, approved, seconds, saw_recommendation_first FROM human_decision ORDER BY id", conn);
        using var r = cmd.ExecuteReader();
        var list = new List<HumanDecision>();
        while (r.Read())
            list.Add(new HumanDecision(r.GetString(0), Enum.Parse<HumanRole>(r.GetString(1)), r.GetString(2),
                                       r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetDouble(4), r.GetBoolean(5)));
        return list;
    }
}
