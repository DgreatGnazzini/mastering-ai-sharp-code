namespace PriorAuthBot.Tests.Deployment;

/// <summary>
/// The settings infra/modules/app.bicep gives the container app, as configuration keys, with the
/// offline model so a test needs no Azure. A test starts from these and breaks one thing.
/// </summary>
internal static class ProductionSettings
{
    public const string Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0";

    public static Dictionary<string, string?> Valid() => new()
    {
        ["Auth:Mode"] = "Entra",
        ["Auth:JwtBearer:Authority"] = Authority,
        ["Auth:JwtBearer:Audience"] = "api://priorauthbot",
        ["Auth:RequiredRole"] = "PriorAuthBot.User",
        ["AI:Provider"] = "Offline",
        ["VectorStore:Provider"] = "InMemory",
        ["Azure:ManagedIdentityClientId"] = "11111111-1111-1111-1111-111111111111",
        ["Azure:TenantId"] = "00000000-0000-0000-0000-000000000001",
        ["Postgres:Authentication"] = "Entra",
        ["ConnectionStrings:priorauthbot"] = "Host=psql-dd-priorauthbot.postgres.database.azure.com;Database=priorauthbot;Username=id-priorauthbot-api;Ssl Mode=Require",
        ["Audit:Store"] = "Postgres",
        ["Audit:CreateSchema"] = "false",
        ["Usage:Ledger"] = "Postgres",
        ["Usage:CreateSchema"] = "false",
        ["Tools:Downstream:ClientId"] = "22222222-2222-2222-2222-222222222222",
    };
}
