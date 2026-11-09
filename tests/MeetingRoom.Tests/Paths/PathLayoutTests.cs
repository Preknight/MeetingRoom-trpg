using MeetingRoom.Core.Paths;

namespace MeetingRoom.Tests.Paths;

/// <summary>
/// 路径基准（实现步骤 2.3）：所有运行期路径都由 exe 所在目录派生，
/// 不用 %APPDATA%、系统临时目录或当前工作目录。
/// </summary>
public sealed class PathLayoutTests
{
    [Fact]
    public void InstallDirIsProcessPathDirectory()
    {
        Assert.True(Path.IsPathRooted(PathLayout.InstallDir));
        Assert.Equal(Path.GetDirectoryName(Environment.ProcessPath), PathLayout.InstallDir);
    }

    [Fact]
    public void EveryRuntimePathStaysUnderInstallDir()
    {
        var paths = new (string Name, string Value)[]
        {
            ("wwwroot", PathLayout.WwwRoot),
            ("meetingroom.json", PathLayout.ConfigFile),
            ("data", PathLayout.DataDir),
            ("data/roles", PathLayout.RolesDir),
            ("data/meetings", PathLayout.MeetingsDir),
            ("logs", PathLayout.LogsDir),
        };

        foreach (var (name, value) in paths)
        {
            Assert.StartsWith(PathLayout.InstallDir + Path.DirectorySeparatorChar, value, StringComparison.Ordinal);
            Assert.DoesNotContain("APPDATA", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Temp", value, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
    }

    [Fact]
    public void ConfigFileSitsNextToExecutable()
    {
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(PathLayout.ConfigFile)!, "meetingroom.json"),
            PathLayout.ConfigFile);
        Assert.Equal(PathLayout.InstallDir, Path.GetDirectoryName(PathLayout.ConfigFile));
    }
}
