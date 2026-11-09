using MeetingRoom.Core.Config;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Vtt;

namespace MeetingRoom.Tests.Vtt;

/// <summary>
/// 地图段落的文本化（《枭熊地图接入实现方案》§3 V1）：<b>隐藏 token 一律不出现</b>、
/// <b>未登记 token 不给名字</b>、距离只按场景的网格规则算。这三条是"别剧透、别编数"的全部落点。
/// </summary>
public sealed class VttContextTests
{
    /// <summary>150 dpi、5 尺/格：横向 3 格 = 15 尺，6 格 = 30 尺，15 格 = 75 尺。</summary>
    private static readonly VttGrid Grid = new(150, "SQUARE", VttMeasurement.Chessboard, 5, "ft");

    private static readonly WorldState World = new(1)
    {
        Characters = new Dictionary<string, CharacterState>(StringComparer.Ordinal)
        {
            ["ella"] = new("ella", "艾拉"),
            ["ran"] = new("ran", "冉"),
        },
    };

    [Fact]
    public void HiddenTokensNeverReachACharacter()
    {
        var context = NewContext(NewSnapshot());

        var text = context.ForCharacter("ella");

        Assert.Contains("冉：15 尺", text, StringComparison.Ordinal);
        // t5「潜伏者」是隐藏的（距艾拉 5 尺）：一个字都不能漏，距离也不能露。
        Assert.DoesNotContain("潜伏者", text, StringComparison.Ordinal);
        Assert.DoesNotContain("：5 尺", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenTokensAreStillShownWhenTheDmOptsIn()
    {
        var context = NewContext(NewSnapshot(), new VttOptions { Enabled = true, IncludeHidden = true });

        var text = context.ForCharacter("ella");

        // 隐藏项本来不该出现；明确允许之后，它至少会以"未登记目标 + 5 尺"的形式出现
        // （角色视角仍然不给名字，那是另一条规则）。
        Assert.Contains($"{VttContext.UnregisteredTarget}：5 尺", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnregisteredTokensAreMaskedToAGenericDescription()
    {
        var context = NewContext(NewSnapshot());

        var text = context.ForCharacter("ella");

        // 名字（「地精」）不给，只给泛称与距离——那是 DM 还没介绍的对手。
        Assert.DoesNotContain("地精", text, StringComparison.Ordinal);
        Assert.Contains($"{VttContext.UnregisteredTarget}：30 尺", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredTokensAreNamedAndDistancesFollowTheSceneRule()
    {
        var context = NewContext(NewSnapshot());

        var text = context.ForCharacter("ella");

        Assert.Contains("[地图]", text, StringComparison.Ordinal);
        Assert.Contains("5 ft/格（chessboard", text, StringComparison.Ordinal);
        Assert.Contains("冉：15 尺", text, StringComparison.Ordinal);
        Assert.Contains("标尺读数：20 ft", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetsOutsideTheInjectionRadiusAreLeftOut()
    {
        // 潜伏者之外还有一个 75 尺外的目标：默认半径 60 尺，它不该出现（连泛称都不该出现）。
        var snapshot = NewSnapshot() with
        {
            Tokens =
            [
                new VttToken("t1", "艾拉", 150, 150, true),
                new VttToken("t4", "远处的人", 2400, 150, true), // 15 格 = 75 尺
            ],
            Rulers = [],
        };

        var text = NewContext(snapshot).ForCharacter("ella");

        Assert.DoesNotContain(VttContext.UnregisteredTarget, text, StringComparison.Ordinal);
        Assert.Contains("附近没有别的目标", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACharacterWithoutATokenIsToldSoInsteadOfGuessedDistances()
    {
        var snapshot = NewSnapshot() with
        {
            Tokens = [new VttToken("t2", "地精", 450, 150, true)],
            Rulers = [],
        };

        var text = NewContext(snapshot).ForCharacter("ella");

        // 没有锚点就什么都不给：宁可说"无从判断"，也不要给一个错的数字。
        Assert.Contains("图上没有名为「艾拉」的棋子", text, StringComparison.Ordinal);
        Assert.DoesNotContain("尺", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNarratorSeesTheWholeMapButStillRespectsTheHiddenSwitch()
    {
        var text = NewContext(NewSnapshot()).ForNarrator();

        Assert.Contains("[地图] 场景已就绪", text, StringComparison.Ordinal);
        Assert.Contains("艾拉", text, StringComparison.Ordinal);
        Assert.Contains("地精", text, StringComparison.Ordinal);
        Assert.Contains("坐标 (150, 150)", text, StringComparison.Ordinal);
        Assert.Contains("当前选中：t1", text, StringComparison.Ordinal);
        Assert.Contains("标尺读数：20 ft", text, StringComparison.Ordinal);
        // 隐藏项对说书人也不出现（它写进正文就等于剧透），除非 includeHidden。
        Assert.DoesNotContain("潜伏者", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNarratorCanLookAroundWithAWiderRadius()
    {
        var snapshot = NewSnapshot() with
        {
            Tokens =
            [
                new VttToken("t1", "艾拉", 150, 150, true),
                new VttToken("t2", "地精", 2400, 150, true), // 75 尺
            ],
            Rulers = [],
        };

        var context = NewContext(snapshot);

        Assert.DoesNotContain("地精", context.ForCharacter("ella"), StringComparison.Ordinal);
        Assert.Contains("地精：75 尺", context.LookAround("ella", radiusFeet: 90), StringComparison.Ordinal);
    }

    [Fact]
    public void LookAroundAlsoAcceptsATokenNameAsTheCentre()
    {
        // 说书人未必记得角色 id：直接点 token 的名字（"冉"）也该能用。
        var text = NewContext(NewSnapshot()).LookAround("冉", radiusFeet: 60);

        Assert.Contains("以「冉」为中心", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LookAroundAcceptsTheCharacterNameAsTheCentre()
    {
        // 真机踩过的那条：模型传的是角色**名**（"艾拉"），不是 id（"ella"）。
        var text = NewContext(NewSnapshot()).LookAround("艾拉", radiusFeet: 60);

        Assert.Contains("以「艾拉」为中心", text, StringComparison.Ordinal);
        Assert.Contains("冉：15 尺", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTokenIdWinsEvenWhenTheCallerPassesTheCharacterName()
    {
        // 真机那条的完整形态：调用方传角色名，图上还有一个同名诱饵 token，
        // vttTokenId 仍要按角色 id 命中正确的那个。
        var snapshot = NewSnapshot() with
        {
            Tokens =
            [
                new VttToken("obr-ella", "Ella (old)", 150, 150, true),
                new VttToken("t2", "冉", 600, 150, true),
                new VttToken("decoy", "艾拉", 1500, 150, true),
            ],
            Rulers = [],
        };

        var text = NewContext(
            snapshot,
            tokenIds: new Dictionary<string, string>(StringComparer.Ordinal) { ["ella"] = "obr-ella" })
            .LookAround("艾拉", radiusFeet: 90);

        Assert.Contains("冉：15 尺", text, StringComparison.Ordinal);
        Assert.DoesNotContain("冉：30 尺", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HasAnchorMatchesIdsNamesAndTokenIds()
    {
        var context = NewContext(
            NewSnapshot(),
            tokenIds: new Dictionary<string, string>(StringComparer.Ordinal) { ["ella"] = "tok-ella" });

        Assert.True(context.HasAnchor("ella"));   // 角色 id
        Assert.True(context.HasAnchor("艾拉"));   // 角色名
        Assert.True(context.HasAnchor("冉"));     // token 名
        Assert.False(context.HasAnchor("nobody"));
        Assert.False(context.HasAnchor(string.Empty));
    }

    [Fact]
    public void AnUnavailableSnapshotProducesNoSectionAtAll()
    {
        var context = NewContext(VttSnapshot.Unavailable("桥没启动"));

        Assert.False(context.HasMap);
        Assert.Empty(context.ForCharacter("ella"));
        Assert.Empty(context.ForNarrator());
    }

    // ——— vttTokenId：角色卡 → OBR token 的稳定映射（退化链的顺序即语义） ———

    [Fact]
    public void TheExplicitTokenIdWinsOverATokenWithTheSameName()
    {
        // 图上两个「艾拉」：obr-ella 在 x=150（冉在 15 尺外），另一个同名 token 在更远处（30 尺外）。
        // 名字匹配会撞上错的那个；vttTokenId 必须赢。
        var snapshot = NewSnapshot() with
        {
            Tokens =
            [
                new VttToken("obr-ella", "艾拉", 150, 150, true),
                new VttToken("t2", "冉", 600, 150, true),
                new VttToken("decoy", "艾拉", 1500, 150, true),
            ],
            Rulers = [],
        };

        var text = NewContext(
            snapshot,
            tokenIds: new Dictionary<string, string>(StringComparer.Ordinal) { ["ella"] = "obr-ella" })
            .ForCharacter("ella");

        Assert.Contains("冉：15 尺", text, StringComparison.Ordinal);
        Assert.DoesNotContain("冉：30 尺", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedTokenIdFallsBackToTheNameMatch()
    {
        // 角色卡写的 token 已经不在图上（DM 换了场景）：退回名字匹配，而不是让整段地图消失。
        var snapshot = NewSnapshot() with
        {
            Tokens =
            [
                new VttToken("t1", "艾拉", 150, 150, true),
                new VttToken("t2", "冉", 600, 150, true),
            ],
            Rulers = [],
        };

        var text = NewContext(
            snapshot,
            tokenIds: new Dictionary<string, string>(StringComparer.Ordinal) { ["ella"] = "已经不在图上的 id" })
            .ForCharacter("ella");

        Assert.Contains("冉：15 尺", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoAnchorAtAllTheHintTextIsUnchanged()
    {
        var snapshot = NewSnapshot() with
        {
            Tokens = [new VttToken("t2", "冉", 600, 150, true)],
            Rulers = [],
        };

        var text = NewContext(snapshot).ForCharacter("ella");

        // 文本原样钉住：改口径必须同时改这条测试，免得悄悄变成"给你个大概距离"。
        Assert.Equal(
            "[地图] 图上没有名为「艾拉」的棋子，位置与距离无从判断——不要自己估算。",
            text);
    }

    [Fact]
    public void AnUnsetTokenIdKeepsTheOldBehaviour()
    {
        var snapshot = NewSnapshot();

        var withoutMapping = NewContext(snapshot).ForCharacter("ella");
        var withEmptyMapping = NewContext(
            snapshot,
            tokenIds: new Dictionary<string, string>(StringComparer.Ordinal))
            .ForCharacter("ella");

        // 老数据没有 vttTokenId：两种构造必须给出同一个结果，行为不变。
        Assert.Equal(withoutMapping, withEmptyMapping);
        Assert.Contains("冉：15 尺", withoutMapping, StringComparison.Ordinal);
    }

    private static VttContext NewContext(
        VttSnapshot snapshot,
        VttOptions? options = null,
        IReadOnlyDictionary<string, string>? tokenIds = null) =>
        new(snapshot, World, options ?? new VttOptions { Enabled = true }, tokenIds);

    /// <summary>
    /// 三个 token：艾拉（已登记、实心）、冉（已登记、3 格远）、地精（未登记、6 格远）、潜伏者（隐藏）。
    /// </summary>
    private static VttSnapshot NewSnapshot() => new()
    {
        Available = true,
        Schema = "obr-bridge-state/1",
        ReceivedAt = 1793003931203,
        SceneReady = true,
        Grid = Grid,
        Tokens =
        [
            new VttToken("t1", "艾拉", 150, 150, true),
            new VttToken("t2", "冉", 600, 150, true),
            new VttToken("t3", "地精", 1050, 150, true),
            new VttToken("t5", "潜伏者", 300, 150, false),
        ],
        Selected = ["t1"],
        Rulers = [new VttRuler("r1", "20 ft", new VttPoint(150, 150), new VttPoint(450, 300))],
    };
}
