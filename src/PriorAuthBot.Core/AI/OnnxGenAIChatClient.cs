using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;
using PriorAuthBot.Core.LocalModels;

namespace PriorAuthBot.Core.AI;

/// <summary>
/// Chapter 15: a chat model that runs inside PriorAuthBot's own process, with ONNX Runtime GenAI.
/// No server, no port, no network: the weights are files the process loads after checking them
/// against models.lock.json, and a question never leaves the machine.
/// </summary>
/// <remarks>
/// <para>
/// The package has its own <c>OnnxRuntimeGenAIChatClient</c>. PriorAuthBot uses this one instead for
/// three things the pipeline above it needs: the prompt is built with the model's own chat template
/// (<see cref="Tokenizer.ApplyChatTemplate"/>), so no prompt format is hand-written per model; the
/// response reports token usage, which chapter 13's metering and the audit log record; and the
/// client names its model in <see cref="ChatClientMetadata"/>, which the response cache and the
/// audit log key on.
/// </para>
/// <para>
/// Generation is CPU-bound and each generator holds its own KV cache, so
/// <see cref="OnnxGenAIOptions.MaxConcurrency"/> answers run at once and the rest wait.
/// </para>
/// </remarks>
public sealed class OnnxGenAIChatClient : IChatClient
{
    public const string ProviderName = "onnxruntime-genai";

    private readonly Model _model;
    private readonly Tokenizer _tokenizer;
    private readonly SemaphoreSlim _gate;
    private readonly OnnxGenAIOptions _options;
    private readonly ChatClientMetadata _metadata;
    private int _disposed;

    /// <summary>Loads the model at <see cref="OnnxGenAIOptions.ModelPath"/>, after checking its files when <paramref name="lockedModel"/> is given.</summary>
    public OnnxGenAIChatClient(OnnxGenAIOptions options, LockedModel? lockedModel)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = options.ModelPath is { Length: > 0 } p ? p : throw new InvalidOperationException(
            "AI:OnnxGenAI:ModelPath is not set. Download a model with scripts/get-model.py and point the setting at its folder.");
        if (options.VerifyHashes)
        {
            ModelIntegrity.EnsureVerified(
                lockedModel ?? throw new ModelIntegrityException($"{options.ModelId} is not in models.lock.json, so PriorAuthBot will not load it."),
                path);
        }

        _options = options;
        _model = new Model(path);
        _tokenizer = new Tokenizer(_model);
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
        _metadata = new ChatClientMetadata(ProviderName, null, options.ModelId);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var prompt = BuildPrompt(messages, options);
        var responseId = Guid.NewGuid().ToString("N");
        var stops = options?.StopSequences ?? [];

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var sequences = _tokenizer.Encode(prompt);
            var inputTokens = sequences[0].Length;
            using var parameters = new GeneratorParams(_model);
            var maxOutput = options?.MaxOutputTokens ?? _options.MaxOutputTokens;
            parameters.SetSearchOption("max_length", inputTokens + maxOutput);
            if (options?.Temperature is > 0 and var temperature)
            {
                parameters.SetSearchOption("do_sample", true);
                parameters.SetSearchOption("temperature", temperature);
                if (options.TopP is { } topP)
                {
                    parameters.SetSearchOption("top_p", topP);
                }
            }
            else
            {
                // Temperature 0, or none given: greedy decoding, the same answer every time.
                parameters.SetSearchOption("do_sample", false);
            }

            using var generator = new Generator(_model, parameters);
            using var stream = _tokenizer.CreateStream();
            generator.AppendTokenSequences(sequences);

            var outputTokens = 0;
            var text = new StringBuilder();
            var finish = ChatFinishReason.Length;
            while (!generator.IsDone())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // One forward pass: milliseconds on a small model, a second on a large one. Off the
                // request thread, so a slow token does not hold up the server's other requests.
                await Task.Run(generator.GenerateNextToken, cancellationToken).ConfigureAwait(false);
                var token = generator.GetNextTokens()[0];
                outputTokens++;
                var piece = stream.Decode(token);
                if (piece.Length == 0)
                {
                    continue;
                }

                text.Append(piece);
                if (stops.FirstOrDefault(s => s.Length > 0 && text.ToString().EndsWith(s, StringComparison.Ordinal)) is { } stop)
                {
                    finish = ChatFinishReason.Stop;
                    var trimmed = piece.Length > stop.Length ? piece[..^stop.Length] : string.Empty;
                    if (trimmed.Length > 0)
                    {
                        yield return Update(trimmed);
                    }

                    break;
                }

                yield return Update(piece);
            }

            if (finish != ChatFinishReason.Stop && generator.IsDone() && outputTokens < maxOutput)
            {
                // The model produced its end-of-turn token before running out of room.
                finish = ChatFinishReason.Stop;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens,
            })])
            {
                ResponseId = responseId,
                ModelId = _options.ModelId,
                FinishReason = finish,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            _gate.Release();
        }

        ChatResponseUpdate Update(string piece) => new(ChatRole.Assistant, piece)
        {
            ResponseId = responseId,
            ModelId = _options.ModelId,
        };
    }

    /// <summary>
    /// The prompt, in the model's own format. Messages go to the tokenizer's chat template as the
    /// same JSON a Hugging Face template reads; the template adds the model's role markers and the
    /// generation prompt. <see cref="ChatOptions.Instructions"/> becomes a system message.
    /// </summary>
    internal string BuildPrompt(IEnumerable<ChatMessage> messages, ChatOptions? options) =>
        _tokenizer.ApplyChatTemplate(null, ToTemplateJson(messages, options), null, true);

    /// <summary>The messages as the chat template's input. Text only: this client reads no images.</summary>
    internal static string ToTemplateJson(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = new List<Dictionary<string, string>>();
        if (options?.Instructions is { Length: > 0 } instructions)
        {
            list.Add(new() { ["role"] = "system", ["content"] = instructions });
        }

        foreach (var message in messages)
        {
            if (message.Contents.Any(c => c is DataContent or UriContent))
            {
                throw new NotSupportedException("The ONNX Runtime GenAI chat client reads text only; images go to the vision model.");
            }

            var role = message.Role == ChatRole.System ? "system"
                : message.Role == ChatRole.Assistant ? "assistant"
                : message.Role == ChatRole.Tool ? "tool"
                : "user";
            list.Add(new() { ["role"] = role, ["content"] = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text)) });
        }

        return JsonSerializer.Serialize(list);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is not null ? null
            : serviceType == typeof(ChatClientMetadata) ? _metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <summary>Safe to call more than once: every keyed pipeline that shares this client disposes it at shutdown.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _tokenizer.Dispose();
        _model.Dispose();
        _gate.Dispose();
    }
}
