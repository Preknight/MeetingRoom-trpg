using MeetingRoom.Core.Vtt;

namespace MeetingRoom.Tests.Vtt;

/// <summary>
/// 距离换算（《枭熊地图接入实现方案》§4）：像素差 ÷ dpi = 格数，格数 × scale.multiplier = 尺；
/// 规则由场景的 <c>measurement</c> 决定，<b>客户端不得覆盖</b>——否则模型可以挑一套更有利的算法。
/// <para>
/// 用的是 5e 的四个口径：chessboard / alternating / euclidean / manhattan。
/// 桥的 Python 实现必须与这里逐条对齐（两端同一组用例）。
/// </para>
/// </summary>
public sealed class DistanceTests
{
    /// <summary>150 dpi、5 尺/格——枭熊的常见设置；下半部分的用例都按它换算。</summary>
    private static readonly VttGrid Grid = new(150, "SQUARE", VttMeasurement.Chessboard, 5, "ft");

    [Theory]
    // 3 × 4 格（像素 450 × 600）：
    [InlineData(VttMeasurement.Chessboard, 4.0)]      // 对角与直走同价
    [InlineData(VttMeasurement.Alternating, 5.0)]     // 4 + floor(3 / 2)
    [InlineData(VttMeasurement.Euclidean, 5.0)]       // sqrt(9 + 16)
    [InlineData(VttMeasurement.Manhattan, 7.0)]       // 3 + 4
    public void EachMeasurementUsesItsOwnFormula(VttMeasurement measurement, double expectedCells)
    {
        Assert.Equal(expectedCells, Distance.GridCells(measurement, deltaX: 3, deltaY: 4), 3);
    }

    [Fact]
    public void PixelsAreConvertedWithTheScenesDpiAndScale()
    {
        var grid = Grid with { Measurement = VttMeasurement.Manhattan };
        var from = new VttPoint(150, 150);
        var to = new VttPoint(600, 750); // 3 格横向、4 格纵向

        // 7 格 × 5 尺 = 35 尺
        Assert.Equal(35, Distance.InFeet(grid, from, to), 3);
        Assert.Equal(35, Distance.InFeet(grid, dxPixels: 450, dyPixels: 600), 3);
    }

    [Fact]
    public void TheScaleMultiplierComesFromTheScene()
    {
        var tenFootSquares = Grid with { Multiplier = 10 };

        // 同一个 4 格，按场景比例就是 40 尺：客户端不许自己写死 5。
        Assert.Equal(40, Distance.InFeet(tenFootSquares, dxPixels: 450, dyPixels: 600), 3);
    }

    [Fact]
    public void AnUnusableGridNeverDividesByZero()
    {
        Assert.Equal(0, Distance.InFeet(VttGrid.Empty, dxPixels: 450, dyPixels: 600));
        Assert.False(VttGrid.Empty.IsUsable);
    }

    [Theory]
    [InlineData("chessboard", VttMeasurement.Chessboard)]
    [InlineData("alternating", VttMeasurement.Alternating)]
    [InlineData("euclidean", VttMeasurement.Euclidean)]
    [InlineData("manhattan", VttMeasurement.Manhattan)]
    [InlineData("CHEBYSHEV", VttMeasurement.Chessboard)]
    [InlineData("", VttMeasurement.Chessboard)]
    [InlineData("没人认识的名字", VttMeasurement.Chessboard)]
    public void TheMeasurementNameIsReadFromTheScene(string raw, VttMeasurement expected)
    {
        Assert.Equal(expected, VttSnapshotParser.ParseMeasurement(raw));
    }

    [Fact]
    public void DistancesAreShownAsWholeFeet()
    {
        Assert.Equal("30 尺", Distance.Describe(30));
        Assert.Equal("5 尺", Distance.Describe(4.5));
        Assert.Equal("0 尺", Distance.Describe(0));
    }
}
