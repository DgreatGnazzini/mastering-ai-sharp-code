using System.Security.Claims;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Intake;

namespace PriorAuthBot.Api;

public sealed class IntakeOptions
{
    public const string SectionName = "Intake";

    /// <summary>The member directory (chapter 4), relative to the content root unless absolute.</summary>
    public string MembersPath { get; set; } = "../../samples/intake/members.json";

    /// <summary>The group allowed to submit forms. Intake forms are full of PHI.</summary>
    public string AllowedGroup { get; set; } = "Claims";

    public long MaxBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>POST /intake (chapter 4): a scanned prior authorization form in, typed fields and a route out.</summary>
public static class IntakeEndpoint
{
    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "image/png",
        ["image/jpeg"] = "image/jpeg",
    };

    public static async Task<IResult> HandleAsync(
        IFormFile file,
        ClaimsPrincipal user,
        CallerIdentityFactory identities,
        IntakeService intake,
        Microsoft.Extensions.Options.IOptions<IntakeOptions> options,
        CancellationToken cancellationToken)
    {
        // The group check comes from the validated token, like every other permission in PriorAuthBot.
        var caller = identities.Create(user);
        if (!caller.Groups.Contains(options.Value.AllowedGroup, StringComparer.Ordinal))
        {
            return Results.Problem($"Only members of {options.Value.AllowedGroup} can submit intake forms.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (!MediaTypes.TryGetValue(file.ContentType, out var mediaType))
        {
            return Results.Problem("Upload a PNG or JPEG image of the form.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        if (file.Length > options.Value.MaxBytes)
        {
            return Results.Problem($"The file is larger than {options.Value.MaxBytes / (1024 * 1024)} MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        await using var stream = file.OpenReadStream();
        var image = await BinaryData.FromStreamAsync(stream, cancellationToken);
        try
        {
            return Results.Ok(await intake.ProcessAsync(image, mediaType, cancellationToken));
        }
        catch (StructuredOutputException)
        {
            return Results.Problem("The model did not return valid form fields. Route the form to data entry.", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (PhiNotAllowedException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
