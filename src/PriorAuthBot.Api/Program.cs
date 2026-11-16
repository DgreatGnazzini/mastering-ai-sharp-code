using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.AI;
using PriorAuthBot.Api;
using PriorAuthBot.Api.Auth;
using PriorAuthBot.Api.Ingestion;
using PriorAuthBot.Core;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Intake;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Search;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPriorAuthBot(builder.Configuration);
builder.Services.AddProblemDetails();

// Ingestion runs in the background and keeps the index in sync with the documents (chapter 7).
builder.Services.AddOptions<IngestionWorkerOptions>().Bind(builder.Configuration.GetSection(IngestionWorkerOptions.SectionName));
builder.Services.AddSingleton<IngestionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionWorker>());

// The policy catalog (chapter 5): about 200 summaries, embedded on first use and searched in memory.
builder.Services.AddOptions<CatalogOptions>().Bind(builder.Configuration.GetSection(CatalogOptions.SectionName));
builder.Services.AddSingleton(sp => new PolicyCatalog(
    sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CatalogOptions>>().Value));

// Intake forms (chapter 4): the member directory the extracted fields are checked against.
builder.Services.AddOptions<IntakeOptions>().Bind(builder.Configuration.GetSection(IntakeOptions.SectionName));
builder.Services.AddSingleton(sp => MemberDirectory.Load(Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntakeOptions>>().Value.MembersPath))));
builder.Services.AddSingleton(sp => new IntakeService(
    sp.GetRequiredService<IntakeFormExtractor>(), sp.GetRequiredService<MemberDirectory>(), PriorAuthBotServiceCollectionExtensions.CreateOcr(sp)));

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

// Chapter 2: a per-user rate limit, so one person (or one runaway script) cannot spend the whole model quota.
builder.Services.AddOptions<UserRateLimitOptions>().Bind(builder.Configuration.GetSection(UserRateLimitOptions.SectionName));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };
    options.AddPolicy(UserRateLimitOptions.PolicyName, context =>
    {
        var limits = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<UserRateLimitOptions>>().Value;
        var user = context.User.FindFirst("oid")?.Value ?? context.User.Identity?.Name ?? "anonymous";
        return RateLimitPartition.GetTokenBucketLimiter(user, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = limits.Burst,
            TokensPerPeriod = limits.RequestsPerMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseGroupOverageHandling();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/", () => PriorAuthBotInfo.Current);

// Chapter 8: a minimal chat page that shows answers with their citations. It signs in with the demo
// personas, so it exists only in Development; production gets a real front end with Entra sign-in.
if (app.Environment.IsDevelopment())
{
    app.MapGet("/ui", (IWebHostEnvironment environment) => Results.File(Path.Combine(environment.ContentRootPath, "ui", "index.html"), "text/html; charset=utf-8"));
}
app.MapGet("/health", () => Results.Ok("healthy"));

var api = app.MapGroup("/").RequireAuthorization().RequireRateLimiting(UserRateLimitOptions.PolicyName);

// Chapter 2: chat straight to the model, streamed as server-sent events. No documents involved.
api.MapPost("/chat", ChatEndpoint.HandleAsync);

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

// Chapter 4: extract a scanned prior authorization form into typed fields, and route it.
// A bearer-token API has no cookies to forge, so the form upload needs no antiforgery token.
api.MapPost("/intake", IntakeEndpoint.HandleAsync).DisableAntiforgery();

// Chapter 5: semantic search over the policy catalog, which every employee may read.
api.MapPost("/catalog/search", async (CatalogSearchRequest request, PolicyCatalog catalog, Microsoft.Extensions.Options.IOptions<CatalogOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 500)
    {
        return Results.Problem("Send a query of 1 to 500 characters.", statusCode: StatusCodes.Status400BadRequest);
    }

    await catalog.EnsureBuiltAsync(Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.Path)), cancellationToken);
    var top = Math.Clamp(request.Top ?? options.Value.Top, 1, 20);
    return Results.Ok(await catalog.SearchAsync(request.Query, request.Mode ?? options.Value.Mode, top, cancellationToken));
});

// Chapters 6 to 8: permission-aware hybrid search, showing what the caller is allowed to retrieve.
api.MapPost("/search", async (SearchRequest request, ClaimsPrincipal user, CallerIdentityFactory identities, SecureRetriever retriever, CancellationToken cancellationToken) =>
{
    var caller = identities.Create(user);
    var results = await retriever.SearchAsync(request.Query, caller, cancellationToken);
    return results.Select(r => new SearchHit(r.Record.DocumentId, r.Record.Title, r.Record.Section, Math.Round(r.Score, 4),
        r.VectorScore is { } v ? Math.Round(v, 3) : null, r.KeywordRank, r.RerankScore is { } s ? Math.Round(s, 3) : null, Citations.Link(r.Record)));
});

// Chapter 8: where a citation link lands. The same group filter as search, and the same 404 whether
// the document is missing or restricted, so a link never confirms that a restricted document exists.
api.MapGet("/documents/{documentId}", async (string documentId, ClaimsPrincipal user, CallerIdentityFactory identities, SecureRetriever retriever, CancellationToken cancellationToken) =>
{
    var document = documentId.Length <= 200 ? await retriever.GetDocumentAsync(documentId, identities.Create(user), cancellationToken) : null;
    return document is null ? Results.NotFound() : Results.Ok(document);
});

// Chapter 7: grounded answers from the documents the caller can see.
api.MapPost("/ask", async (AskRequest request, ClaimsPrincipal user, CallerIdentityFactory identities, RagAnswerService rag, CancellationToken cancellationToken) =>
{
    var caller = identities.Create(user);
    return await rag.AskAsync(request.Question, caller, cancellationToken);
});

await app.RunAsync();

public partial class Program;
