using MeetingRoom.Core.Participants;

namespace MeetingRoom.Core.Roles;

/// <summary>
/// 内置的默认角色卡：<c>data/roles/</c> 为空时写入磁盘，让工具开箱即可用——之后用户可以随意改。
/// 产品只保留叙事模式，所以默认卡组是**说书人 + 一个示例角色**。
/// </summary>
public static class DefaultRoles
{
    public static IReadOnlyList<RoleCard> Parse() =>
    [
        RoleCardLoader.Parse(NarratorCard),
        RoleCardLoader.Parse(CompanionCard),
    ];

    /// <summary>写出默认角色卡，返回写出的文件路径。</summary>
    public static IReadOnlyList<string> WriteTo(string rolesDirectory)
    {
        Directory.CreateDirectory(rolesDirectory);
        var written = new List<string>();

        foreach (var card in Parse())
        {
            var path = Path.Combine(rolesDirectory, $"{card.Id}.md");
            if (File.Exists(path))
            {
                continue;
            }

            File.WriteAllText(path, Render(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            written.Add(path);
        }

        return written;
    }

    /// <summary>渲染成角色卡格式：与表单录入共用同一个渲染器，免得两处格式慢慢分叉。</summary>
    private static string Render(RoleCard card) => RoleCardWriter.Render(card);

    private const string NarratorCard = """
        ---
        id: narrator
        name: 说书人
        title: 本场的说书人
        summary: 只写环境与调度、不出角色台词；推进时间与场面，在导演没给新指令时自主推一拍
        role: host
        ---

        你是本场的**说书人**：替导演（用户）把场面写出来，但你不扮演任何角色。

        - 只写环境、时间推进、在场人物的动作与气氛；**不替任何角色写台词**，也不替导演做决定。
        - 需要结算时才提交世界状态变更（时间、地点、进场离场、属性、持有物、已知信息）。
        - 场面拖沓或卡住时点名让角色接话；导演没给新指令时可以自己推一拍。
        - 收束时写一段收束并留一句建议，然后把决定权交回导演。
        """;

    private const string CompanionCard = """
        ---
        id: companion
        name: 同伴
        title: 旅伴
        summary: 谨慎、务实，习惯先确认风险再行动
        role: member
        ---

        你是队伍里的一名同伴。

        - 只写你自己的言行与判断，不替别人下结论，也不代替导演推进剧情。
        - 遇到不确定的事先观察、试探，再决定要不要行动。
        - 会主动指出风险，也会在别人卡住时提出一个具体可行的下一步。
        """;
}
