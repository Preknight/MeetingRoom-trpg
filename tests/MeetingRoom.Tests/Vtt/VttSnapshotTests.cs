using System.Text.Json;
using MeetingRoom.Core.Vtt;

namespace MeetingRoom.Tests.Vtt;

/// <summary>
/// 快照解析（《枭熊地图接入实现方案》§4 的数据契约）：桥给的是
/// <c>{schema, receivedAt, scene:{sceneReady, grid:{dpi, type, measurement, scale:{raw, parsed}}, tokens, selected, rulers}}</c>。
/// <para>解析宽容：缺字段补安全默认值；缺 dpi / 缺比例 / 场景未就绪 → 整份不可用（那种数据算出的距离一定是错的）。</para>
/// </summary>
public sealed class VttSnapshotTests
{
    private const string Contract = """
    {
      "schema": "obr-bridge-state/1",
      "receivedAt": 1793003931203,
      "scene": {
        "sceneReady": true,
        "grid": { "dpi": 150, "type": "SQUARE", "measurement": "chessboard",
                  "scale": { "raw": "5 ft", "parsed": { "multiplier": 5, "unit": "ft" } } },
        "tokens": [
          { "id": "t1", "name": "艾拉", "x": 150, "y": 150, "visible": true },
          { "id": "t2", "name": "地精", "x": 450, "y": 150, "visible": false }
        ],
        "selected": ["t1"],
        "rulers": [ { "id": "r1", "measurement": "20 ft",
                      "start": { "x": 150, "y": 150 }, "end": { "x": 450, "y": 300 } } ]
      }
    }
    """;

    [Fact]
    public void TheContractIsReadIntoTheSnapshot()
    {
        var snapshot = VttSnapshotParser.Parse(Contract);

        Assert.True(snapshot.Available);
        Assert.Equal("obr-bridge-state/1", snapshot.Schema);
        Assert.Equal(1793003931203, snapshot.ReceivedAt);
        Assert.True(snapshot.SceneReady);
        Assert.Equal(150, snapshot.Grid.Dpi);
        Assert.Equal(VttMeasurement.Chessboard, snapshot.Grid.Measurement);
        Assert.Equal(5, snapshot.Grid.Multiplier);
        Assert.Equal("ft", snapshot.Grid.Unit);
        Assert.Equal(["t1"], snapshot.Selected);

        var goblin = Assert.Single(snapshot.Tokens, token => token.Id == "t2");
        Assert.False(goblin.Visible); // 桥的显示开关就是"已揭示 / 未揭示"
        Assert.Equal("20 ft", Assert.Single(snapshot.Rulers).Measurement);
    }

    [Fact]
    public void ASnapshotWithoutAReadySceneIsUnavailable()
    {
        var snapshot = VttSnapshotParser.Parse(Contract.Replace("\"sceneReady\": true", "\"sceneReady\": false"));

        Assert.False(snapshot.Available);
        Assert.Contains("sceneReady", snapshot.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AGridWithoutDpiOrScaleIsUnavailable()
    {
        var noDpi = VttSnapshotParser.Parse(Contract.Replace("\"dpi\": 150", "\"dpi\": 0"));

        Assert.False(noDpi.Available);
        Assert.Contains("网格", noDpi.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaleFallsBackToTheRawTextWhenParsedIsMissing()
    {
        // 桥的旧版本只给 raw（"5 ft"）：自己拆一次，别把整份地图判成不可用。
        var json = Contract.Replace("\"scale\": { \"raw\": \"5 ft\", \"parsed\": { \"multiplier\": 5, \"unit\": \"ft\" } }",
            "\"scale\": { \"raw\": \"10 ft\" }");

        var snapshot = VttSnapshotParser.Parse(json);

        Assert.True(snapshot.Available);
        Assert.Equal(10, snapshot.Grid.Multiplier);
        Assert.Equal("ft", snapshot.Grid.Unit);
    }

    [Fact]
    public void TokensWithoutVisibleAreTreatedAsShown()
    {
        var json = Contract
            .Replace(", \"visible\": true", string.Empty)
            .Replace(", \"visible\": false", string.Empty);

        var snapshot = VttSnapshotParser.Parse(json);

        Assert.All(snapshot.Tokens, token => Assert.True(token.Visible));
    }

    [Fact]
    public void BrokenJsonIsReportedAsAJsonException()
    {
        Assert.Throws<JsonException>(() => VttSnapshotParser.Parse("{ \"scene\": "));
        Assert.Throws<JsonException>(() => VttSnapshotParser.Parse("""{ "schema": "x" }"""));
    }

    [Fact]
    public void TheUnavailableSnapshotCarriesAReasonButNoTokens()
    {
        var snapshot = VttSnapshot.Unavailable("桥没启动");

        Assert.False(snapshot.Available);
        Assert.Equal("桥没启动", snapshot.UnavailableReason);
        Assert.Empty(snapshot.Tokens);
        Assert.Null(snapshot.ReceivedAtTime);
    }
}
