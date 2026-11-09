using System.Text.Json;
using System.Text.Json.Serialization;

using MeetingRoom.Core.Combat;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// §25 B 组：`visibility` 从裸字符串改成枚举后，
/// 既要让工具 schema 出现 enum，也要让旧字符串（<c>"public"</c> / <c>"secret"</c>）继续能读。
/// </summary>
public sealed class VisibilityEnumTests
{
    private static readonly JsonSerializerOptions CamelCaseEnum = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Theory]
    [InlineData("public", CheckVisibility.Public)]
    [InlineData("secret", CheckVisibility.Secret)]
    [InlineData("Secret", CheckVisibility.Secret)]
    [InlineData("SECRET", CheckVisibility.Secret)]
    public void LegacyStringsStillDeserialize(string text, CheckVisibility expected)
    {
        var parsed = JsonSerializer.Deserialize<CheckVisibility>($"\"{text}\"", CamelCaseEnum);

        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void EnumSerializesAsCamelCase()
    {
        Assert.Equal("\"secret\"", JsonSerializer.Serialize(CheckVisibility.Secret, CamelCaseEnum));
        Assert.Equal("\"public\"", JsonSerializer.Serialize(CheckVisibility.Public, CamelCaseEnum));
    }

    [Fact]
    public void SecretDrivesTheCharacterFacingSummary()
    {
        var secret = new CheckResult
        {
            CheckId = "chk-1",
            ActorId = "ella",
            Kind = CheckKind.Skill,
            Dc = 13,
            Visibility = CheckVisibility.Secret,
            Success = true,
        };

        var open = secret with { Visibility = CheckVisibility.Public };

        Assert.True(secret.IsSecret);
        Assert.Equal("（暗骰）你无法确定结果。", secret.CharacterFacingSummary());
        Assert.False(open.IsSecret);
        Assert.NotEqual("（暗骰）你无法确定结果。", open.CharacterFacingSummary());
    }
}
