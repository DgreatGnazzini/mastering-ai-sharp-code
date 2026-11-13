using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Intake;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 4's endpoint, end to end through HTTP with a scripted vision model.</summary>
public sealed class IntakeApiTests
{
    private static readonly string FormPath = Path.Combine(TestPaths.SampleIntake, "intake-form-001.png");

    [Fact]
    public async Task ClaimsStaffGetTypedFieldsAndARoute()
    {
        await using var factory = new Factory();

        var response = await Client(factory, "claims").PostAsync("/intake", Upload(FormPath, "image/png"), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<IntakeResult>(StructuredOutput.SerializerOptions, TestContext.Current.CancellationToken);
        Assert.Equal("DD-5820317", result!.Form.MemberId);
        Assert.Equal(IntakeRoute.ReadyForClinicalReview, result.Route);
    }

    [Fact]
    public async Task OtherStaffCannotSubmitForms()
    {
        await using var factory = new Factory();

        var response = await Client(factory, "nurse").PostAsync("/intake", Upload(FormPath, "image/png"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Vision.Requests);
    }

    [Fact]
    public async Task OnlyImagesAreAccepted()
    {
        await using var factory = new Factory();

        var response = await Client(factory, "claims").PostAsync("/intake", Upload(Path.Combine(TestPaths.SampleFiles, "business-travel-policy.docx"), "application/octet-stream"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    private static HttpClient Client(Factory factory, string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", user);
        return client;
    }

    private static MultipartFormDataContent Upload(string path, string mediaType)
    {
        var file = new ByteArrayContent(File.ReadAllBytes(path));
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new MultipartFormDataContent { { file, "file", Path.GetFileName(path) } };
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        public RecordingChatClient Vision { get; } = new(_ => File.ReadAllText(Path.Combine(TestPaths.SampleIntake, "intake-form-001.expected.json")));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AI:Provider", "Offline");
            builder.UseSetting("VectorStore:Provider", "InMemory");
            builder.UseSetting("Ingestion:DocumentsPath", TestPaths.SampleDocuments);
            builder.UseSetting("Intake:MembersPath", Path.Combine(TestPaths.SampleIntake, "members.json"));
            builder.ConfigureTestServices(services => services.AddKeyedSingleton<IChatClient>(AiServiceCollectionExtensions.VisionKey, Vision));
        }
    }
}
