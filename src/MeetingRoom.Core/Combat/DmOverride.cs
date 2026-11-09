using System.Text.Json;

using MeetingRoom.Core.Rules;

namespace MeetingRoom.Core.Combat;

/// <summary>
/// DM 覆盖请求（D3）：只给"改成什么"，<b>from 由引擎填</b>。
/// <para>
/// <see cref="Field"/> 走白名单；<see cref="Value"/> 用 <see cref="JsonElement"/> 承接，
/// 这样 <c>hp</c>（数字）、<c>condition</c>（对象）、<c>budget</c>（对象）能共用一条通道。
/// </para>
/// </summary>
public sealed record DmOverrideRequest
{
    public required string Target { get; init; }

    public required string Field { get; init; }

    public JsonElement? Value { get; init; }

    public string? Reason { get; init; }

    /// <summary><c>field=dc</c> 时指定要改哪条检定记录。</summary>
    public string? CheckId { get; init; }
}

/// <summary>一次覆盖的结果：成功给新快照与 <c>from/to</c>，失败给可叙述的 code + message。</summary>
public sealed record DmOverrideResult(
    bool Ok,
    string? Code,
    string? Message,
    string Field,
    string? From,
    string? To,
    Sheet? Sheet,
    Encounter? Encounter,
    bool CreatedSheet = false);

/// <summary>
/// DM 覆盖引擎（D3）：九个白名单字段，其余一律拒绝并列出支持项。
/// <para>
/// <b>不绕开 D1</b>：改 <c>hp</c> 走与伤害/治疗同一套迁移（0 → <see cref="VitalStatus.Dying"/>，
/// 不会直接判死）；改 <c>status</c> 只动状态、不动 <c>DeathSaves</c>、不动 HP。
/// </para>
/// </summary>
public static class DmOverrideEngine
{
    public static IReadOnlyList<string> SupportedFields { get; } =
    [
        "hp", "tempHp", "maxHp", "ac", "status", "condition", "initiative", "budget", "dc",
    ];

    public static DmOverrideResult Apply(Encounter? encounter, SheetStore? sheets, DmOverrideRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var field = request.Field?.Trim() ?? string.Empty;

        if (!SupportedFields.Contains(field, StringComparer.OrdinalIgnoreCase))
        {
            return new DmOverrideResult(
                false,
                "unknown-field",
                $"不支持覆盖字段「{request.Field}」；支持：{string.Join("、", SupportedFields)}。",
                field,
                null,
                null,
                null,
                null);
        }

        if (string.IsNullOrWhiteSpace(request.Target))
        {
            return Fail("missing-target", "覆盖要给 target（角色 id）。", field);
        }

        var target = request.Target.Trim();

        return field.ToLowerInvariant() switch
        {
            "hp" => OverrideHp(sheets, target, request),
            "temphp" => OverrideScalar(sheets, target, request, "tempHp"),
            "maxhp" => OverrideScalar(sheets, target, request, "maxHp"),
            "ac" => OverrideScalar(sheets, target, request, "ac"),
            "status" => OverrideStatus(sheets, target, request),
            "condition" => OverrideCondition(encounter, target, request),
            "initiative" => OverrideInitiative(encounter, target, request),
            "budget" => OverrideBudget(encounter, request),
            "dc" => OverrideDc(request),
            _ => Fail("unknown-field", $"不支持覆盖字段「{field}」。", field),
        };
    }

    /// <summary>
    /// 改 HP（D3）：<b>走 D1 的迁移规则</b>——0 且原本活着 → <see cref="VitalStatus.Dying"/>（不是 Dead）；
    /// 已 Stable 设 0 保持 Stable（它本来就 0 HP）；已 Dead 保持 Dead；回正值 → Alive 并清空死亡豁免。
    /// </summary>
    private static DmOverrideResult OverrideHp(SheetStore? sheets, string target, DmOverrideRequest request)
    {
        if (sheets is null)
        {
            return Fail("no-sheet-store", "没有数值表（SheetStore），HP 覆盖无法执行。", "hp");
        }

        if (!TryInt(request.Value, out var value))
        {
            return Fail("bad-value", "hp 覆盖要给整数。", "hp");
        }

        // r9：模型被 HP 守卫拒后，会用 dm_override 把 HP 直接改成 0 再重报 defeat——
        // 那等于绕过守卫。所以 hp 只允许改成正数：让人倒下走伤害，收场走 dm_end。
        if (value <= 0)
        {
            return Fail(
                "override-hp-zero",
                "不能用 dm_override 把人改成 0 HP；要让人倒下用 combat_action 的伤害，要收场用 dm_end。",
                "hp");
        }

        var before = sheets.Get(target);

        if (before is null)
        {
            // §26.5：无表时允许用 value 对象创建最小表（至少 currentHp / maxHp / ac），
            // 事件里要标 createdSheet，否则"凭空出现一张表"无从解释。
            if (TryReadMinimalSheet(request.Value, out var currentHp, out var maxHp, out var armorClass))
            {
                var created = sheets.CreateMinimal(target, currentHp, maxHp, armorClass);

                return new DmOverrideResult(
                    true,
                    null,
                    $"「{target}」原本没有数值表；已按给的值创建最小表（HP {created.CurrentHp}/{created.MaxHp}、AC {created.ArmorClass}）。",
                    "hp",
                    null,
                    created.CurrentHp?.ToString(),
                    created,
                    null,
                    CreatedSheet: true);
            }

            return Fail(
                "unknown-sheet",
                $"数值表里没有「{target}」。要建表请把 value 给成对象：{{\"currentHp\": 12, \"maxHp\": 12, \"ac\": 13}}；否则改用 combat_action 或补 roles/{target}.sheet.json。",
                "hp");
        }

        var (ok, message, updated) = sheets.OverrideHp(target, value);

        return ok
            ? new DmOverrideResult(true, null, message, "hp", before.CurrentHp?.ToString(), updated?.CurrentHp?.ToString(), updated, null)
            : Fail("override-failed", message, "hp");
    }

    private static DmOverrideResult OverrideScalar(SheetStore? sheets, string target, DmOverrideRequest request, string field)
    {
        if (sheets is null)
        {
            return Fail("no-sheet-store", $"没有数值表（SheetStore），{field} 覆盖无法执行。", field);
        }

        if (!TryInt(request.Value, out var value))
        {
            return Fail("bad-value", $"{field} 覆盖要给整数。", field);
        }

        var before = sheets.Get(target);

        if (before is null)
        {
            return Fail("unknown-sheet", $"数值表里没有「{target}」。", field);
        }

        var (ok, message, updated) = sheets.OverrideScalar(target, field, value);
        var from = field switch
        {
            "tempHp" => before.TempHp?.ToString(),
            "maxHp" => before.MaxHp?.ToString(),
            _ => before.ArmorClass?.ToString(),
        };

        return ok
            ? new DmOverrideResult(true, null, message, field, from, value.ToString(), updated, null)
            : Fail("override-failed", message, field);
    }

    private static DmOverrideResult OverrideStatus(SheetStore? sheets, string target, DmOverrideRequest request)
    {
        if (sheets is null)
        {
            return Fail("no-sheet-store", "没有数值表（SheetStore），状态覆盖无法执行。", "status");
        }

        if (!TryString(request.Value, out var text)
            || !Enum.TryParse<VitalStatus>(text, ignoreCase: true, out var status))
        {
            return Fail("bad-value", "status 覆盖要给 alive / dying / stable / dead。", "status");
        }

        var before = sheets.Get(target);

        if (before is null)
        {
            return Fail("unknown-sheet", $"数值表里没有「{target}」。", "status");
        }

        // 只改状态：不动 HP、不动死亡豁免（D3 明写）。
        var (ok, message, updated) = sheets.OverrideStatus(target, status);

        return ok
            ? new DmOverrideResult(
                true, null, message, "status",
                before.Status.ToString().ToLowerInvariant(),
                status.ToString().ToLowerInvariant(),
                updated, null)
            : Fail("override-failed", message, "status");
    }

    private static DmOverrideResult OverrideCondition(Encounter? encounter, string target, DmOverrideRequest request)
    {
        if (encounter is null)
        {
            return Fail("no-encounter", "现在不在战斗中，状态覆盖需要一场战斗。", "condition");
        }

        if (encounter.Find(target) is null)
        {
            return Fail("unknown-combatant", $"战斗名单里没有「{target}」。", "condition");
        }

        var (name, rounds, remove) = ReadCondition(request);

        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail("bad-value", "condition 覆盖要给 name（可带 rounds / remove）。", "condition");
        }

        var before = encounter.Find(target)!.ConditionRounds.TryGetValue(name, out var current)
            ? current.ToString()
            : null;

        var updated = remove
            ? EncounterEngine.RemoveCondition(encounter, target, name)
            : EncounterEngine.ApplyCondition(encounter, target, name, rounds ?? 1);

        return new DmOverrideResult(
            true, null, remove ? $"已移除状态「{name}」。" : $"已挂上状态「{name}」（{rounds ?? 1} 回合）。",
            "condition", before, remove ? null : (rounds ?? 1).ToString(), null, updated);
    }

    private static DmOverrideResult OverrideInitiative(Encounter? encounter, string target, DmOverrideRequest request)
    {
        if (encounter is null)
        {
            return Fail("no-encounter", "现在不在战斗中，先攻覆盖需要一场战斗。", "initiative");
        }

        if (!TryInt(request.Value, out var value))
        {
            return Fail("bad-value", "initiative 覆盖要给整数。", "initiative");
        }

        var combatant = encounter.Find(target);

        if (combatant is null)
        {
            return Fail("unknown-combatant", $"战斗名单里没有「{target}」。", "initiative");
        }

        var updated = EncounterEngine.SetInitiative(encounter, target, value);

        return new DmOverrideResult(
            true, null, $"「{target}」先攻改为 {value}，已重排顺序（不打断当前回合）。",
            "initiative", combatant.Initiative.ToString(), value.ToString(), null, updated);
    }

    private static DmOverrideResult OverrideBudget(Encounter? encounter, DmOverrideRequest request)
    {
        if (encounter is null || encounter.Turn is null)
        {
            return Fail("no-encounter", "现在不在战斗中，行动经济覆盖需要一场战斗。", "budget");
        }

        var (action, bonus, reaction, movement) = ReadBudget(request);

        if (action is null && bonus is null && reaction is null && movement is null)
        {
            return Fail("bad-value", "budget 覆盖要给 action / bonus / reaction / movementFeet 中至少一项。", "budget");
        }

        var turn = encounter.Turn;
        var before = $"action={(turn.ActionUsed ? 1 : 0)},bonus={(turn.BonusUsed ? 1 : 0)},reaction={(turn.ReactionUsed ? 1 : 0)},movement={turn.MovementUsedFeet}";

        var updated = EncounterEngine.SetBudget(encounter, action, bonus, reaction, movement);

        return new DmOverrideResult(
            true, null, "当前回合的行动经济已按 DM 覆盖调整。",
            "budget", before, $"action={Text(action)},bonus={Text(bonus)},reaction={Text(reaction)},movement={Text(movement)}",
            null, updated);
    }

    private static DmOverrideResult OverrideDc(DmOverrideRequest request)
    {
        if (!TryInt(request.Value, out var value))
        {
            return Fail("bad-value", "dc 覆盖要给整数。", "dc");
        }

        if (string.IsNullOrWhiteSpace(request.CheckId))
        {
            return Fail("missing-check-id", "dc 覆盖要指定 checkId（只对那一条检定生效）。", "dc");
        }

        return new DmOverrideResult(
            true, null, $"检定 {request.CheckId} 的 DC 改为 {value}。", "dc", null, value.ToString(), null, null);
    }

    private static (string? Name, int? Rounds, bool Remove) ReadCondition(DmOverrideRequest request)
    {
        if (request.Value is { ValueKind: JsonValueKind.Object } obj)
        {
            var name = obj.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var rounds = obj.TryGetProperty("rounds", out var roundsElement) && roundsElement.TryGetInt32(out var r) ? r : (int?)null;
            var remove = obj.TryGetProperty("remove", out var removeElement) && removeElement.ValueKind == JsonValueKind.True;
            return (name, rounds, remove);
        }

        if (request.Value is { ValueKind: JsonValueKind.String } text)
        {
            return (text.GetString(), null, false);
        }

        return (null, null, false);
    }

    private static (bool? Action, bool? Bonus, bool? Reaction, int? Movement) ReadBudget(DmOverrideRequest request)
    {
        if (request.Value is not { ValueKind: JsonValueKind.Object } obj)
        {
            return (null, null, null, null);
        }

        bool? Read(string name) =>
            obj.TryGetProperty(name, out var element)
                ? element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                }
                : null;

        return (
            Read("action"),
            Read("bonus"),
            Read("reaction"),
            obj.TryGetProperty("movementFeet", out var movement) && movement.TryGetInt32(out var feet) ? feet : null);
    }

    private static bool TryReadMinimalSheet(
        JsonElement? value,
        out int currentHp,
        out int maxHp,
        out int armorClass)
    {
        currentHp = 0;
        maxHp = 0;
        armorClass = 10;

        if (value is not { ValueKind: JsonValueKind.Object } obj)
        {
            return false;
        }

        if (!obj.TryGetProperty("currentHp", out var current) || !current.TryGetInt32(out currentHp)
            || !obj.TryGetProperty("maxHp", out var max) || !max.TryGetInt32(out maxHp))
        {
            return false;
        }

        if (obj.TryGetProperty("ac", out var ac) && ac.TryGetInt32(out var parsed))
        {
            armorClass = parsed;
        }

        return true;
    }

    private static string Text(bool? value) => value is null ? "-" : value.Value.ToString().ToLowerInvariant();

    private static string Text(int? value) => value?.ToString() ?? "-";

    private static bool TryInt(JsonElement? value, out int result)
    {
        result = 0;

        return value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out result);
    }

    private static bool TryString(JsonElement? value, out string result)
    {
        result = string.Empty;

        if (value is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } parsed)
        {
            result = parsed;
            return true;
        }

        return false;
    }

    private static DmOverrideResult Fail(string code, string message, string field) =>
        new(false, code, message, field, null, null, null, null);
}
