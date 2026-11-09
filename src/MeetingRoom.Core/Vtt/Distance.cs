using System.Globalization;

namespace MeetingRoom.Core.Vtt;

/// <summary>
/// 距离换算（《枭熊地图接入实现方案》§4）：<b>像素差 ÷ dpi = 格数；格数 × scale.multiplier = 尺</b>。
/// <para>
/// 规则由场景的 <c>measurement</c> 决定，<b>客户端不得覆盖</b>——否则模型可以自己挑一套更有利的算法。
/// C# 侧的四个公式必须与桥的 Python 实现逐条对齐（两端用同一组用例对照测试）。
/// </para>
/// <para>
/// 用的是 5e 的常见口径：对角在 chessboard 下与直走同价，在 alternating 下每两个对角格算三格。
/// </para>
/// </summary>
public static class Distance
{
    /// <summary>两个像素点之间的格数（先按 dpi 折算成格，再套规则）。</summary>
    public static double InCells(VttGrid grid, double dxPixels, double dyPixels)
    {
        ArgumentNullException.ThrowIfNull(grid);

        return grid.Dpi <= 0
            ? 0
            : GridCells(grid.Measurement, dxPixels / grid.Dpi, dyPixels / grid.Dpi);
    }

    /// <summary>两个像素点之间的尺数。</summary>
    public static double InFeet(VttGrid grid, double dxPixels, double dyPixels)
    {
        ArgumentNullException.ThrowIfNull(grid);

        return InCells(grid, dxPixels, dyPixels) * Math.Max(0, grid.Multiplier);
    }

    /// <summary>两个 token 之间的尺数。</summary>
    public static double InFeet(VttGrid grid, VttPoint from, VttPoint to)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return InFeet(grid, to.X - from.X, to.Y - from.Y);
    }

    /// <summary>格数规则本身（入参已经是格，不是像素）。</summary>
    public static double GridCells(VttMeasurement measurement, double deltaX, double deltaY)
    {
        var dx = Math.Abs(deltaX);
        var dy = Math.Abs(deltaY);

        return measurement switch
        {
            VttMeasurement.Chessboard => Math.Max(dx, dy),
            VttMeasurement.Alternating => Math.Max(dx, dy) + Math.Floor(Math.Min(dx, dy) / 2),
            VttMeasurement.Euclidean => Math.Sqrt((dx * dx) + (dy * dy)),
            VttMeasurement.Manhattan => dx + dy,
            _ => Math.Max(dx, dy),
        };
    }

    /// <summary>给模型看的读数：取整到整尺（标尺读数也是整尺，两边能对上）。</summary>
    public static string Describe(double feet) =>
        Math.Round(feet, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " 尺";

    /// <summary>度量名的中文说明：进提示词时用，避免模型把规则名当成剧情内容。</summary>
    public static string DescribeMeasurement(VttMeasurement measurement) => measurement switch
    {
        VttMeasurement.Chessboard => "chessboard（对角与直走同价）",
        VttMeasurement.Alternating => "alternating（每两个对角格算三格）",
        VttMeasurement.Euclidean => "euclidean（直线距离）",
        VttMeasurement.Manhattan => "manhattan（横竖相加）",
        _ => measurement.ToString(),
    };
}
