using System.Text.Json;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Intake;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 4: intake form extraction, validation, and routing, with a scripted model.</summary>
public sealed class IntakeTests
{
    private static readonly MemberDirectory Members = MemberDirectory.Load(Path.Combine(TestPaths.SampleIntake, "members.json"));

    private static string ExpectedJson => File.ReadAllText(Path.Combine(TestPaths.SampleIntake, "intake-form-001.expected.json"));

    private static IntakeForm Expected => JsonSerializer.Deserialize<IntakeForm>(ExpectedJson, StructuredOutput.SerializerOptions)!;

    [Theory]
    [InlineData("1457392060", true)]
    [InlineData("1234567893", true)]
    [InlineData("1457392061", false)]
    [InlineData("1475392060", false)]
    [InlineData("145739206", false)]
    [InlineData("14573920AB", false)]
    public void NpiCheckDigit(string npi, bool valid) => Assert.Equal(valid, Npi.IsValid(npi));

    [Fact]
    public void TheSampleFormIsReadyForClinicalReview()
    {
        var result = IntakeTriage.Decide(Expected, Members);

        Assert.Equal(IntakeRoute.ReadyForClinicalReview, result.Route);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData("memberId", "DD-5820371", "MemberId does not match any member.")]
    [InlineData("dateOfBirth", "1978-09-23", "DateOfBirth does not match the member on file.")]
    [InlineData("orderingProviderNpi", "1457392066", "OrderingProviderNpi fails its check digit.")]
    [InlineData("cptCode", null, "CptCode could not be read.")]
    public void OneWrongFieldSendsTheFormToAPerson(string field, string? value, string problem)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(ExpectedJson)!.AsObject();
        json[field] = value;
        var form = json.Deserialize<IntakeForm>(StructuredOutput.SerializerOptions)!;

        var result = IntakeTriage.Decide(form, Members);

        Assert.Equal(IntakeRoute.NeedsDataEntryReview, result.Route);
        Assert.Equal([problem], result.Problems);
    }

    [Fact]
    public void LowOcrConfidenceSendsTheFormToAPerson()
    {
        var result = IntakeTriage.Decide(Expected, Members, ocrConfidence: 0.72);

        Assert.Equal(IntakeRoute.NeedsDataEntryReview, result.Route);
    }

    [Theory]
    [InlineData("""{"memberId":"5820317"}""", "MemberId must be DD- followed by seven digits.")]
    [InlineData("""{"icd10Code":"M5416X"}""", "Icd10Code is not a valid ICD-10-CM code.")]
    [InlineData("""{"dateOfBirth":"2097-01-01"}""", "DateOfBirth must be after 1900 and not in the future.")]
    public void BadShapesFailValidation(string patch, string error)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(ExpectedJson)!.AsObject();
        foreach (var (key, value) in System.Text.Json.Nodes.JsonNode.Parse(patch)!.AsObject())
        {
            json[key] = value?.DeepClone();
        }

        var response = new ChatResponse<IntakeForm>(new ChatResponse(new ChatMessage(ChatRole.Assistant, json.ToJsonString())), StructuredOutput.SerializerOptions);

        Assert.Equal([error], StructuredOutput.Check(response, out _));
    }

    [Fact]
    public async Task ExtractorSendsTheImageAsDataContent()
    {
        var model = new RecordingChatClient(_ => ExpectedJson);
        var image = BinaryData.FromBytes(await File.ReadAllBytesAsync(Path.Combine(TestPaths.SampleIntake, "intake-form-001.png"), TestContext.Current.CancellationToken));

        var form = await new IntakeFormExtractor(model).ExtractFromImageAsync(image, "image/png", TestContext.Current.CancellationToken);

        Assert.Equal(Expected, form);
        var user = model.Requests.Single()[^1];
        var data = Assert.Single(user.Contents.OfType<DataContent>());
        Assert.Equal("image/png", data.MediaType);
        Assert.Equal(0, model.Options.Single()!.Temperature);
    }

    [Fact]
    public async Task ExtractorRetriesOnceWithTheValidationError()
    {
        var bad = ExpectedJson.Replace("DD-5820317", "5820317", StringComparison.Ordinal);
        var replies = new Queue<string>([bad, ExpectedJson]);
        var model = new RecordingChatClient(_ => replies.Dequeue());

        var form = await new IntakeFormExtractor(model).ExtractFromTextAsync("Member ID: DD-5820317", TestContext.Current.CancellationToken);

        Assert.Equal("DD-5820317", form.MemberId);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("MemberId must be DD-", model.Requests[1][^1].Text, StringComparison.Ordinal);
    }
}
