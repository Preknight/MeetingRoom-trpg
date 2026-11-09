using MeetingRoom.Core.Rules;

namespace MeetingRoom.Core.Combat;

/// <summary>一次专注检定的结果（C4）：是否需要检定、DC、掷骰、是否维持、以及更新后的 Sheet。</summary>
public sealed record ConcentrationCheckResult(
    bool Required,
    int Dc,
    RollEvent? Roll,
    bool Maintained,
    Sheet Sheet);

/// <summary>
/// 专注检定（C4）：受伤时 CON 豁免，<b>DC = max(10, 伤害 / 2)</b>；失败则专注中断（清 <c>Sheet.Concentration</c>）。
/// 没有专注时不做检定（<see cref="ConcentrationCheckResult.Required"/> = false），也不消耗随机源。
/// </summary>
public static class ConcentrationCheck
{
    public static ConcentrationCheckResult Check(Sheet sheet, int damage, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (sheet.Concentration is null)
        {
            return new ConcentrationCheckResult(false, 0, null, true, sheet);
        }

        var dc = Math.Max(10, damage / 2);
        var bonus = AbilityModifier(sheet, Ability.Constitution);

        if (sheet.SaveProficiencies.Contains(Ability.Constitution))
        {
            bonus += sheet.ProficiencyBonus ?? 0;
        }

        IReadOnlyList<RollSource> sources = bonus == 0 ? [] : [new RollSource("体质豁免", bonus)];
        var roll = D20.RollSave(dc, sources, modes: null, random: random);
        var maintained = roll.Success == true;
        var updated = maintained ? sheet : sheet with { Concentration = null };

        return new ConcentrationCheckResult(true, dc, roll, maintained, updated);
    }

    private static int AbilityModifier(Sheet sheet, Ability ability) =>
        sheet.Abilities is { } abilities && abilities.TryGetValue(ability, out var score)
            ? (int)Math.Floor((score - 10) / 2.0)
            : 0;
}
