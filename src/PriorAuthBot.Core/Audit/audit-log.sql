-- PriorAuthBot's audit log in PostgreSQL (chapter 13). Append-only by construction:
--   * UPDATE is refused by a trigger, always.
--   * DELETE is refused by the same trigger for any record still inside the retention period, and
--     the API's database role is never granted DELETE at all (see scripts/audit-roles.sql).
--   * TRUNCATE is refused by a statement trigger.
-- The retention period lives in its own one-row table that only the compliance role can change.
-- Deductible Dynamics' compliance team set six years (2,190 days), HIPAA's retention period for
-- required documentation. The hash chain in each row makes any change visible even to a reader.

CREATE TABLE IF NOT EXISTS priorauthbot_audit (
    sequence      bigint      PRIMARY KEY,
    audit_id      text        NOT NULL UNIQUE,
    recorded_at   timestamptz NOT NULL,
    user_id       text        NOT NULL,
    operation     text        NOT NULL,
    record        text        NOT NULL,  -- the record exactly as hashed, so the bytes never change
    previous_hash text        NOT NULL,
    hash          text        NOT NULL
);

CREATE INDEX IF NOT EXISTS priorauthbot_audit_user_time ON priorauthbot_audit (user_id, recorded_at);

CREATE TABLE IF NOT EXISTS priorauthbot_audit_retention (
    only_row       boolean PRIMARY KEY DEFAULT true CHECK (only_row),
    retention_days integer NOT NULL CHECK (retention_days >= 365)
);

INSERT INTO priorauthbot_audit_retention (retention_days) VALUES (2190) ON CONFLICT DO NOTHING;

CREATE OR REPLACE FUNCTION priorauthbot_audit_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION 'priorauthbot_audit is append-only: it cannot be truncated';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'priorauthbot_audit is append-only: records cannot be changed';
    END IF;
    IF TG_OP = 'DELETE' AND OLD.recorded_at >= now() - make_interval(days => (SELECT retention_days FROM priorauthbot_audit_retention)) THEN
        RAISE EXCEPTION 'priorauthbot_audit is append-only: record % is inside the retention period', OLD.sequence;
    END IF;
    RETURN OLD;
END;
$$;

DROP TRIGGER IF EXISTS priorauthbot_audit_append_only ON priorauthbot_audit;
CREATE TRIGGER priorauthbot_audit_append_only
    BEFORE UPDATE OR DELETE ON priorauthbot_audit
    FOR EACH ROW EXECUTE FUNCTION priorauthbot_audit_guard();

DROP TRIGGER IF EXISTS priorauthbot_audit_no_truncate ON priorauthbot_audit;
CREATE TRIGGER priorauthbot_audit_no_truncate
    BEFORE TRUNCATE ON priorauthbot_audit
    FOR EACH STATEMENT EXECUTE FUNCTION priorauthbot_audit_guard();
