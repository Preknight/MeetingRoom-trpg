using MeetingRoom.Core.Context;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Roleplay;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 工具面与前缀（§25.5）：工具名集合、说明非空、说明不含易变内容、前缀指纹稳定、
/// 非说书人调用被拒。
/// </summary>
public sealed class ToolSurfaceTests
{
    [Fact]
    public void DeclaredToolNamesMatchTheRegisteredSurface()
    {
        var registered = NarratorToolDispatcher.BuildTools().Select(tool => tool.Name).ToArray();

        Assert.Equal(Narrator.ToolNames.OrderBy(name => name, StringComparer.Ordinal),
            registered.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Contains("dm_override", registered);
        Assert.Equal(13, registered.Length);
    }

    [Fact]
    public void EveryToolHasANonEmptyDescription()
    {
        foreach (var tool in NarratorToolDispatcher.BuildTools())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} 没有说明");
        }
    }

    [Fact]
    public void DescriptionsAndPromptCarryNoVolatileContent()
    {
        var prompt = Narrator.BuildSystemPrompt("示例世界", "示例情境", "示例快照");
        var texts = NarratorToolDispatcher.BuildTools().Select(tool => tool.Description ?? string.Empty)
            .Append(prompt)
            .ToArray();

        foreach (var text in texts)
        {
            // 日期、UUID、以及常见的会话标识形态都不该出现在前缀里。
            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", text);
            Assert.DoesNotMatch(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}", text);
            Assert.DoesNotContain("session-", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PrefixFingerprintIsStableAcrossCalls()
    {
        var tools = NarratorToolDispatcher.BuildTools();
        var prompt = Narrator.BuildSystemPrompt("示例世界", "示例情境", string.Empty);

        var first = ContextComposer.PrefixFingerprintOf(prompt, string.Empty, tools);
        var second = ContextComposer.PrefixFingerprintOf(prompt, string.Empty, tools);
        var toolFirst = ContextComposer.ToolFingerprintOf(tools);
        var toolSecond = ContextComposer.ToolFingerprintOf(NarratorToolDispatcher.BuildTools());

        Assert.Equal(first, second);
        Assert.Equal(toolFirst, toolSecond);
        Assert.Equal(64, first.Length);   // SHA-256 十六进制
    }

    [Fact]
    public async Task NonNarratorCallsAreRejected()
    {
        var dispatcher = new NarratorToolDispatcher(new StubNarrator(), _ => false, table: null);
        var call = new FunctionCallContent(
            "call-1",
            Narrator.DmOverride,
            new Dictionary<string, object?>
            {
                ["target"] = "goblin-1",
                ["field"] = "hp",
                ["value"] = 0,
                ["reason"] = "导演说它倒下了",
            });

        var result = await dispatcher.InvokeAsync("npc-1", call, CancellationToken.None);

        Assert.Contains("只有说书人可以调用", result.Result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NarratorCallWithoutReasonIsRejected()
    {
        var dispatcher = new NarratorToolDispatcher(new StubNarrator(), _ => true, table: null);
        var call = new FunctionCallContent(
            "call-2",
            Narrator.DmOverride,
            new Dictionary<string, object?> { ["target"] = "goblin-1", ["field"] = "hp" });

        var result = await dispatcher.InvokeAsync("narrator", call, CancellationToken.None);

        Assert.Contains("必须带 reason", result.Result?.ToString(), StringComparison.Ordinal);
    }

    private sealed class StubNarrator : INarratorActions
    {
        public Task<NarratorActionOutcome> ProposeRevealFogAsync(
            IReadOnlyList<MeetingRoom.Core.Maps.GridCell>? cells,
            MeetingRoom.Core.Maps.FogShape? shape,
            string reason,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NarratorActionOutcome(true, "（stub）迷雾提议"));

        public Task<NarratorActionOutcome> ProposeWorldStateAsync(
            WorldStateMutation mutation,
            string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public NarratorActionOutcome NudgeCharacter(string characterId, string task) => new(true, "ok");

        public NarratorActionOutcome IdleBeat(string? hint) => new(true, "ok");

        public NarratorActionOutcome DeclareSceneEnd(string reason) => new(true, "ok");

        public Task<NarratorActionOutcome> AskUserAsync(
            IReadOnlyList<InteractionQuestion> questions,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
