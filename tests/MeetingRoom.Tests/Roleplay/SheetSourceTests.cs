using System.ComponentModel;
using System.Text.Json;

using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Core.Rules;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// §26 Sheet 来源 + §27 指纹含参数 schema：
/// 角色卡旁路表加载与校验、无表时创建最小表、hp 类键被拒、指纹对参数变更敏感而对键顺序不敏感。
/// </summary>
public sealed class SheetSourceTests
{
    [Fact]
    public void RoleSheetLoaderLoadsValidSheetsAndReportsBrokenOnes()
    {
        var result = Load();

        Assert.Contains(result.Sheets, sheet => sheet.CharacterId == "ella");
        Assert.Equal(30, result.Sheets.Single(sheet => sheet.CharacterId == "ella").CurrentHp);
        Assert.Contains(result.Problems, problem => problem.Contains("broken.sheet.json", StringComparison.Ordinal)
            && problem.Contains("maxHp", StringComparison.Ordinal));
    }

    [Fact]
    public void AcAliasIsAccepted()
    {
        var sheet = Load().Sheets.Single(item => item.CharacterId == "alias-ok");

        // 简写 ac / str / profBonus 都被规范化成了 Sheet 的规范名。
        Assert.Equal(14, sheet.ArmorClass);
        Assert.Equal(2, sheet.ProficiencyBonus);
        Assert.Equal(12, sheet.Abilities![Ability.Strength]);
    }

    [Fact]
    public void SameValueAliasesAreAccepted()
    {
        var sheet = Load().Sheets.Single(item => item.CharacterId == "alias-same");

        Assert.Equal(16, sheet.ArmorClass);
    }

    [Fact]
    public void ConflictingAliasesAreRejected()
    {
        var problems = Load().Problems;

        Assert.Contains(problems, problem => problem.Contains("alias-conflict.sheet.json", StringComparison.Ordinal)
            && problem.Contains("值不同", StringComparison.Ordinal));
    }

    [Fact]
    public void LevelIsRejectedWithAnExplanation()
    {
        var problems = Load().Problems;

        Assert.Contains(problems, problem => problem.Contains("alias-level.sheet.json", StringComparison.Ordinal)
            && problem.Contains("characterLevel", StringComparison.Ordinal));
    }

    private static RoleSheetLoadResult Load() =>
        RoleSheetLoader.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "Roles"));

    [Fact]
    public void DmOverrideCreatesMinimalSheetWhenThereIsNone()
    {
        var sheets = new SheetStore();

        var result = DmOverrideEngine.Apply(null, sheets, new DmOverrideRequest
        {
            Target = "ella",
            Field = "hp",
            Value = Json("""{"currentHp": 12, "maxHp": 12, "ac": 13}"""),
            Reason = "导演说她有 12 点血",
        });

        Assert.True(result.Ok, result.Message);
        Assert.True(result.CreatedSheet);
        Assert.Equal(12, sheets.Get("ella")!.MaxHp);
        Assert.Equal(13, sheets.Get("ella")!.ArmorClass);
    }

    [Fact]
    public void DmOverrideRefusesToGuessWhenNoSheetAndNoMinimalValues()
    {
        var result = DmOverrideEngine.Apply(null, new SheetStore(), new DmOverrideRequest
        {
            Target = "ella",
            Field = "hp",
            Value = Json("0"),
        });

        Assert.False(result.Ok);
        Assert.Equal("unknown-sheet", result.Code);
        Assert.Contains("currentHp", result.Message!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hp")]
    [InlineData("maxHp")]
    [InlineData("hpMax")]
    [InlineData("currentHp")]
    [InlineData("tempHp")]
    [InlineData("HP")]
    public void HitPointKeysAreRecognized(string key) => Assert.True(WorldStateStore.IsHitPointKey(key));

    [Theory]
    [InlineData("好感")]
    [InlineData("injury")]
    [InlineData("money")]
    public void NonHitPointKeysPassThrough(string key) => Assert.False(WorldStateStore.IsHitPointKey(key));

    [Fact]
    public void ToolFingerprintSensitiveToParameterName()
    {
        var first = new[] { Tool("probe_tool", (string? alpha) => "schema") };
        var second = new[] { Tool("probe_tool", (string? beta) => "schema") };

        Assert.NotEqual(ContextComposer.ToolFingerprintOf(first), ContextComposer.ToolFingerprintOf(second));
    }

    [Fact]
    public void ToolFingerprintStableForSameDeclaration()
    {
        var first = new[] { Tool("probe_tool", ([Description("a")] string? alpha) => "schema") };
        var second = new[] { Tool("probe_tool", ([Description("a")] string? alpha) => "schema") };

        Assert.Equal(ContextComposer.ToolFingerprintOf(first), ContextComposer.ToolFingerprintOf(second));
    }

    private static AITool Tool(string name, Delegate body) =>
        AIFunctionFactory.Create(body, name, "说明");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
