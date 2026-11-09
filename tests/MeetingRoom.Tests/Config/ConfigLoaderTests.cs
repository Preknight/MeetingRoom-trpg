using MeetingRoom.Core.Config;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Config;

/// <summary>
/// 配置加载与校验（设计文档 §10.5 / 实现步骤 2.4）：
/// 缺项补默认、未知键记提示、非法值拒绝启动、文件缺失自动生成。
/// </summary>
public sealed class ConfigLoaderTests
{
    [Fact]
    public void FillsMissingValuesWithDefaults()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "server": { "port": 9000 } }""");

        var result = ConfigLoader.Load(file);

        Assert.Equal(9000, result.Options.Server.Port);
        Assert.Equal("127.0.0.1", result.Options.Server.Bind);
        Assert.Equal(15, result.Options.Server.HeartbeatSeconds);
        Assert.Equal(12, result.Options.Limits.MaxStepsPerTurn);
        Assert.Equal(2000, result.Options.Limits.SteerMergeWindowMs);
        Assert.Equal(500, result.Options.Summaries.ThoughtSummaryMaxChars);
        Assert.Equal("paper-abstract", result.Options.Summaries.ThoughtSummaryStyle);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public void GeneratesDefaultFileWhenMissing()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");

        var result = ConfigLoader.Load(file);

        Assert.True(File.Exists(file));
        Assert.Equal(new MeetingRoomOptions(), result.Options);
        Assert.Contains(result.Notices, n => n.Contains("已按内置默认值生成", StringComparison.Ordinal));

        // 生成的默认文件必须能被自己读回——它是用户后续手改的起点。
        var reloaded = ConfigLoader.Load(file);
        Assert.Equal(result.Options, reloaded.Options);
        Assert.Empty(reloaded.Notices);
    }

    [Fact]
    public void ReportsUnknownKeys()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "foo": 1, "server": { "port": 9000, "bar": 2 } }""");

        var result = ConfigLoader.Load(file);

        Assert.Equal(9000, result.Options.Server.Port);
        Assert.Contains("忽略未知键 foo", result.Notices);
        Assert.Contains("忽略未知键 server.bar", result.Notices);
    }

    [Theory]
    [InlineData("""{ "server": { "port": 99999 } }""", "server.port")]
    [InlineData("""{ "server": { "port": -1 } }""", "server.port")]
    [InlineData("""{ "server": { "bind": "0.0.0.0" } }""", "server.bind")]
    [InlineData("""{ "server": { "heartbeatSeconds": 0 } }""", "server.heartbeatSeconds")]
    [InlineData("""{ "limits": { "maxStepsPerTurn": 0 } }""", "limits.maxStepsPerTurn")]
    [InlineData("""{ "summaries": { "thoughtSummaryStyle": "haiku" } }""", "summaries.thoughtSummaryStyle")]
    [InlineData("""{ "provider": { "baseUrl": "not-a-url" } }""", "provider.baseUrl")]
    public void RejectsInvalidValues(string json, string expectedProblem)
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, json);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(file));

        Assert.Contains(ex.Problems, p => p.Contains(expectedProblem, StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsEveryProblemAtOnce()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "server": { "port": 99999 }, "limits": { "maxConcurrentTurns": -2 } }""");

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(file));

        Assert.Contains(ex.Problems, p => p.Contains("server.port", StringComparison.Ordinal));
        Assert.Contains(ex.Problems, p => p.Contains("limits.maxConcurrentTurns", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "server": """);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(file));

        Assert.Contains(ex.Problems, p => p.Contains("不是合法 JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsDynamicPortZero()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "server": { "port": 0 } }""");

        var result = ConfigLoader.Load(file);

        Assert.Equal(0, result.Options.Server.Port);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void AcceptsLoopbackBindings(string bind)
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, $$"""{ "server": { "bind": "{{bind}}" } }""");

        var result = ConfigLoader.Load(file);

        Assert.Equal(bind, result.Options.Server.Bind);
    }

    [Fact]
    public void SpeechLimitAndRoundCapHaveDefaults()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "server": { "port": 9000 } }""");

        var result = ConfigLoader.Load(file);

        Assert.Equal(300, result.Options.Speech.MaxCharsPerSpeech);
        Assert.Equal(5, result.Options.Limits.MaxRounds);
    }

    [Fact]
    public void NegativeSpeechLimitIsRejected()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "speech": { "maxCharsPerSpeech": -1 } }""");

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(file));

        Assert.Contains(ex.Problems, problem => problem.Contains("speech.maxCharsPerSpeech", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "interaction": { "maxConsecutiveSkips": -1 } }""", "interaction.maxConsecutiveSkips")]
    [InlineData("""{ "interaction": { "timeoutSeconds": -5 } }""", "interaction.timeoutSeconds")]
    [InlineData("""{ "interaction": { "maxOptions": 1 } }""", "interaction.maxOptions")]
    [InlineData("""{ "interaction": { "everyRound": false, "allowSelfSkip": false } }""", "interaction.allowSelfSkip")]
    public void RejectsInvalidInteractionValues(string json, string expectedProblem)
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, json);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(file));

        Assert.Contains(ex.Problems, p => p.Contains(expectedProblem, StringComparison.Ordinal));
    }

    [Fact]
    public void InteractionModeDefaultsToAskingEveryRound()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "server": { "port": 9000 } }""");

        var result = ConfigLoader.Load(file);

        Assert.True(result.Options.Interaction.Enabled);
        Assert.True(result.Options.Interaction.EveryRound);
        Assert.True(result.Options.Interaction.AllowSelfSkip);
        Assert.Equal(3, result.Options.Interaction.MaxConsecutiveSkips);
        Assert.Equal(120, result.Options.Interaction.TimeoutSeconds);
        Assert.Equal(4, result.Options.Interaction.MaxOptions);
    }

    [Fact]
    public void ZeroInteractionTimeoutMeansWaitForever()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "interaction": { "timeoutSeconds": 0 } }""");

        var result = ConfigLoader.Load(file);

        Assert.Equal(0, result.Options.Interaction.TimeoutSeconds);
    }

    [Fact]
    public void ForbiddingSelfSkipIsFineWhenEveryRoundStillAsks()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "interaction": { "allowSelfSkip": false, "everyRound": true } }""");

        var result = ConfigLoader.Load(file);

        Assert.False(result.Options.Interaction.AllowSelfSkip);
    }

    [Fact]
    public void CombinedSkipIsAllowedWhenInteractionModeIsOff()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(
            file,
            """{ "interaction": { "enabled": false, "everyRound": false, "allowSelfSkip": false } }""");

        // 交互模式整个关掉时，"两个提问来源都关"就没有矛盾可言了。
        var result = ConfigLoader.Load(file);

        Assert.False(result.Options.Interaction.Enabled);
    }

    [Fact]
    public void ZeroSpeechLimitIsAllowed()
    {
        using var dir = new TempDir();
        var file = dir.File("meetingroom.json");
        File.WriteAllText(file, """{ "speech": { "maxCharsPerSpeech": 0 } }""");

        var result = ConfigLoader.Load(file);

        Assert.Equal(0, result.Options.Speech.MaxCharsPerSpeech);
    }
}
