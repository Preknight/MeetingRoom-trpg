using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roles;

namespace MeetingRoom.Tests.Roles;

/// <summary>
/// 角色卡渲染（设计方案 §15.11）：表单字段 → §5.4 的 frontmatter + 正文，
/// 必须与 <see cref="RoleCardLoader.Parse"/> **往返一致**——否则"填了一张卡，存下去再打开就变了"。
/// </summary>
public sealed class RoleCardWriterTests
{
    [Fact]
    public void RenderThenParseRoundTrips()
    {
        var card = new RoleCard
        {
            Id = "narrator",
            Name = "说书人",
            Title = "控场与描写",
            Summary = "把导演的话转写成正文，控场发言顺序",
            Role = ParticipantRole.Host,
            Model = "deepseek-chat",
            Effort = "high",
            AllowedTools = ["propose_world_state", "nudge_character"],
            VttTokenId = "obr-narrator-7",
            ReadOnly = true,
            Body = "你是这场戏的说书人。\n\n- 只描写环境，不替角色做决定。",
        };

        var markdown = RoleCardWriter.Render(card);
        var parsed = RoleCardLoader.Parse(markdown, fallbackId: "fallback");

        Assert.Equal(card.Id, parsed.Id);
        Assert.Equal(card.Name, parsed.Name);
        Assert.Equal(card.Title, parsed.Title);
        Assert.Equal(card.Summary, parsed.Summary);
        Assert.Equal(ParticipantRole.Host, parsed.Role);
        Assert.Equal(card.Model, parsed.Model);
        Assert.Equal(card.Effort, parsed.Effort);
        Assert.Equal(card.AllowedTools, parsed.AllowedTools);
        Assert.Equal("obr-narrator-7", parsed.VttTokenId);
        Assert.True(parsed.ReadOnly);
        Assert.Equal(card.Body, parsed.Body);
    }

    [Fact]
    public void OptionalFieldsAreOmittedWhenEmpty()
    {
        var markdown = RoleCardWriter.Render(new RoleCard
        {
            Id = "detective",
            Name = "侦探",
            Body = "你是个侦探。",
        });

        Assert.DoesNotContain("model:", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("effort:", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("allowedTools:", markdown, StringComparison.Ordinal);
        // vttTokenId 是可选字段：不填就不写进卡，老卡解析回来仍是空串。
        Assert.DoesNotContain("vttTokenId:", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("readOnly:", markdown, StringComparison.Ordinal);
        Assert.Contains("role: member", markdown, StringComparison.Ordinal);
        Assert.Equal(string.Empty, RoleCardLoader.Parse(markdown).VttTokenId);
    }

    [Fact]
    public void LineBreaksInFieldsAreFoldedToKeepTheFenceIntact()
    {
        // frontmatter 只认单行值：值里的换行折成空格，否则一张卡会被解析成两半。
        var markdown = RoleCardWriter.Render(new RoleCard
        {
            Id = "detective",
            Name = "侦探\n（副标题）",
            Summary = "第一行\n第二行",
            Body = "正文",
        });

        var frontmatter = markdown.Split("---")[1];
        Assert.DoesNotContain("\n（副标题）", frontmatter, StringComparison.Ordinal);

        var parsed = RoleCardLoader.Parse(markdown, "fallback");
        Assert.Equal("侦探 （副标题）", parsed.Name);
        Assert.Equal("第一行 第二行", parsed.Summary);
        Assert.Equal("正文", parsed.Body);
    }
}
