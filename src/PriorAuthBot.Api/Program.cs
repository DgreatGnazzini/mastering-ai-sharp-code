using PriorAuthBot.Core;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => PriorAuthBotInfo.Current);
app.MapGet("/health", () => Results.Ok("healthy"));

await app.RunAsync();
