using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.AI;
using PriorAuthBot.Api;
using PriorAuthBot.Api.Auth;
using PriorAuthBot.Api.Ingestion;
using PriorAuthBot.Core;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPriorAuthBot(builder.Configuration);
builder.Services.AddProblemDetails();

// Ingestion runs in the background and keeps the index in sync with the documents (chapter 7).
builder.Services.AddOptions<IngestionWorkerOptions>().Bind(builder.Configuration.GetSection(IngestionWorkerOptions.SectionName));
builder.Services.AddSingleton<IngestionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionWorker>());

// Sign-in. Production uses Entra ID tokens; local development can use demo personas.
var authMode = builder.Configuration["Auth:Mode"] ?? "Entra";
if (authMode == DemoAuthenticationHandler.SchemeName)
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("Auth:Mode 'Demo' is only allowed in the Development environment.");
    }

    builder.Services.AddAuthentication(DemoAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>(DemoAuthenticationHandler.SchemeName, null);
}
else
{
    builder.Services.AddAuthentication().AddJwtBearer(options =>
    {
        builder.Configuration.Bind("Auth:JwtBearer", options);

        // Keep Entra's claim names ("oid", "groups") instead of mapping them to long URIs.
        options.MapInboundClaims = false;
    });
}

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseGroupOverageHandling();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => PriorAuthBotInfo.Current);
app.MapGet("/health", () => Results.Ok("healthy"));

var api = app.MapGroup("/").RequireAuthorization();

// Chapter 2: chat straight to the model, streamed. No documents involved.
api.MapPost("/chat", (ChatRequest request, IChatClient chatClient, CancellationToken cancellationToken) =>
    chatClient.GetStreamingResponseAsync(request.Message, cancellationToken: cancellationToken)
        .Select(update => update.Text));

// Chapter 3: classify a question into a typed intent, and decide where it goes.
api.MapPost("/classify", async (ClassifyRequest request, QuestionClassifier classifier, CancellationToken cancellationToken) =>
{
    try
    {
        var intent = await classifier.ClassifyAsync(request.Question, cancellationToken);
        return Results.Ok(new ClassifyResponse(intent, QuestionRouter.Route(intent)));
    }
    catch (StructuredOutputException)
    {
        // The model's output never reached the caller unvalidated, and neither does the raw reply.
        return Results.Problem("The model did not return a valid classification.", statusCode: StatusCodes.Status502BadGateway);
    }
});

// Chapters 5 and 6: permission-aware search, showing what the caller is allowed to retrieve.
api.MapPost("/search", async (SearchRequest request, ClaimsPrincipal user, CallerIdentityFactory identities, SecureRetriever retriever, CancellationToken cancellationToken) =>
{
    var caller = identities.Create(user);
    var results = await retriever.SearchAsync(request.Query, caller, cancellationToken);
    return results.Select(r => new SearchHit(r.Record.DocumentId, r.Record.Title, r.Record.Section, Math.Round(r.Score, 3)));
});

// Chapter 7: grounded answers from the documents the caller can see.
api.MapPost("/ask", async (AskRequest request, ClaimsPrincipal user, CallerIdentityFactory identities, RagAnswerService rag, CancellationToken cancellationToken) =>
{
    var caller = identities.Create(user);
    return await rag.AskAsync(request.Question, caller, cancellationToken);
});

await app.RunAsync();

public partial class Program;
