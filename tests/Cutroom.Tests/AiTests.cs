using Cutroom.AI;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.Tests;

sealed class FakeProvider(params string[] answers) : IAiProvider
{
    int next;
    public List<string> Prompts { get; } = [];
    public string Name => "Fake";
    public bool Installed => true;
    public Task<string> Ask(string prompt, string schema, CancellationToken ct)
    {
        Prompts.Add(prompt);
        return Task.FromResult(answers[Math.Min(next++, answers.Length - 1)]);
    }
}

public class AiTests
{
    public AiTests() => Tools.CacheRoot = Directory.CreateTempSubdirectory("cutroom-ai").FullName;

    [Fact]
    public async Task CardDraftsRetryOnceWhenShapeIsWrong()
    {
        var fake = new FakeProvider("""{"condensed":""}""", """{"condensed":"Sleep cancels ults","keyPoints":"Cancels ults\nOne cooldown","exactWords":"Sleep cancels almost every ultimate"}""");
        var ai = new AiService("claude", name => name == "claude" ? fake : null);
        var drafts = await ai.DraftCardText("Sleep cancels almost every ultimate.", "", ["Ana"], CancellationToken.None);
        Assert.Equal("Sleep cancels ults", drafts.Condensed);
        Assert.Equal(2, fake.Prompts.Count);
        Assert.Contains("previous answer was invalid", fake.Prompts[1]);
        // Cached: no new call.
        await ai.DraftCardText("Sleep cancels almost every ultimate.", "", ["Ana"], CancellationToken.None);
        Assert.Equal(2, fake.Prompts.Count);
    }

    [Fact]
    public async Task VisualPlanCoversSlots()
    {
        var slots = new List<Slot> { new() { Id = "a", Text = "Sleep is strong." }, new() { Id = "b", Text = "Here is why." } };
        var fake = new FakeProvider("""{"suggestions":[{"slotId":"a","kind":"clip","clipDescription":"Ana sleeps Rein","cardText":null,"reason":"shows it"},{"slotId":"b","kind":"textCard","clipDescription":null,"cardText":"Why\nIt cancels ults","reason":"explain"}]}""");
        var ai = new AiService("claude", name => name == "claude" ? fake : null);
        var plan = await ai.PlanVisuals(slots, CancellationToken.None);
        Assert.Equal(2, plan.Count);
        Assert.Equal("Ana sleeps Rein", plan[0].ClipDescription);
    }

    [Fact]
    public void NoProviderMeansDisabled()
    {
        var ai = new AiService("claude", _ => null);
        Assert.False(ai.Enabled);
    }

    [Fact]
    public async Task ApiKeysNeverReachTheChildProcess()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-should-not-leak");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-should-not-leak");
        try
        {
            var env = await Tools.Run("cmd.exe", ["/c", "set"], env: CliProvider.SafeEnvironment());
            Assert.DoesNotContain("sk-should-not-leak", env);
            Assert.Contains("PATH", env, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void ExtractsJsonFromProse()
    {
        Assert.Equal("""{"a":{"b":"}"}}""", JsonText.FirstObject("""Sure! ```json {"a":{"b":"}"}} ``` done"""));
        Assert.Null(JsonText.FirstObject("no json here"));
    }

    [Fact]
    public void FixedBlockIsLinesSharedByMostDescriptions()
    {
        var block = AiService.FixedBlock([
            "This video covers sleep.\n\nTwitter: x.com/me\nDiscord: d.gg/me\nThanks for watching!",
            "Another topic entirely.\nTwitter: x.com/me\nDiscord: d.gg/me\nThanks for watching!",
            "Third.\nTwitter: x.com/me\nThanks for watching!",
        ]);
        Assert.Equal("Twitter: x.com/me\nDiscord: d.gg/me\nThanks for watching!", block);
        var full = AiService.AssembleDescription("Summary.", "0:00 Intro\n1:00 Why", block);
        Assert.StartsWith("Summary.\n\n0:00 Intro", full);
        Assert.EndsWith("Thanks for watching!", full);
    }
}

/// <summary>One real call through each installed CLI (uses the subscription). Run manually with --filter Category=Live.</summary>
[Trait("Category", "Live")]
public class AiLiveTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task ProviderReturnsSchemaJson(string name)
    {
        IAiProvider provider = name == "claude" ? new ClaudeProvider() : new CodexProvider();
        if (!provider.Installed) return;
        Tools.CacheRoot = Directory.CreateTempSubdirectory("cutroom-ai-live").FullName;
        var ai = new AiService(name, n => n == name ? provider : null);
        var drafts = await ai.DraftCardText("Sleep dart puts an enemy to sleep and cancels almost every ultimate in the game.", "", ["Ana", "Sleep Dart"], CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(drafts.KeyPoints));
    }
}
