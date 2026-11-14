using PriorAuthBot.Core.Search;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 8's building blocks: the keyword tokenizer, BM25, and reciprocal rank fusion.</summary>
public sealed class KeywordSearchTests
{
    [Fact]
    public void CodesAndPolicyNumbersStayWhole()
    {
        var terms = KeywordTokenizer.Tokenize("Is CPT 72148 covered under HR-104? See E0601.");

        Assert.Contains("72148", terms);
        Assert.Contains("e0601", terms);
        Assert.Contains("hr-104", terms);
        Assert.Contains("104", terms);
        Assert.DoesNotContain("is", terms);
    }

    [Theory]
    [InlineData("screens", "screen")]
    [InlineData("escalated", "escalat")]
    [InlineData("policies", "policy")]
    [InlineData("72148", "72148")]
    [InlineData("status", "status")]
    public void StemsOnlyTheCommonEndings(string word, string expected) =>
        Assert.Equal(expected, KeywordTokenizer.Stem(word));

    [Fact]
    public void Bm25RanksTheRareTermFirst()
    {
        var index = new Bm25Index<string>();
        index.Add("mri", "Prior authorization is required for MRI of the lumbar spine (CPT 72148).");
        index.Add("knee", "Prior authorization is required for total knee replacement (CPT 27447).");
        index.Add("pto", "Paid time off accrues each pay period.");

        var hits = index.Search("prior authorization 72148", 3);

        Assert.Equal("mri", hits[0].Item);
        Assert.Equal(2, hits.Count);
        Assert.DoesNotContain(hits, h => h.Item == "pto");
    }

    [Fact]
    public void Bm25ReturnsNothingForStopWordsOnly() =>
        Assert.Empty(Index().Search("what is the", 5));

    [Fact]
    public void FusionRewardsAgreementBetweenLists()
    {
        IReadOnlyList<IReadOnlyList<string>> rankings =
        [
            ["a", "b", "c"],
            ["b", "c", "d"],
        ];

        var fused = ReciprocalRankFusion.Fuse(rankings, x => x);

        // c is only third and second (1/63 + 1/62), and still beats a, which is first in one list (1/61).
        Assert.Equal(["b", "c", "a", "d"], fused.Select(f => f.Item));
        Assert.Equal((1.0 / 62) + (1.0 / 61), fused[0].Score, 6);
        Assert.Equal([1, null], fused.Single(f => f.Item == "a").Ranks);
    }

    [Fact]
    public void FusionCountsAnItemOncePerList()
    {
        var fused = ReciprocalRankFusion.Fuse<string, string>([["a", "a", "b"]], x => x);

        Assert.Equal(1.0 / 61, fused.Single(f => f.Item == "a").Score, 6);
    }

    private static Bm25Index<string> Index()
    {
        var index = new Bm25Index<string>();
        index.Add("x", "What is the policy");
        return index;
    }
}
