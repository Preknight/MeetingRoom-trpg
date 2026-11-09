using System.Text.Json;
using System.Text.Json.Serialization;

using MeetingRoom.Core.Rules;

namespace MeetingRoom.Core.Combat;

/// <summary>一次扣血的结果（无论是否成功都给可读原因，拒绝也要能叙述）。</summary>
public sealed record DamageApplication(
    bool Ok,
    string? Code,
    string? Message,
    Sheet? Sheet,
    int AbsorbedByTempHp,
    int HpLost,
    int? HpBefore,
    int? HpAfter,
    int? TempHpAfter,
    VitalStatus? Status,
    DeathSaves? Saves = null);

/// <summary>
/// 数值表仓库（C3a）：<b>HP 的唯一真相</b>。按角色 id 存取 <see cref="Sheet"/>，
/// 可选落盘为 JSON（<c>sheets.json</c>，与工作空间同目录）。
/// <para>
/// 扣血规则（D1）：<b>临时 HP 先吸收</b>；掉到 0 HP → <see cref="VitalStatus.Dying"/>（或已稳定/濒死时受伤记失败），
/// 单次溢出 ≥ 最大 HP → 直接 <see cref="VitalStatus.Dead"/>。治疗走 <see cref="Heal"/>，DM 改数走 <see cref="SetHp"/>。
/// </para>
/// </summary>
public sealed class SheetStore
{
    public const string DefaultFileName = "sheets.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// 按角色 id 存表。<b>大小写不敏感</b>（OrdinalIgnoreCase）：说书人给的战斗者 id 常写成
    /// <c>Ella</c>，而角色卡旁路表叫 <c>ella</c>——用 Ordinal 会让"表明明存在却扣不下去"（端到端实测踩到）。
    /// </summary>
    private readonly Dictionary<string, Sheet> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly string? _path;

    public SheetStore(string? path = null)
    {
        _path = path;

        if (path is not null && File.Exists(path))
        {
            Load(path);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sheets.Count;
            }
        }
    }

    public IReadOnlyList<Sheet> All
    {
        get
        {
            lock (_gate)
            {
                return _sheets.Values.ToArray();
            }
        }
    }

    public void Put(Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        lock (_gate)
        {
            _sheets[sheet.CharacterId] = sheet;
            PersistLocked(_sheets.Values.ToList());
        }
    }

    public Sheet? Get(string characterId)
    {
        lock (_gate)
        {
            return _sheets.TryGetValue(characterId, out var sheet) ? sheet : null;
        }
    }

    public bool Remove(string characterId)
    {
        lock (_gate)
        {
            if (!_sheets.Remove(characterId))
            {
                return false;
            }

            PersistLocked(_sheets.Values.ToList());
            return true;
        }
    }

    /// <summary>
    /// 扣血：临时 HP 先吸收，再扣当前 HP；0 HP → 昏迷 / 死亡两态。
    /// 目标没登记、或 HP 字段未填时返回可读拒绝（<b>不静默</b>）。
    /// </summary>
    public DamageApplication ApplyDamage(string targetId, int amount, bool critical = false)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(targetId, out var sheet))
            {
                return Fail("unknown-sheet", $"数值表里没有「{targetId}」，伤害没有扣。", null);
            }

            if (sheet.MaxHp is not { } maxHp || sheet.CurrentHp is not { } current || sheet.TempHp is not { } tempHp)
            {
                return Fail("incomplete-sheet", $"「{targetId}」的 HP 字段未填（maxHp / currentHp / tempHp），伤害没有扣。", sheet);
            }

            if (amount <= 0)
            {
                return new DamageApplication(true, null, null, sheet, 0, 0, current, current, tempHp, sheet.Status, sheet.DeathSaves);
            }

            var absorbed = Math.Min(tempHp, amount);
            var remaining = amount - absorbed;
            var hpLost = Math.Min(current, remaining);
            var overflow = remaining - hpLost;
            var newCurrent = current - hpLost;
            var newTemp = tempHp - absorbed;

            var status = sheet.Status;
            var saves = sheet.DeathSaves ?? new DeathSaves(0, 0);

            if (newCurrent == 0 && status != VitalStatus.Dead)
            {
                if (overflow >= maxHp)
                {
                    // massive damage：单次溢出 ≥ 最大 HP → 直接死亡（不经过 Dying）。
                    status = VitalStatus.Dead;
                }
                else if (status is VitalStatus.Dying or VitalStatus.Stable)
                {
                    // 濒死 / 已稳定时再受伤：失败 +1（暴击 +2），累计 3 次即死；状态回到 Dying。
                    status = VitalStatus.Dying;
                    saves = saves with { Failures = saves.Failures + (critical ? 2 : 1) };

                    if (saves.Failures >= DeathSaveEngine.SavesToResolve)
                    {
                        status = VitalStatus.Dead;
                        saves = saves with { Failures = DeathSaveEngine.SavesToResolve };
                    }
                }
                else
                {
                    // 从存活掉到 0：进入濒死，死亡豁免清零。
                    status = VitalStatus.Dying;
                    saves = new DeathSaves(0, 0);
                }
            }

            var updated = sheet with
            {
                CurrentHp = newCurrent,
                TempHp = newTemp,
                Status = status,
                DeathSaves = saves,
            };

            _sheets[targetId] = updated;
            PersistLocked(_sheets.Values.ToList());

            return new DamageApplication(
                true,
                null,
                null,
                updated,
                absorbed,
                hpLost,
                current,
                newCurrent,
                newTemp,
                status,
                saves);
        }
    }

    /// <summary>DM 改数 / 治疗：直接设当前 HP 与临时 HP；回到正值时状态复位为存活。</summary>
    public Sheet? SetHp(string characterId, int currentHp, int? tempHp = null)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return null;
            }

            var max = sheet.MaxHp ?? Math.Max(currentHp, 0);
            var clamped = Math.Clamp(currentHp, 0, Math.Max(max, 0));
            var updated = sheet with
            {
                CurrentHp = clamped,
                TempHp = tempHp ?? sheet.TempHp ?? 0,
                Status = clamped > 0
                    ? VitalStatus.Alive
                    : sheet.Status == VitalStatus.Dead ? VitalStatus.Dead : VitalStatus.Dying,
                DeathSaves = clamped > 0 ? new DeathSaves(0, 0) : sheet.DeathSaves,
            };

            _sheets[characterId] = updated;
            PersistLocked(_sheets.Values.ToList());
            return updated;
        }
    }

    /// <summary>
    /// 治疗（D1）：加 HP；从 0 HP 回到正值 → <see cref="VitalStatus.Alive"/> 并清空死亡豁免。
    /// 角色不存在返回 <c>null</c>。
    /// </summary>
    public Sheet? Heal(string characterId, int amount)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return null;
            }

            var max = sheet.MaxHp ?? int.MaxValue;
            var current = Math.Clamp((sheet.CurrentHp ?? 0) + Math.Max(0, amount), 0, Math.Max(max, 0));
            var updated = sheet with
            {
                CurrentHp = current,
                Status = current > 0 ? VitalStatus.Alive : sheet.Status,
                DeathSaves = current > 0 ? new DeathSaves(0, 0) : sheet.DeathSaves,
            };

            _sheets[characterId] = updated;
            PersistLocked(_sheets.Values.ToList());
            return updated;
        }
    }

    /// <summary>
    /// 施法前检查法术位（D2，<b>不改状态</b>）：非施法者 → <c>not-a-spellcaster</c>；
    /// 该环没有或已用满 → <c>no-slot</c>。顺序固定：先"是不是施法者"，再"有没有槽"。
    /// </summary>
    public (bool Ok, string Code, string Message) CanSpendSlot(string characterId, int castAtLevel)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return (false, "unknown-sheet", $"数值表里没有「{characterId}」。");
            }

            if (sheet.SpellSlots.Count == 0)
            {
                return (false, "not-a-spellcaster", $"「{characterId}」没有法术位表，不是施法者。");
            }

            if (!sheet.SpellSlots.TryGetValue(castAtLevel, out var slot) || slot.Used >= slot.Max)
            {
                return (false, "no-slot", $"「{characterId}」的 {castAtLevel} 环法术位已经用完（上限 {sheet.SpellSlots.GetValueOrDefault(castAtLevel)?.Max ?? 0}）。");
            }

            return (true, string.Empty, string.Empty);
        }
    }

    /// <summary>消耗一格法术位（D2）：先走 <see cref="CanSpendSlot"/> 的同一套判定，再 <c>Used + 1</c>。</summary>
    public (bool Ok, string Code, string Message, Sheet? Sheet) SpendSlot(string characterId, int castAtLevel)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return (false, "unknown-sheet", $"数值表里没有「{characterId}」。", null);
            }

            if (sheet.SpellSlots.Count == 0)
            {
                return (false, "not-a-spellcaster", $"「{characterId}」没有法术位表，不是施法者。", sheet);
            }

            if (!sheet.SpellSlots.TryGetValue(castAtLevel, out var slot) || slot.Used >= slot.Max)
            {
                return (false, "no-slot", $"「{characterId}」的 {castAtLevel} 环法术位已经用完。", sheet);
            }

            var slots = new Dictionary<int, SpellSlot>(sheet.SpellSlots)
            {
                [castAtLevel] = slot with { Used = slot.Used + 1 },
            };

            var updated = sheet with { SpellSlots = slots };
            _sheets[characterId] = updated;
            PersistLocked(_sheets.Values.ToList());
            return (true, string.Empty, string.Empty, updated);
        }
    }

    /// <summary>
    /// 长休（D2）：所有法术位 <c>Used = 0</c>、HP 回满、死亡豁免清零、状态清空、专注中断。
    /// DM 想保留的状态应在休整后重新挂（不在这里加白名单，避免"跨休保留"变成隐藏规则）。
    /// </summary>
    public Sheet? LongRest(string characterId)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return null;
            }

            var slots = new Dictionary<int, SpellSlot>(sheet.SpellSlots);

            foreach (var level in slots.Keys.ToList())
            {
                slots[level] = slots[level] with { Used = 0 };
            }

            var updated = sheet with
            {
                CurrentHp = sheet.MaxHp ?? sheet.CurrentHp,
                Status = VitalStatus.Alive,
                DeathSaves = new DeathSaves(0, 0),
                Conditions = [],
                Concentration = null,
                SpellSlots = slots,
            };

            _sheets[characterId] = updated;
            PersistLocked(_sheets.Values.ToList());
            return updated;
        }
    }

    /// <summary>
    /// 短休（D2）：<b>不恢复法术位</b>。Warlock 的 Pact Magic 需要职业字段，
    /// <see cref="Sheet"/> 现在没有 → 明确返回"暂不支持"，不假装恢复。
    /// </summary>
    public (bool Ok, string Message, Sheet? Sheet) ShortRest(string characterId)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return (false, $"数值表里没有「{characterId}」。", null);
            }

            return (true, "短休：不恢复法术位（Pact Magic 暂不支持——Sheet 没有职业字段，不假装恢复）。", sheet);
        }
    }

    /// <summary>
    /// DM 覆盖 HP（D3）：<b>复用 D1 的迁移规则</b>，不另写一套。
    /// <list type="bullet">
    /// <item>回正值 → <see cref="VitalStatus.Alive"/> 且死亡豁免清零；</item>
    /// <item>0 且原本活着 → <see cref="VitalStatus.Dying"/>（<b>不是</b> Dead）；</item>
    /// <item>原本 Stable 设 0 → 保持 Stable（它本来就 0 HP）；原本 Dead → 保持 Dead。</item>
    /// </list>
    /// </summary>
    public (bool Ok, string Message, Sheet? Sheet) OverrideHp(string characterId, int value)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return (false, $"数值表里没有「{characterId}」。", null);
            }

            var max = sheet.MaxHp ?? Math.Max(value, 0);
            var clamped = Math.Clamp(value, 0, Math.Max(max, 0));

            var status = clamped > 0
                ? VitalStatus.Alive
                : sheet.Status switch
                {
                    VitalStatus.Dead => VitalStatus.Dead,
                    VitalStatus.Stable => VitalStatus.Stable,
                    _ => VitalStatus.Dying,
                };

            var updated = sheet with
            {
                CurrentHp = clamped,
                Status = status,
                DeathSaves = clamped > 0 ? new DeathSaves(0, 0) : sheet.DeathSaves,
            };

            _sheets[characterId] = updated;
            PersistLocked(_sheets.Values.ToList());
            return (true, $"HP 覆盖为 {clamped}（{status.ToString().ToLowerInvariant()}）。", updated);
        }
    }

    /// <summary>
    /// 无表时创建最小数值表（§26.5）：至少 <c>currentHp / maxHp / ac</c>。
    /// 只给这三个字段——六属性 / 熟练仍然空着，所以这张表只能应付扣血，
    /// 检定仍会走"字段未填"的拒绝；要完整就补 <c>roles/&lt;id&gt;.sheet.json</c>。
    /// </summary>
    public Sheet CreateMinimal(string characterId, int currentHp, int maxHp, int armorClass)
    {
        lock (_gate)
        {
            var max = Math.Max(1, maxHp);
            var current = Math.Clamp(currentHp, 0, max);

            var sheet = new Sheet
            {
                CharacterId = characterId,
                MaxHp = max,
                CurrentHp = current,
                TempHp = 0,
                ArmorClass = armorClass,
                Status = current > 0 ? VitalStatus.Alive : VitalStatus.Dying,
                DeathSaves = new DeathSaves(0, 0),
            };

            _sheets[characterId] = sheet;
            PersistLocked(_sheets.Values.ToList());
            return sheet;
        }
    }

    /// <summary>DM 覆盖标量（D3）：<c>tempHp</c> / <c>maxHp</c> / <c>ac</c>；不做状态迁移。</summary>
    public (bool Ok, string Message, Sheet? Sheet) OverrideScalar(string characterId, string field, int value)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return (false, $"数值表里没有「{characterId}」。", null);
            }

            var updated = field switch
            {
                "tempHp" => sheet with { TempHp = Math.Max(0, value) },
                "maxHp" => sheet with { MaxHp = Math.Max(1, value) },
                "ac" => sheet with { ArmorClass = value },
                _ => null,
            };

            if (updated is null)
            {
                return (false, $"不支持标量覆盖字段「{field}」（只支持 tempHp / maxHp / ac）。", sheet);
            }

            _sheets[characterId] = updated;
            PersistLocked(_sheets.Values.ToList());
            return (true, $"{field} 覆盖为 {value}。", updated);
        }
    }

    /// <summary>
    /// DM 覆盖生命状态（D3）：<b>只改状态</b>，不改 HP、<b>不动 <c>DeathSaves</c></b>。
    /// </summary>
    public (bool Ok, string Message, Sheet? Sheet) OverrideStatus(string characterId, VitalStatus status)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return (false, $"数值表里没有「{characterId}」。", null);
            }

            var updated = sheet with { Status = status };
            _sheets[characterId] = updated;
            PersistLocked(_sheets.Values.ToList());
            return (true, $"状态覆盖为 {status.ToString().ToLowerInvariant()}（HP 与死亡豁免不变）。", updated);
        }
    }

    /// <summary>医学稳定（D1）：濒死 → 已稳定（不改 HP、不改死亡豁免计数）。</summary>
    public (bool Ok, string Message, Sheet? Sheet) Stabilize(string characterId)
    {
        lock (_gate)
        {
            if (!_sheets.TryGetValue(characterId, out var sheet))
            {
                return (false, $"数值表里没有「{characterId}」。", null);
            }

            var (ok, message, updated) = DeathSaveEngine.Stabilize(sheet);

            if (!ok)
            {
                return (false, message, sheet);
            }

            _sheets[characterId] = updated;
            PersistLocked(_sheets.Values.ToList());
            return (true, message, updated);
        }
    }

    private static DamageApplication Fail(string code, string message, Sheet? sheet) =>
        new(false, code, message, sheet, 0, 0, sheet?.CurrentHp, sheet?.CurrentHp, sheet?.TempHp, sheet?.Status);

    private void PersistLocked(IReadOnlyList<Sheet> sheets)
    {
        if (_path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            _path,
            JsonSerializer.Serialize(new SheetFile { Sheets = sheets }, JsonOptions),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void Load(string path)
    {
        var file = JsonSerializer.Deserialize<SheetFile>(File.ReadAllText(path), JsonOptions);

        if (file?.Sheets is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var sheet in file.Sheets)
            {
                _sheets[sheet.CharacterId] = sheet;
            }
        }
    }

    private sealed record SheetFile
    {
        public IReadOnlyList<Sheet> Sheets { get; init; } = [];
    }
}
