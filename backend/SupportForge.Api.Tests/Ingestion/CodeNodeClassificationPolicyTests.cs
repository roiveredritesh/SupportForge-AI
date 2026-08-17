using SupportForge.Ingestion.Code;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class CodeNodeClassificationPolicyTests
{
    [Theory]
    [InlineData("handwritten", 0.0)]
    [InlineData("handwritten", 1.0)]
    [InlineData("test", 0.5)]
    [InlineData("config", 0.9)]
    [InlineData("unknown", 0.0)]
    [InlineData("", 0.0)]
    public void Include_DefaultsToTrue_ForAnyKindOtherThanVendoredOrGenerated(string kind, double confidence)
    {
        Assert.True(CodeNodeClassificationPolicy.Include(kind, confidence, []));
    }

    [Fact]
    public void Include_ExcludesVendored_AtOrAboveThreshold()
    {
        Assert.False(CodeNodeClassificationPolicy.Include("vendored", 0.8, []));
        Assert.False(CodeNodeClassificationPolicy.Include("vendored", 0.95, []));
    }

    [Fact]
    public void Include_KeepsVendored_BelowThreshold()
    {
        Assert.True(CodeNodeClassificationPolicy.Include("vendored", 0.79, []));
        Assert.True(CodeNodeClassificationPolicy.Include("vendored", 0.0, []));
    }

    [Fact]
    public void Include_ExcludesGenerated_OnlyWhenBothConfidenceAndTier0MarkAgree()
    {
        Assert.False(CodeNodeClassificationPolicy.Include("generated", 0.9, ["Generated"]));
    }

    [Fact]
    public void Include_KeepsGenerated_WhenConfidenceHighButNoTier0Mark()
    {
        Assert.True(CodeNodeClassificationPolicy.Include("generated", 0.95, []));
    }

    [Fact]
    public void Include_KeepsGenerated_WhenTier0MarkPresentButConfidenceLow()
    {
        Assert.True(CodeNodeClassificationPolicy.Include("generated", 0.5, ["Generated"]));
    }
}
