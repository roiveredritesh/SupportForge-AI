using SupportForge.Agents.Verification;

namespace SupportForge.Api.Tests.Agents;

// U13 (JEV intent classification plan, R18): the splitter that gives each draft sentence its own Jev Noul.
public class SentenceSplitterTests
{
    [Fact]
    public void KeepsVersionsAndDecimalsWhole()
    {
        var sentences = SentenceSplitter.Split("Upgrade to v2.1 before the release. The price rose to 3.5 dollars today.", 10);

        Assert.Equal(
            new[] { "Upgrade to v2.1 before the release.", "The price rose to 3.5 dollars today." },
            sentences);
    }

    [Fact]
    public void DoesNotSplitAfterLatinAbbreviations()
    {
        var sentences = SentenceSplitter.Split(
            "Use a tool such as e.g. the built-in exporter for reports. Run it again after the upgrade finishes.",
            10);

        Assert.Equal(
            new[] { "Use a tool such as e.g. the built-in exporter for reports.", "Run it again after the upgrade finishes." },
            sentences);
    }

    [Fact]
    public void DoesNotSplitAfterTitles()
    {
        var sentences = SentenceSplitter.Split("Ask Dr. Lee about the refund policy today.", 10);

        Assert.Equal(new[] { "Ask Dr. Lee about the refund policy today." }, sentences);
    }

    [Fact]
    public void StripsBulletAndNumberedMarkers()
    {
        var text = "- Restart the service after the update\n* Clear the local cache folder\n1. Open the settings page\n2. Click save changes now";

        var sentences = SentenceSplitter.Split(text, 10);

        Assert.Equal(
            new[] { "Restart the service after the update", "Clear the local cache folder", "Open the settings page", "Click save changes now" },
            sentences);
    }

    [Fact]
    public void SplitsOnNewlinesEvenWithoutPunctuation()
    {
        var sentences = SentenceSplitter.Split("The first line has four words\nThe second line has four words", 10);

        Assert.Equal(
            new[] { "The first line has four words", "The second line has four words" },
            sentences);
    }

    [Fact]
    public void DropsSentencesWithFewerThanFourWords()
    {
        var sentences = SentenceSplitter.Split("Yes. Restart the router now. Thanks!", 10);

        Assert.Equal(new[] { "Restart the router now." }, sentences);
    }

    [Fact]
    public void SplitsOnQuestionAndExclamationMarks()
    {
        var sentences = SentenceSplitter.Split("Does the export work on mobile devices? Yes it does, definitely!", 10);

        Assert.Equal(
            new[] { "Does the export work on mobile devices?", "Yes it does, definitely!" },
            sentences);
    }

    [Fact]
    public void DoesNotSplitInsideUrlsOrFileNames()
    {
        var sentences = SentenceSplitter.Split("Read the guide at example.com for the full setup steps.", 10);

        Assert.Equal(new[] { "Read the guide at example.com for the full setup steps." }, sentences);
    }

    [Fact]
    public void CapsTheNumberOfSentencesInOrder()
    {
        var sentences = SentenceSplitter.Split(
            "First sentence has words. Second sentence has words. Third sentence has words. Fourth sentence has words.",
            2);

        Assert.Equal(new[] { "First sentence has words.", "Second sentence has words." }, sentences);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ok. Thanks.")]
    public void EmptyOrFragmentOnlyTextGivesNoSentences(string text)
    {
        Assert.Empty(SentenceSplitter.Split(text, 10));
    }
}
