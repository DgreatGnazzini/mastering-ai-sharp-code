// Token and cost calculator (chapter 1). Counts the tokens in a prompt with the o200k_base
// encoding and estimates what a workload would cost per request and per month on each model.
//
//   dotnet run --project tools/PriorAuthBot.TokenCost -- samples/documents/pto-policy.md
//   dotnet run --project tools/PriorAuthBot.TokenCost -- --text "Can I carry PTO over?" --output-tokens 150 --requests-per-day 4000
//   dotnet run --project tools/PriorAuthBot.TokenCost -- --text "Member DD-4471902" --show-tokens
using System.Globalization;
using System.Text.Json;
using PriorAuthBot.Core.AI;

var options = TokenCostOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("usage: PriorAuthBot.TokenCost (<file> | --text \"...\") [--output-tokens N] [--requests-per-day N] [--prices prices.json] [--show-tokens]");
    return 1;
}

var text = options.Text ?? await File.ReadAllTextAsync(options.File!);
var counter = new TokenCounter();
var inputTokens = counter.Count(text);

using var pricesFile = File.OpenRead(options.PricesPath);
var prices = JsonSerializer.Deserialize<PriceSheet>(pricesFile, PriceSheet.JsonOptions)!;

var culture = CultureInfo.GetCultureInfo("en-US");
if (options.ShowTokens)
{
    // Each token in brackets, so you can see where the tokenizer splits words, numbers, and IDs.
    var tokens = Microsoft.ML.Tokenizers.TiktokenTokenizer.CreateForEncoding("o200k_base").EncodeToTokens(text, out _);
    Console.WriteLine(string.Concat(tokens.Select(t => $"[{t.Value}]")));
    Console.WriteLine();
}

Console.WriteLine(string.Create(culture, $"Input: {inputTokens:N0} tokens ({text.Length:N0} characters, {(double)text.Length / Math.Max(inputTokens, 1):0.0} characters per token)"));
Console.WriteLine(string.Create(culture, $"Output: {options.OutputTokens:N0} tokens per request (assumed). Requests: {options.RequestsPerDay:N0} per day."));
Console.WriteLine();
Console.WriteLine($"{"Model",-30} {"Per request",14} {"Per month",14}");
foreach (var price in prices.Models)
{
    var estimate = CostModel.Estimate(price, inputTokens, options.OutputTokens, options.RequestsPerDay);
    Console.WriteLine(string.Create(culture, $"{price.Model,-30} {estimate.PerRequest,14:C6} {estimate.PerMonth,14:C2}"));
}

Console.WriteLine();
Console.WriteLine(prices.Note);
return 0;

internal sealed record PriceSheet(string Note, List<ModelPrice> Models)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed record TokenCostOptions(string? File, string? Text, int OutputTokens, int RequestsPerDay, string PricesPath, bool ShowTokens)
{
    public static TokenCostOptions? Parse(string[] args)
    {
        string? file = null, text = null;
        var showTokens = false;
        var outputTokens = 300;
        var requestsPerDay = 4000;
        var prices = Path.Combine(AppContext.BaseDirectory, "prices.json");
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--text" when i + 1 < args.Length: text = args[++i]; break;
                case "--output-tokens" when i + 1 < args.Length: outputTokens = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--requests-per-day" when i + 1 < args.Length: requestsPerDay = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--prices" when i + 1 < args.Length: prices = args[++i]; break;
                case "--show-tokens": showTokens = true; break;
                default: file = args[i]; break;
            }
        }

        return (file is null) == (text is null) ? null : new TokenCostOptions(file, text, outputTokens, requestsPerDay, prices, showTokens);
    }
}
