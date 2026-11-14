using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace DeductibleDynamics.MockHr;

/// <summary>PTO balances by employee object ID ("oid"), as the HR system would hold them. Synthetic.</summary>
public sealed record EmployeePto(string Name, double AvailableHours, double AccruedThisYearHours, double UsedThisYearHours, double CarryoverLimitHours);

/// <summary>
/// The mock HR API (chapter 10). The entry point is a named class so the tests can host it next to
/// PriorAuthBot's API, which has its own Program class.
/// </summary>
public sealed class MockHrProgram
{
    /// <summary>The scope a token must carry. It is delegated: a user granted it to PriorAuthBot.</summary>
    public const string PtoReadScope = "Pto.Read";

    private MockHrProgram()
    {
    }

    public static async Task Main(string[] args)
    {
        var app = Build(args);
        await app.RunAsync();
    }

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddProblemDetails();

        var mode = builder.Configuration["Auth:Mode"] ?? "Entra";
        builder.Services.AddAuthentication().AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            if (mode == "Development")
            {
                // Development only: PriorAuthBot's DevelopmentTokenProvider signs tokens with a shared
                // key in place of Entra ID. The app refuses this mode outside Development.
                if (!builder.Environment.IsDevelopment())
                {
                    throw new InvalidOperationException("Auth:Mode 'Development' is only allowed in the Development environment.");
                }

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = builder.Configuration["Auth:Development:Issuer"],
                    ValidAudience = builder.Configuration["Auth:Development:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                        builder.Configuration["Auth:Development:SigningKey"] ?? throw new InvalidOperationException("Auth:Development:SigningKey is not set."))),
                };
            }
            else
            {
                builder.Configuration.Bind("Auth:JwtBearer", options);
            }
        });

        // A delegated token for a user: it names the user (oid) and carries the scope the user
        // consented to (scp). An app-only token from the client credentials flow has neither, so
        // PriorAuthBot's own identity can never read anyone's balance.
        builder.Services.AddAuthorizationBuilder().AddPolicy("pto-read", policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("oid")
            .RequireAssertion(context => context.User.FindAll("scp")
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(PtoReadScope, StringComparer.Ordinal)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        var employees = JsonSerializer.Deserialize<Dictionary<string, EmployeePto>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "employees.json")), JsonSerializerOptions.Web) ?? [];

        app.MapGet("/health", () => Results.Ok("healthy"));

        // The only data endpoint. It has no employee ID parameter: the employee is whoever the token names.
        app.MapGet("/me/pto-balance", (HttpContext context) =>
        {
            var oid = context.User.FindFirst("oid")!.Value;
            return employees.TryGetValue(oid, out var e)
                ? Results.Ok(new
                {
                    employeeName = e.Name,
                    availableHours = e.AvailableHours,
                    accruedThisYearHours = e.AccruedThisYearHours,
                    usedThisYearHours = e.UsedThisYearHours,
                    carryoverLimitHours = e.CarryoverLimitHours,
                    asOf = DateOnly.FromDateTime(DateTime.UtcNow),
                })
                : Results.NotFound();
        }).RequireAuthorization("pto-read");

        return app;
    }
}
