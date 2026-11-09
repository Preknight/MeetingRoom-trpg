namespace MeetingRoom.Core.Rules;

/// <summary>
/// 武器射程解析（C4）：真实包的 <c>items[].range</c> 是原生字符串（<c>"150/600"</c>），
/// 近战则看 <c>reach</c>（默认 5）。<b>有 range 就不看 reach</b>——导出里远程武器的 reach 是默认值冗余。
/// </summary>
public static class WeaponRange
{
    public const int DefaultReachFeet = 5;

    /// <summary>解析 <c>"150/600"</c> / <c>"30"</c>；格式不对返回 false（不猜）。</summary>
    public static bool TryParse(string? range, out int normalFeet, out int longFeet)
    {
        normalFeet = 0;
        longFeet = 0;

        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var parts = range.Trim().Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || parts.Length > 2
            || !int.TryParse(parts[0], out normalFeet) || normalFeet <= 0)
        {
            return false;
        }

        if (parts.Length == 2 && (!int.TryParse(parts[1], out longFeet) || longFeet <= 0))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            longFeet = normalFeet;
        }

        return true;
    }

    /// <summary>近战触及：有 reach 用 reach，否则默认 5。</summary>
    public static int ReachOf(ItemDefinition weapon)
    {
        ArgumentNullException.ThrowIfNull(weapon);

        return weapon.Reach is { } reach && reach > 0 ? reach : DefaultReachFeet;
    }
}
