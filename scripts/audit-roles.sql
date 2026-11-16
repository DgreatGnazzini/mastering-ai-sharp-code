-- Who may do what with PriorAuthBot's audit log in PostgreSQL (chapter 13). Run once, as an
-- administrator, after src/PriorAuthBot.Core/Audit/audit-log.sql. Role names are examples; in Azure
-- (chapter 14) they are Entra ID principals for the API's managed identity and the compliance group.
--
--   priorauthbot_schema     owns the tables and runs migrations. Nobody logs in as it day to day.
--   priorauthbot_api        the API: appends records and reads them for the audit endpoints.
--   priorauthbot_auditor    the compliance team's read-only access, for reports outside the API.
--   priorauthbot_retention  the scheduled purge job: may delete records past the retention period.
--
-- The trigger in audit-log.sql refuses UPDATE and TRUNCATE for every role, the owner included,
-- and DELETE for any record still inside the retention period. A table owner can disable a
-- trigger, which is why the owner is a separate role that no application or person uses.

CREATE ROLE priorauthbot_schema NOLOGIN;
CREATE ROLE priorauthbot_api LOGIN;
CREATE ROLE priorauthbot_auditor LOGIN;
CREATE ROLE priorauthbot_retention LOGIN;

ALTER TABLE priorauthbot_audit OWNER TO priorauthbot_schema;
ALTER TABLE priorauthbot_audit_retention OWNER TO priorauthbot_schema;
ALTER FUNCTION priorauthbot_audit_guard() OWNER TO priorauthbot_schema;

REVOKE ALL ON priorauthbot_audit, priorauthbot_audit_retention FROM PUBLIC;
GRANT INSERT, SELECT ON priorauthbot_audit TO priorauthbot_api;
GRANT SELECT ON priorauthbot_audit TO priorauthbot_auditor;
GRANT SELECT, DELETE ON priorauthbot_audit TO priorauthbot_retention;
GRANT SELECT ON priorauthbot_audit_retention TO priorauthbot_api, priorauthbot_auditor, priorauthbot_retention;

-- The purge, run by priorauthbot_retention on a schedule. The trigger refuses any row it reaches
-- too early, so a wrong WHERE clause fails instead of deleting recent records.
--   DELETE FROM priorauthbot_audit
--   WHERE recorded_at < now() - make_interval(days => (SELECT retention_days FROM priorauthbot_audit_retention));
