namespace PriorAuthBot.Core.Audit;

public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary><c>Postgres</c> (append-only table) or <c>InMemory</c> (tests and a developer's machine only).</summary>
    public string Store { get; set; } = "InMemory";

    /// <summary>Create the table and triggers at startup. True for local databases; false where a migration owns the schema.</summary>
    public bool CreateSchema { get; set; } = true;

    /// <summary>
    /// Keep the question and the answer in each record. Deductible Dynamics' compliance team wants
    /// them: "what did this user see" cannot be answered from document IDs alone. A team that decides
    /// otherwise turns this off and keeps the IDs, counts, and outcomes.
    /// </summary>
    public bool RecordContent { get; set; } = true;

    /// <summary>The app role (Entra ID app role value) that may read the audit log through the API.</summary>
    public string ReaderRole { get; set; } = "PriorAuthBot.AuditReader";

    /// <summary>
    /// What happens when a record cannot be written. <c>FailClosed</c> (the default) withholds the
    /// response: PriorAuthBot does not show anyone PHI it cannot account for. <c>FailOpen</c> logs and
    /// carries on, for a team that judges availability the bigger risk.
    /// </summary>
    public string OnWriteFailure { get; set; } = FailClosed;

    /// <summary>Chapter 14: a second, immutable copy of the log in Blob Storage, and the chain's anchors.</summary>
    public AuditArchiveOptions Archive { get; set; } = new();

    public const string FailClosed = "FailClosed";
    public const string FailOpen = "FailOpen";
}
