using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace PriorAuthBot.Core.Retrieval.Reranking;

/// <summary>
/// A cross-encoder on ONNX Runtime (section 8.4): <c>cross-encoder/ms-marco-MiniLM-L6-v2</c>, a
/// 22-million-parameter BERT model trained on Bing search logs (MS MARCO) to read a query and a
/// passage together and output one relevance score. It runs in-process on the CPU, so the question
/// and the passages never leave the machine, and scoring a dozen passages takes milliseconds.
/// The score is a raw logit: above 0 means the model thinks the passage is relevant.
/// </summary>
public sealed class CrossEncoderReranker : IReranker, IDisposable
{
    /// <summary>The model was trained on 512 tokens at most; passages are cut to fit.</summary>
    private const int MaxTokens = 512;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _needsTokenTypes;

    public CrossEncoderReranker(string modelDirectory)
    {
        var model = Path.Combine(modelDirectory, "model.onnx");
        var vocab = Path.Combine(modelDirectory, "vocab.txt");
        if (!File.Exists(model) || !File.Exists(vocab))
        {
            throw new FileNotFoundException(
                $"The cross-encoder needs model.onnx and vocab.txt in {Path.GetFullPath(modelDirectory)}. Run scripts/get-reranker.sh, or set Retrieval:Rerank:Provider to None.");
        }

        _session = new InferenceSession(model, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        _tokenizer = BertTokenizer.Create(vocab, new BertOptions { LowerCaseBeforeTokenization = true });
        _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public string Name => "Cross-encoder (ms-marco-MiniLM-L6-v2, ONNX)";

    public Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(passages);
        if (passages.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<double>>([]);
        }

        // Each input is [CLS] query [SEP] passage [SEP], with token type 0 for the query and 1 for the passage.
        var queryIds = _tokenizer.EncodeToIds(query, addSpecialTokens: false);
        var inputs = new List<(IReadOnlyList<int> Ids, IReadOnlyList<int> Types)>(passages.Count);
        foreach (var passage in passages)
        {
            var room = Math.Max(1, MaxTokens - queryIds.Count - 3);
            var passageIds = _tokenizer.EncodeToIds(passage, addSpecialTokens: false);
            if (passageIds.Count > room)
            {
                passageIds = passageIds.Take(room).ToList();
            }

            inputs.Add((_tokenizer.BuildInputsWithSpecialTokens(queryIds, passageIds), _tokenizer.CreateTokenTypeIdsFromSequences(queryIds, passageIds)));
        }

        // One batch, padded to the longest input; the attention mask tells the model to ignore the padding.
        var length = inputs.Max(i => i.Ids.Count);
        var dimensions = new[] { inputs.Count, length };
        var ids = new DenseTensor<long>(dimensions);
        var mask = new DenseTensor<long>(dimensions);
        var types = new DenseTensor<long>(dimensions);
        for (var row = 0; row < inputs.Count; row++)
        {
            for (var column = 0; column < inputs[row].Ids.Count; column++)
            {
                ids[row, column] = inputs[row].Ids[column];
                mask[row, column] = 1;
                types[row, column] = inputs[row].Types[column];
            }
        }

        List<NamedOnnxValue> feeds =
        [
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        ];
        if (_needsTokenTypes)
        {
            feeds.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var results = _session.Run(feeds);
        var logits = results[0].AsTensor<float>();
        var scores = new double[inputs.Count];
        for (var row = 0; row < scores.Length; row++)
        {
            scores[row] = logits[row, 0];
        }

        return Task.FromResult<IReadOnlyList<double>>(scores);
    }

    public void Dispose() => _session.Dispose();
}
