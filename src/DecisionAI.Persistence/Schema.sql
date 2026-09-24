-- ============================================================================
--  DecisionAI 持久化 schema（Phase 3）—— Postgres 16 + pgvector
--
--  三個原則，跟記憶體實作完全一致（所以呼叫端零改動）：
--   1) append-only：policy 存的是 delta 不是快照，catalog entry 的語意不可就地修改。
--      就地改一條 entry 的意思，會讓所有引用過它的歷史似然默默失效。
--   2) 版本化：case 全程只讀自己 pin 的版本，因此查詢一律帶 version 條件。
--   3) 事件流是真相：case_event 可以重建任何 case 的完整狀態，其餘都是投影。
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS vector;

-- ── 事件流（重放的唯一真相）──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS case_event (
    case_id     text        NOT NULL,
    seq         int         NOT NULL,
    at          timestamptz NOT NULL,
    step_id     text        NOT NULL,
    actor_id    text        NOT NULL,
    actor_role  text        NOT NULL,
    kind        text        NOT NULL,
    payload     jsonb       NOT NULL,
    PRIMARY KEY (case_id, seq)
);
CREATE INDEX IF NOT EXISTS case_event_kind_idx ON case_event (kind);

-- ── PolicySnapshot：存 delta，快照用摺疊重建 ─────────────────────────────
--  存快照會有一個很實際的問題：欄位一加，舊快照就讀不回來了。
--  存 delta 則是「發生過什麼」，加欄位不會讓歷史失效。
CREATE TABLE IF NOT EXISTS policy_delta (
    version     bigint      PRIMARY KEY,
    case_id     text        NOT NULL,
    at          timestamptz NOT NULL DEFAULT now(),
    payload     jsonb       NOT NULL
);

-- ── ClaimCatalog：append-only，帶嵌入向量供近似檢索 ──────────────────────
--  embedding 只用來縮小候選；是不是同一個故障仍由四元組比對決定。
CREATE TABLE IF NOT EXISTS claim_catalog (
    catalog_id          text        PRIMARY KEY,
    domain              text        NOT NULL,
    mechanism           text        NOT NULL,
    locus               text        NOT NULL,
    trigger_            text        NOT NULL,
    observable          text        NOT NULL,
    added_at_version    bigint      NOT NULL,
    confirmed_true_count int        NOT NULL DEFAULT 0,
    embedding           vector(256)
);
CREATE INDEX IF NOT EXISTS claim_catalog_domain_idx  ON claim_catalog (domain, added_at_version);
CREATE INDEX IF NOT EXISTS claim_catalog_vec_idx     ON claim_catalog USING hnsw (embedding vector_cosine_ops);

-- ── 實驗歷史頻率：似然「數出來」而不是「估出來」的來源 ──────────────────
CREATE TABLE IF NOT EXISTS experiment_outcome (
    experiment_key      text    NOT NULL,
    claim_catalog_id    text    NOT NULL,
    outcome_index       int     NOT NULL,
    outcome_count       int     NOT NULL,
    n                   int     NOT NULL DEFAULT 0,
    PRIMARY KEY (experiment_key, claim_catalog_id, outcome_index)
);

-- ── conformal 樣本：校準集與稽核集必須分開，且不可混用 ───────────────────
CREATE TABLE IF NOT EXISTS conformal_sample (
    id           bigserial PRIMARY KEY,
    task_family  text      NOT NULL,
    true_claim   text      NOT NULL,
    beliefs      jsonb     NOT NULL,
    is_audit     boolean   NOT NULL,
    at           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS conformal_family_idx ON conformal_sample (task_family, is_audit);

-- ── 人類決策稽核（automation bias 監控的資料來源）───────────────────────
CREATE TABLE IF NOT EXISTS human_decision (
    id           bigserial PRIMARY KEY,
    case_id      text      NOT NULL,
    role         text      NOT NULL,
    approver     text      NOT NULL,
    approved     boolean   NOT NULL,
    seconds      double precision,
    saw_recommendation_first boolean NOT NULL,
    at           timestamptz NOT NULL DEFAULT now()
);
