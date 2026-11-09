using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 世界状态结算（设计文档 §11.4-6）：程序校验、整次原子、失败不回滚正文。
/// </summary>
public sealed class WorldStateStoreTests
{
    [Fact]
    public void ValidMutationAppliesAndBumpsVersion()
    {
        var store = Store();

        var result = store.Apply(new WorldStateMutation
        {
            Time = "第三日黄昏",
            Place = "钟楼",
            Enter = ["ran"],
            Attributes = [new AttributeDelta("ran", "信任", 2)],
            Items = [new ItemDelta("ran", "铜钥匙")],
            Knowledge = [new KnowledgeDelta("ran", "钟楼下有暗门")],
        });

        Assert.True(result.Applied);
        Assert.Equal(1, result.State.Version);
        Assert.Equal("第三日黄昏", result.State.Time);
        Assert.Equal("钟楼", result.State.Place);
        Assert.Contains("ran", result.State.Present);
        Assert.Equal(2, result.State.Characters["ran"].Attributes["信任"]);
        Assert.Contains("铜钥匙", result.State.Characters["ran"].Items);
        Assert.Contains("钟楼下有暗门", result.State.Characters["ran"].Known);
    }

    [Fact]
    public void UnknownCharacterIsRejectedAndStateIsUntouched()
    {
        var store = Store();
        var before = store.Current;

        var result = store.Apply(new WorldStateMutation
        {
            Attributes = [new AttributeDelta("ghost", "信任", 1)],
        });

        Assert.False(result.Applied);
        Assert.Contains(result.Errors, error => error.Contains("未知角色", StringComparison.Ordinal));
        Assert.Equal(0, store.Version);
        Assert.Equal(before.Characters.Count, store.Current.Characters.Count);
    }

    [Fact]
    public void OutOfRangeAttributeIsRejected()
    {
        var store = Store();

        var result = store.Apply(new WorldStateMutation
        {
            Attributes = [new AttributeDelta("ella", "信任", 500)],
        });

        Assert.False(result.Applied);
        Assert.Contains(result.Errors, error => error.Contains("超出", StringComparison.Ordinal));
        Assert.Equal(0, store.Version);
    }

    [Fact]
    public void RemovingAnItemThatWasNeverHeldIsRejected()
    {
        var store = Store();

        var result = store.Apply(new WorldStateMutation
        {
            Items = [new ItemDelta("ella", "并不存在的剑", Remove: true)],
        });

        Assert.False(result.Applied);
        Assert.Contains(result.Errors, error => error.Contains("并不持有", StringComparison.Ordinal));
    }

    [Fact]
    public void OneBadDeltaRejectsTheWholeSettlement()
    {
        var store = Store();

        var result = store.Apply(new WorldStateMutation
        {
            Time = "第四日",
            Attributes =
            [
                new AttributeDelta("ella", "信任", 1),
                new AttributeDelta("ran", "信任", 1000),
            ],
        });

        // 原子性：合法的那条也不生效（否则会出现"半结算"的世界）。
        Assert.False(result.Applied);
        Assert.Equal("第一日清晨", store.Current.Time);
        Assert.Equal(0, store.Version);
    }

    [Fact]
    public void KnowledgeOnlyGrows()
    {
        var store = Store();

        store.Apply(new WorldStateMutation { Knowledge = [new KnowledgeDelta("ella", "钥匙是真的")] });
        store.Apply(new WorldStateMutation { Knowledge = [new KnowledgeDelta("ella", "钥匙是真的")] });

        Assert.Single(store.Current.Characters["ella"].Known);
    }

    [Fact]
    public void SnapshotRespectsMaxCharsAndListsPresence()
    {
        var store = Store();

        store.Apply(new WorldStateMutation { Time = "第三日黄昏", Place = "钟楼", Leave = ["ran"] });

        var snapshot = store.Snapshot(maxChars: 200);

        Assert.Contains("时间：第三日黄昏", snapshot, StringComparison.Ordinal);
        Assert.Contains("地点：钟楼", snapshot, StringComparison.Ordinal);
        Assert.Contains("艾拉（在场）", snapshot, StringComparison.Ordinal);
        Assert.Contains("冉（不在场）", snapshot, StringComparison.Ordinal);

        var tiny = store.Snapshot(maxChars: 12);
        Assert.Equal(13, tiny.Length);
    }

    // ——— 名字 → id 归一（§7.4 修的那条：模型给名字、内部按 id 存） ———

    [Fact]
    public void NamesAreResolvedForEveryReferenceField()
    {
        var store = Store();

        var result = store.Apply(new WorldStateMutation
        {
            Enter = ["冉"],
            Leave = ["艾拉"],
            Attributes = [new AttributeDelta("冉", "信任", 2)],
            Items = [new ItemDelta("冉", "铜钥匙")],
            Knowledge = [new KnowledgeDelta("冉", "钟楼下有暗门")],
        });

        Assert.True(result.Applied, string.Join("；", result.Errors));

        // 五个字段全部用名字提交，全部落到 id 上。
        Assert.Contains("ran", result.State.Present);
        Assert.DoesNotContain("ella", result.State.Present);
        Assert.Equal(2, result.State.Characters["ran"].Attributes["信任"]);
        Assert.Contains("铜钥匙", result.State.Characters["ran"].Items);
        Assert.Contains("钟楼下有暗门", result.State.Characters["ran"].Known);

        // 归一不动存储：id 与名字都保持原样。
        Assert.Equal("冉", result.State.Characters["ran"].Name);
        Assert.Equal("ella", result.State.Characters["ella"].Id);
    }

    [Fact]
    public void IdsStillWinOverNames()
    {
        // a 的 id 是 detective、名字是「侦探」；b 的名字恰好等于 a 的 id。
        var store = new WorldStateStore(
        [
            new CharacterState("detective", "侦探"),
            new CharacterState("b", "detective"),
        ]);

        var result = store.Apply(new WorldStateMutation
        {
            Attributes = [new AttributeDelta("detective", "信任", 1)],
        });

        Assert.True(result.Applied, string.Join("；", result.Errors));

        // id 按 Ordinal 命中优先：落到 id="detective" 那个，不是名字叫 detective 的 b。
        Assert.Equal(1, result.State.Characters["detective"].Attributes["信任"]);
        Assert.Empty(result.State.Characters["b"].Attributes);
    }

    [Fact]
    public void AmbiguousNamesAreRejectedWithTheKnownList()
    {
        var store = new WorldStateStore(
        [
            new CharacterState("detective-a", "侦探"),
            new CharacterState("detective-b", "侦探"),
        ]);
        var before = store.Current;

        var result = store.Apply(new WorldStateMutation
        {
            Attributes = [new AttributeDelta("侦探", "信任", 1)],
        });

        Assert.False(result.Applied);
        Assert.Contains(result.Errors, error => error.Contains("请用 id", StringComparison.Ordinal));
        Assert.Contains(
            result.Errors,
            error => error.Contains("已知角色：detective-a（侦探）、detective-b（侦探）", StringComparison.Ordinal));

        // 重名不猜：整次不生效，状态对象原封不动。
        Assert.Equal(0, store.Version);
        Assert.Same(before, store.Current);
    }

    [Fact]
    public void UnknownNamesKeepTheOriginalMessage()
    {
        var store = Store();

        var result = store.Apply(new WorldStateMutation
        {
            Attributes = [new AttributeDelta("陌生人", "信任", 1)],
        });

        Assert.False(result.Applied);

        // 逐字钉住：归一不改变"未知角色"那条文案（前端与日志都按它写过）。
        Assert.Equal(["未知角色：陌生人"], result.Errors);
    }

    [Fact]
    public void EnglishNamesAreMatchedCaseInsensitively()
    {
        var store = new WorldStateStore([new CharacterState("ella", "Ella")]);

        var result = store.Apply(new WorldStateMutation
        {
            Knowledge = [new KnowledgeDelta("ELLA", "地窖里有人")],
        });

        Assert.True(result.Applied, string.Join("；", result.Errors));
        Assert.Contains("地窖里有人", result.State.Characters["ella"].Known);
    }

    private static WorldStateStore Store() => new(
    [
        new CharacterState("ella", "艾拉"),
        new CharacterState("ran", "冉"),
    ],
    time: "第一日清晨",
    place: "小镇广场");
}
