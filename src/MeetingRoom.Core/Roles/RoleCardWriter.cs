using System.Text;
using MeetingRoom.Core.Participants;

namespace MeetingRoom.Core.Roles;

/// <summary>
/// 角色卡的渲染（设计文档 §5.4）：把字段写成 <c>---</c> 包裹的 frontmatter + 正文。
/// <para>
/// 与 <see cref="RoleCardLoader"/> 严格对称——"表单填 → 程序渲染 → 落盘 → 再解析回来"必须得到同一张卡。
/// 这是"不让用户手写 frontmatter"的前提（设计方案 §15.11）：格式只在这里定义一处，
/// <see cref="DefaultRoles"/> 也用它，免得两处各写一份、慢慢分叉。
/// </para>
/// </summary>
public static class RoleCardWriter
{
    public static string Render(RoleCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var builder = new StringBuilder();

        builder.Append("---\n");
        builder.Append("id: ").Append(Line(card.Id)).Append('\n');
        builder.Append("name: ").Append(Line(card.Name)).Append('\n');
        builder.Append("title: ").Append(Line(card.Title)).Append('\n');
        builder.Append("summary: ").Append(Line(card.Summary)).Append('\n');
        builder.Append("role: ").Append(card.Role == ParticipantRole.Host ? "host" : "member").Append('\n');

        if (!string.IsNullOrWhiteSpace(card.Model))
        {
            builder.Append("model: ").Append(Line(card.Model)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(card.Effort))
        {
            builder.Append("effort: ").Append(Line(card.Effort)).Append('\n');
        }

        if (card.AllowedTools.Count > 0)
        {
            builder.Append("allowedTools: [").Append(string.Join(", ", card.AllowedTools.Select(Line))).Append("]\n");
        }

        if (!string.IsNullOrWhiteSpace(card.VttTokenId))
        {
            builder.Append("vttTokenId: ").Append(Line(card.VttTokenId)).Append('\n');
        }

        if (card.ReadOnly)
        {
            builder.Append("readOnly: true\n");
        }

        builder.Append("---\n\n");
        builder.Append(card.Body.Trim()).Append('\n');

        return builder.ToString();
    }

    /// <summary>frontmatter 只认单行值：值里的换行折成空格，否则一张卡会被解析成两半。</summary>
    private static string Line(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.ReplaceLineEndings(" ").Trim();
}
