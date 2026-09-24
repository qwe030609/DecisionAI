// ============================================================================
//  持久化測試（Phase 3）
//
//  需要一個真的 Postgres + pgvector：連線字串放在環境變數 DECISIONAI_PG。
//  沒設就整組跳過——「沒有資料庫時假裝通過」比紅燈更糟，所以這裡是 Skip 不是 Pass。
//
//  測的是三件事，每一件都對應一個會讓系統默默壞掉的失效：
//   1) 換成 Postgres 後，呼叫端的行為完全不變（port 的意義）
//   2) Pin(舊版本) 真的讀得到舊版本（進行中的 case 不該被別人的學習影響）
//   3) pgvector 只縮小候選，不做判定（相似度不得升格為「這是同一個故障」）
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Adapters.Runtime;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Policy;
using DecisionAI.Modules.Catalog;
using DecisionAI.Modules.Evaluation;
using DecisionAI.Modules.Retrieval;
using DecisionAI.Persistence;
using DecisionAI.Testing;
using Xunit;

namespace DecisionAI.Tests;

/// <summary>沒有資料庫就跳過整個 class，而不是讓它假裝通過。</summary>
public sealed class RequiresPostgres : FactAttribute
{
    public RequiresPostgres()
    {
        if (PostgresFixture.ConnectionString is null)
            Skip = "未設定 DECISIONAI_PG → 跳過持久化測試（不是通過）";
    }
}

public sealed class PostgresFixture : IDisposable
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("DECISIONAI_PG");

    public PostgresFixture()
    {
        if (ConnectionString is null) return;
        Db.EnsureSchema(ConnectionString, File.ReadAllText(SchemaPath()));
        Db.Truncate(ConnectionString);
    }

    private static string SchemaPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DecisionAI.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "DecisionAI.Persistence", "Schema.sql");
    }

    public void Dispose() { }
}

public class PersistenceTests : IClassFixture<PostgresFixture>
{
    private static string Cs => PostgresFixture.ConnectionString!;

    [RequiresPostgres]
    public void PolicyStore_ReplaysDeltas_AndPinnedVersionsStayFrozen()
    {
        Db.Truncate(Cs);
        var store = new PostgresPolicyStore(Cs);
        Assert.Equal(0, store.Current.Version);

        var v1 = store.Commit(Delta("CASE-1", ("solver-A", true), ("solver-B", false)));
        var v2 = store.Commit(Delta("CASE-2", ("solver-A", true), ("solver-B", false)));
        Assert.Equal(2, v2.Version);
        Assert.True(v2.Weight("solver-A", "d", "solver").Mean > v2.Weight("solver-B", "d", "solver").Mean);

        // pin 住的版本不會被後來的學習改寫——這正是「進行中的 case 不受影響」的實作
        Assert.Equal(v1.Weight("solver-A", "d", "solver").N, store.Pin(1).Weight("solver-A", "d", "solver").N);
        Assert.NotEqual(store.Pin(1).Weight("solver-A", "d", "solver").N, store.Pin(2).Weight("solver-A", "d", "solver").N);

        // 換一個 store 物件（模擬重啟）：摺疊 delta 必須得到一模一樣的快照
        var reopened = new PostgresPolicyStore(Cs);
        Assert.Equal(v2.Version, reopened.Current.Version);
        Assert.Equal(v2.Weight("solver-A", "d", "solver").Mean, reopened.Current.Weight("solver-A", "d", "solver").Mean, 9);
        Assert.Equal(v2.Curve("solver-A").Records.Length, reopened.Current.Curve("solver-A").Records.Length);
    }

    [RequiresPostgres]
    public void ClaimCatalog_IsAppendOnly_AndMatchesTheInMemorySemantics()
    {
        Db.Truncate(Cs);
        var pg = new PostgresClaimCatalog(Cs, new LocalEmbedder());
        var mem = new InMemoryClaimCatalog();

        var frame = AtsScenario.RaceFrame;
        var a = pg.Register(frame, "d", confirmedTrue: false);
        var b = mem.Register(frame, "d", confirmedTrue: false);
        Assert.Equal(b.CatalogId, a.CatalogId);                    // id 規則一致，跨儲存後端穩定

        // 同一個 Mechanism|Locus 只換措辭 → 不新增條目
        var reworded = frame with { Trigger = "換句話說的觸發", Observable = "換句話說的可觀察" };
        Assert.Equal(a.CatalogId, pg.Register(reworded, "d", confirmedTrue: false).CatalogId);
        Assert.Single(pg.View(pg.CurrentVersion).Entries);

        // confirmedTrue 只能累加，不能就地改語意
        var bumped = pg.Register(frame, "d", confirmedTrue: true);
        Assert.Equal(1, bumped.ConfirmedTrueCount);
        Assert.Equal(a.CatalogId, bumped.CatalogId);

        // 舊版本的 view 看不到後來新增的條目
        long before = pg.CurrentVersion;
        pg.Register(AtsScenario.NetworkFrame, "d", confirmedTrue: false);
        Assert.Single(pg.View(before).Entries);
        Assert.Equal(2, pg.View(pg.CurrentVersion).Entries.Length);
    }

    [RequiresPostgres]
    public void PgVector_NarrowsCandidates_ButNeverDecidesIdentity()
    {
        Db.Truncate(Cs);
        var embedder = new LocalEmbedder();
        var catalog = new PostgresClaimCatalog(Cs, embedder);
        var index = new PgVectorIndex(Cs, embedder);

        catalog.Register(AtsScenario.RaceFrame, "d", false);
        catalog.Register(AtsScenario.LifecycleFrame, "d", false);
        catalog.Register(AtsScenario.NetworkFrame, "d", false);
        Assert.Equal(3, index.Count);

        // 檢索找得到最像的那一條
        var hits = index.Search(AtsScenario.RaceFrame.Render(), "d", 2);
        Assert.NotEmpty(hits);
        Assert.Contains(AtsScenario.RaceFrame.Mechanism, hits[0].Id);

        // 但「像」不等於「是」：換一個機制、locus 也不同的四元組，
        // 檢索仍會回傳最近的幾條，判定卻必須是 New
        var stranger = new ClaimFrame(Mechanisms.WearOut, "完全不同的位置 XYZ", "觸發", "可觀察");
        var assisted = new RetrievalAssistedCanonicalizer(new ClaimCanonicalizer(), index);
        var r = assisted.Canonicalize(stranger, catalog.View(catalog.CurrentVersion), "d");
        Assert.Equal(MatchKind.New, r.Kind);

        // 而真的同一條（只換措辭）必須對回同一個 catalog id
        var reworded = AtsScenario.RaceFrame with { Trigger = "另一種說法", Observable = "另一種觀察描述" };
        var hit = assisted.Canonicalize(reworded, catalog.View(catalog.CurrentVersion), "d");
        Assert.Equal(MatchKind.Fuzzy, hit.Kind);
    }

    [RequiresPostgres]
    public void ExperimentHistory_AndConformalSamples_SurviveRestart()
    {
        Db.Truncate(Cs);
        var experiments = new PostgresExperimentCatalog(Cs);
        string key = InMemoryExperimentCatalog.KeyOf("停用 KeepAlive timer，連跑 5000 次循環");
        for (int i = 0; i < 9; i++) experiments.Record(key, "CLM-race", 0, 2);
        experiments.Record(key, "CLM-race", 1, 2);

        var view = new PostgresExperimentCatalog(Cs).View(experiments.CurrentVersion);
        var h = Assert.Single(view.For(key));
        Assert.Equal(10, h.Total);
        Assert.True(h.Frequencies[0] > h.Frequencies[1]);

        var store = new PostgresConformalSampleStore(Cs);
        for (int i = 0; i < 12; i++)
            store.Add(new Modules.Assurance.ConformalSample("diagnosis",
                ImmutableDictionary<string, double>.Empty.Add("H1", 0.8).Add("H2", 0.2), "H1", IsAudit: i % 4 == 0));

        var q = new PostgresConformalSampleStore(Cs).RebuildAll();
        Assert.True(q.ContainsKey("diagnosis"));
        Assert.Equal(9, q["diagnosis"].CalibrationSize);
        Assert.Equal(3, q["diagnosis"].AuditSize);
    }

    [RequiresPostgres]
    public void OversightLog_SurvivesRestart_SoRubberStampingIsDetectableAcrossMonths()
    {
        Db.Truncate(Cs);
        var log = new PostgresOversightLog(Cs);
        for (int i = 0; i < 12; i++)
            log.Record(new HumanDecision($"CASE-{i}", Core.Ports.HumanRole.Approver, "approver", true, 1.5, true));

        var monitor = new AutomationBiasMonitor();
        foreach (var d in new PostgresOversightLog(Cs).All()) monitor.Record(d);

        var policy = monitor.PolicyFor(Core.Ports.HumanRole.Approver);
        Assert.True(policy.Tightened);
        Assert.True(policy.RequireBlindFirstPass);
        Assert.True(policy.RequireSecondApprover);
    }

    private static PolicyDelta Delta(string caseId, params (string Agent, bool Ok)[] rows) =>
        new(caseId,
            rows.Select(r => new WeightObservation(r.Agent, "d", "solver", r.Ok, VerifierLevel.L5_RealOutcome)).ToImmutableArray(),
            ImmutableArray<CatalogEntryCandidate>.Empty,
            ImmutableArray<string>.Empty)
        {
            CalibrationPoints = rows.Select(r => new CalibrationPoint(r.Agent, r.Ok ? 0.9 : 0.8, r.Ok)).ToImmutableArray()
        };
}
