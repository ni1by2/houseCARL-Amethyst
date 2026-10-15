using Xunit;
using Xunit.Abstractions;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>
/// The standing context — the instructions string the server publishes on <c>initialize</c>, which every session is
/// handed whether or not a skill loads. Held on the SERVED string, the way
/// <see cref="PublishedDescriptionBoundTests"/> holds the descriptions <c>tools/list</c> publishes, not on the source
/// literal in <c>Program.cs</c>.
///
/// <para>Claude Code delivers the first 2,048 characters of the instructions and appends "[truncated]", so the
/// string is bounded there; at 3,173 characters the enable and generated-output rules never reached a session.</para>
///
/// <para>The three cross-skill facts folded in are held by their lead phrase and held to ONE occurrence: the point of
/// folding them here was that they are stated once, in the one place a session always sees.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class ServerInstructionsTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public ServerInstructionsTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    [Fact]
    public void TheStandingInstructionsAreNotEmpty()
    {
        var text = _s.PublishedInstructions;

        Assert.False(string.IsNullOrWhiteSpace(text),
            "initialize published no instructions — the standing context is what a session gets before any skill " +
            "loads, so an empty string is a silent loss, not a smaller payload.");
    }

    [Fact]
    public void TheInstructionsFitClaudeCodesCutOf2048Characters()
    {
        var length = _s.PublishedInstructions.Length;
        _out.WriteLine($"ServerInstructions: {length} characters (Claude Code cut {PublishedDescriptionBoundTests.Bound})");

        Assert.True(length <= PublishedDescriptionBoundTests.Bound,
            $"The published instructions are {length} characters; Claude Code delivers the first " +
            $"{PublishedDescriptionBoundTests.Bound} and drops the rest. Shorten them, or move a tool-specific line into that " +
            "tool's description.");
    }

    [Theory]
    [InlineData("RUNTIME DISTRIBUTION LAYERS")]
    [InlineData("NEVER COPY GENERATED OUTPUT")]
    public void BothPlatformVariantsFitAndStateSharedRulesOnce(string lead)
    {
        foreach (var amethyst in new[] { false, true })
        {
            var text = ServerInstructionsText.Build(amethyst);
            Assert.True(text.Length <= PublishedDescriptionBoundTests.Bound,
                $"The {(amethyst ? "Amethyst" : "MO2")} instructions contain {text.Length} characters.");
            Assert.Equal(1, text.Split(lead).Length - 1);
        }
    }

    [Fact]
    public void ServedInstructionsUseTheCurrentPlatformText()
    {
        Assert.Equal(ServerInstructionsText.Build(OperatingSystem.IsLinux()), _s.PublishedInstructions);
    }

    [Fact]
    public void AmethystGuidanceDescribesFreshnessStagingAndConsentAccurately()
    {
        var text = ServerInstructionsText.Build(amethyst: true);

        Assert.Contains("freshness warning", text);
        Assert.Contains("saved profile/generation", text);
        Assert.Contains("new staging mod", text);
        Assert.Contains("per-plugin and per-call consent", text);
        Assert.Contains("may be immediately visible", text);
        Assert.Contains("does not prove what the game has loaded", text);
        Assert.DoesNotContain("not available yet", text);
    }

    [Fact]
    public void LiveMo2GuidanceExplainsNewAndExistingModPlacement()
    {
        var text = ServerInstructionsText.Build(amethyst: false);

        Assert.Contains("A new mod folder loads last", text);
        Assert.Contains("existing-folder write keeps its priority", text);
        Assert.Contains("only when their mod is enabled in MO2", text);
    }
}
