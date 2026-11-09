using MeetingRoom.Core.Logging;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Logging;

/// <summary>
/// 文件日志（设计文档 §10.1 / 待办 A6）：落 exe 同级的 <c>logs/</c>、按天一个文件、一行一条、
/// 写不进去也不把程序带崩。
/// </summary>
public sealed class FileLogTests
{
    [Fact]
    public void WritesOneLinePerMessage()
    {
        using var temp = new TempDir();
        using var log = new FileLog(temp.File("logs"));

        log.Info("开会：议题=定价");

        var line = Assert.Single(File.ReadAllLines(log.CurrentPath));

        Assert.Contains("INFO ", line, StringComparison.Ordinal);
        Assert.Contains("开会：议题=定价", line, StringComparison.Ordinal);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INFO ", line);
    }

    [Fact]
    public void MultiLineMessagesStayOnOneLine()
    {
        using var temp = new TempDir();
        using var log = new FileLog(temp.File("logs"));

        log.Error("复制失败", new InvalidOperationException("文件被占用"));

        var lines = File.ReadAllLines(log.CurrentPath);

        // 一条记录占一个物理行：消息里的换行被转义成字面 \n，ReadAllLines 只数出一行。
        Assert.Single(lines);
        Assert.Contains("ERROR", lines[0], StringComparison.Ordinal);
        Assert.Contains("文件被占用", lines[0], StringComparison.Ordinal);
        Assert.Contains(@"\n", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentWritesDoNotLoseLines()
    {
        using var temp = new TempDir();
        using var log = new FileLog(temp.File("logs"));

        Parallel.For(0, 200, index => log.Info($"第 {index} 条"));

        Assert.Equal(200, File.ReadAllLines(log.CurrentPath).Length);
    }

    [Fact]
    public void ItCreatesTheDirectoryOnDemand()
    {
        using var temp = new TempDir();
        var directory = temp.File(Path.Combine("还不存在", "logs"));

        using var log = new FileLog(directory);
        log.Info("第一次写入就把目录建出来");

        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(log.CurrentPath));
    }

    [Fact]
    public void OldFilesAreTrimmedToTheKeepCount()
    {
        using var temp = new TempDir();
        var directory = temp.File("logs");
        Directory.CreateDirectory(directory);

        foreach (var day in new[] { "2020-01-01", "2020-01-02", "2020-01-03" })
        {
            File.WriteAllText(Path.Combine(directory, $"meetingroom-{day}.log"), "旧的");
        }

        // 只留 2 个：今天那个 + 一个旧的；更旧的被清掉。
        using var log = new FileLog(directory, keepFiles: 2);
        log.Info("触发一次写入（换文件时才清理）");

        var kept = Directory
            .GetFiles(directory, "meetingroom-*.log")
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, kept.Length);
        Assert.DoesNotContain("meetingroom-2020-01-01.log", kept);
        Assert.Contains("meetingroom-2020-01-03.log", kept);
    }

    [Fact]
    public void AnUnwritableDirectoryDoesNotThrow()
    {
        // 目录名撞到一个已存在的**文件**：建目录会失败，日志该吞掉而不是把程序带崩。
        using var temp = new TempDir();
        var blocked = temp.File("logs");
        File.WriteAllText(blocked, "我不是目录");

        using var log = new FileLog(blocked);

        log.Info("这条写不进去，但不该抛");
    }
}
