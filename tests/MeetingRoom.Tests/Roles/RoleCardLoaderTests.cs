using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roles;

namespace MeetingRoom.Tests.Roles;

/// <summary>角色卡解析（设计文档 §5.4）：frontmatter 字段 + Markdown 正文。</summary>
public sealed class RoleCardLoaderTests
{
    [Fact]
    public void ParsesFrontmatterAndBody()
    {
        var card = RoleCardLoader.Parse(
            """
            ---
            id: product-manager
            name: 产品经理
            title: 需求与优先级
            summary: 10 年 ToB SaaS 产品经验，关注用户价值与优先级判断
            role: member
            model: deepseek-chat
            effort: high
            allowedTools: [read_material, list_materials, submit_material]
            readOnly: true
            ---

            你是资深产品经理，先给结论再给理由。
            """);

        Assert.Equal("product-manager", card.Id);
        Assert.Equal("产品经理", card.Name);
        Assert.Equal("需求与优先级", card.Title);
        Assert.StartsWith("10 年 ToB SaaS", card.Summary, StringComparison.Ordinal);
        Assert.Equal(ParticipantRole.Member, card.Role);
        Assert.Equal("deepseek-chat", card.Model);
        Assert.Equal("high", card.Effort);
        Assert.Equal(["read_material", "list_materials", "submit_material"], card.AllowedTools);
        Assert.True(card.ReadOnly);
        Assert.Equal("你是资深产品经理，先给结论再给理由。", card.Body);
    }

    [Fact]
    public void RecognizesHostRole()
    {
        var card = RoleCardLoader.Parse("---\nid: host\nname: 主持人\nrole: host\n---\n\n你负责推进会议。");

        Assert.Equal(ParticipantRole.Host, card.Role);
        Assert.False(card.ReadOnly);
        Assert.Empty(card.AllowedTools);
    }

    [Fact]
    public void FallsBackToFileNameForId()
    {
        var card = RoleCardLoader.Parse("---\nname: 销售经理\n---\n\n你关注可销售性。", "sales-manager");

        Assert.Equal("sales-manager", card.Id);
        Assert.Equal("销售经理", card.Name);
    }

    [Fact]
    public void TextWithoutFrontmatterIsAllBody()
    {
        var card = RoleCardLoader.Parse("没有 frontmatter 的角色卡。", "plain");

        Assert.Equal("plain", card.Id);
        Assert.Equal("plain", card.Name);
        Assert.Equal("没有 frontmatter 的角色卡。", card.Body);
    }

    [Fact]
    public void MissingIdWithoutFallbackIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => RoleCardLoader.Parse("---\nname: 无 id\n---\n正文"));
    }

    [Fact]
    public void LoadsWholeDirectoryInStableOrder()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "_roles-tmp");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "b.md"), "---\nid: b\n---\nB");
            File.WriteAllText(Path.Combine(directory, "a.md"), "---\nid: a\n---\nA");

            var cards = RoleCardLoader.LoadDirectory(directory);

            Assert.Equal(["a", "b"], cards.Select(c => c.Id));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
