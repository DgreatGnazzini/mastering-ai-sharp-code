using System.ClientModel;
using System.ClientModel.Primitives;

namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>Lets tests build the <see cref="ClientResultException"/> the OpenAI SDK throws, with a status and headers.</summary>
internal sealed class FakePipelineResponse(int status, string body = "", params (string Name, string Value)[] headers) : PipelineResponse
{
    private Stream? _content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));

    public static ClientResultException Exception(int status, string body = "", params (string Name, string Value)[] headers) =>
        new($"HTTP {status} {body}", new FakePipelineResponse(status, body, headers));

    public override int Status => status;

    public override string ReasonPhrase => status.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override Stream? ContentStream { get => _content; set => _content = value; }

    public override BinaryData Content => BinaryData.FromString(body);

    protected override PipelineResponseHeaders HeadersCore { get; } = new FakeHeaders(headers);

    public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Content);

    public override void Dispose() => _content?.Dispose();

    private sealed class FakeHeaders((string Name, string Value)[] headers) : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            headers.Select(h => KeyValuePair.Create(h.Name, h.Value)).GetEnumerator();

        public override bool TryGetValue(string name, out string? value)
        {
            value = headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
            return value is not null;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = headers.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
            return values.Any();
        }
    }
}
