using MeetingRoom.Core.Rules;

namespace MeetingRoom.Core.Combat;

/// <summary>一次死亡豁免的结果（D1）：掷了什么、成功/失败、累计到几、掷完是什么状态。</summary>
public sealed record DeathSaveResult(
    bool Required,
    int Natural,
    int Total,
    bool? Success,
    DeathSaves Saves,
    VitalStatus Status,
    Sheet Sheet,
    string Message,
    RollEvent? Roll = null);

/// <summary>
/// 死亡豁免（D1）：<c>d20</c> 无加值。
/// <list type="bullet">
/// <item>natural 20 → 回 1 HP、转 <see cref="VitalStatus.Alive"/>、清零；</item>
/// <item>10–19 → 成功 +1；1–9 → 失败 +1；natural 1 → 失败 +2；</item>
/// <item>成功累计 3 → <see cref="VitalStatus.Stable"/>；失败累计 3 → <see cref="VitalStatus.Dead"/>。</item>
/// <item>成为 Stable 或恢复 HP 时，成功/失败<b>都清零</b>（PHB：<c>reset to zero when you regain any Hit Points or become Stable</c>）。</item>
/// </list>
/// 只有 <see cref="VitalStatus.Dying"/> 才掷；其它状态返回 <c>Required = false</c> 且不动随机源。
/// </summary>
public static class DeathSaveEngine
{
    /// <summary>医学稳定检定 DC（一次动作）。</summary>
    public const int MedicineDc = 10;

    /// <summary>三成功稳定、三失败死亡。</summary>
    public const int SavesToResolve = 3;

    public static DeathSaveResult Roll(Sheet sheet, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (sheet.Status != VitalStatus.Dying)
        {
            return new DeathSaveResult(
                false, 0, 0, null, sheet.DeathSaves ?? new DeathSaves(0, 0), sheet.Status, sheet,
                $"「{sheet.CharacterId}」不是在濒死状态，不需要掷死亡豁免。");
        }

        var natural = (random ?? Random.Shared).Next(1, 21);
        var saves = sheet.DeathSaves ?? new DeathSaves(0, 0);

        if (natural == 20)
        {
            var revived = sheet with
            {
                CurrentHp = Math.Max(1, sheet.CurrentHp ?? 0),
                Status = VitalStatus.Alive,
                DeathSaves = new DeathSaves(0, 0),
            };

            return new DeathSaveResult(
                true, natural, natural, true, new DeathSaves(0, 0), VitalStatus.Alive, revived,
                "死亡豁免 natural 20：回了 1 HP，转回存活（失败/成功清零）。",
                BuildRoll(natural, success: true));
        }

        var successes = saves.Successes;
        var failures = saves.Failures;

        if (natural == 1)
        {
            failures += 2;
        }
        else if (natural >= 10)
        {
            successes += 1;
        }
        else
        {
            failures += 1;
        }

        var status = VitalStatus.Dying;

        if (successes >= SavesToResolve)
        {
            // PHB：恢复 HP 或成为 Stable 时，成功/失败都清零（两个触发条件，不是只有回 HP）。
            status = VitalStatus.Stable;
            successes = 0;
            failures = 0;
        }
        else if (failures >= SavesToResolve)
        {
            status = VitalStatus.Dead;
            failures = SavesToResolve;
        }

        var updated = saves == new DeathSaves(successes, failures) && status == sheet.Status
            ? sheet
            : sheet with
            {
                Status = status,
                DeathSaves = new DeathSaves(successes, failures),
            };

        var message = status switch
        {
            VitalStatus.Stable => $"死亡豁免 {natural}：第 {SavesToResolve} 次成功，已稳定（成功/失败清零）。",
            VitalStatus.Dead => $"死亡豁免 {natural}：失败累计 {failures}/3，角色死亡。",
            _ when natural == 1 => $"死亡豁免 natural 1：记两次失败（{successes}/3 成功，{failures}/3 失败）。",
            _ when natural >= 10 => $"死亡豁免 {natural}：成功 +1（{successes}/3）。",
            _ => $"死亡豁免 {natural}：失败 +1（{failures}/3）。",
        };

        return new DeathSaveResult(
            true, natural, natural, natural >= 10,
            new DeathSaves(successes, failures), status, updated, message,
            BuildRoll(natural, success: natural >= 10));
    }

    private static RollEvent BuildRoll(int natural, bool success) => new()
    {
        Mode = AdvantageMode.Normal,
        Rolled = [natural],
        Natural = natural,
        Modifier = 0,
        Sources = [],
        Total = natural,
        Success = success,
        Crit = natural == 20,
        Fumble = natural == 1,
    };

    /// <summary>
    /// 医学稳定（D1）：DC 10 成功 → <see cref="VitalStatus.Stable"/>（不改死亡豁免计数、不改 HP）。
    /// 非濒死状态返回 <c>Ok = false</c>，由调用方给出可叙述的拒绝。
    /// </summary>
    public static (bool Ok, string Message, Sheet Sheet) Stabilize(Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (sheet.Status != VitalStatus.Dying)
        {
            return (false, $"「{sheet.CharacterId}」现在不是濒死状态，不需要稳定。", sheet);
        }

        return (
            true,
            $"医学检定 DC {MedicineDc} 成功：已稳定（不再掷死亡豁免，成功/失败清零）。",
            sheet with { Status = VitalStatus.Stable, DeathSaves = new DeathSaves(0, 0) });
    }
}
