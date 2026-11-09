using System.Globalization;
using System.Text;

namespace MeetingRoom.Core.Logging;

/// <summary>
/// 文件日志（设计文档 §10.1 的 <c>logs/</c>，待办 A6）：**exe 同级**、按天一个文件、append-only。
/// <para>
/// 为什么需要它：控制台只在"有人看着那个窗口"时才算日志——从资源管理器双击 exe 起服务，窗口一关就什么都查不到
/// （2026-09 用户从 <c>output/</c> 跑时抓到）。排错必须看文件。
/// </para>
/// <para>
/// 落点固定为 exe 同级，**不提供配置覆盖项**（§10.1，2026-09 用户拍板）——与原则 1 一致：
/// "整目录拷走就能用"也包括日志。
/// </para>
/// </summary>
public sealed class FileLog : IDisposable
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>不带 BOM 的 UTF-8：日志第一行不该带一个隐形字符（脚本与 <c>rg</c> 都要能直接匹配）。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly string _prefix;
    private readonly int _keepFiles;

    private string? _openedFile;
    private bool _disposed;

    public FileLog(string directory, string filePrefix = "meetingroom", int keepFiles = 14)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);

        Directory = Path.GetFullPath(directory);
        _prefix = filePrefix;
        _keepFiles = Math.Max(1, keepFiles);
    }

    /// <summary>日志目录（exe 同级的 <c>logs/</c>）。</summary>
    public string Directory { get; }

    /// <summary>今天这个日志文件的完整路径。</summary>
    public string CurrentPath => Path.Combine(Directory, $"{_prefix}-{DateTimeOffset.Now:yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception exception) =>
        Write("ERROR", message + Environment.NewLine + exception);

    /// <summary>
    /// 写一条。**每次都开关句柄**，不留长句柄——用户随时能用记事本打开看今天的日志
    /// （长句柄会撞上 Windows 的共享语义，见风险 9 的同一族坑；日志频率低，这点开销无所谓）。
    /// </summary>
    public void Write(string level, string message)
    {
        if (_disposed)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)} {level} {OneLine(message)}";

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var path = CurrentPath;
                var newDay = !string.Equals(path, _openedFile, StringComparison.OrdinalIgnoreCase);

                File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);

                // 换天（或首次写入）：写完之后再清理——这样"保留 N 个"把今天这个也算进去。
                if (newDay)
                {
                    _openedFile = path;
                    Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写不进去（磁盘满 / 目录只读）不该把程序带崩——宁可少日志，不可少功能。
            }
        }
    }

    /// <summary>
    /// 消息里的换行一律转义成字面 <c>\n</c>：一条记录**占一个物理行**——用记事本 / <c>rg</c> 扫日志时
    /// 一眼一条，异常堆栈也不会散成几十行。
    /// </summary>
    private static string OneLine(string message) =>
        string.IsNullOrEmpty(message) ? string.Empty : message.ReplaceLineEndings("\\n").TrimEnd();

    /// <summary>只保留最近 <paramref name="keepFiles"/> 个日志文件（文件名里带日期，按名字倒序即可）。</summary>
    private void Trim()
    {
        try
        {
            var expired = System.IO.Directory
                .GetFiles(Directory, $"{_prefix}-*.log")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Skip(_keepFiles)
                .ToArray();

            foreach (var file in expired)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响写入。
        }
    }

    public void Dispose() => _disposed = true;
}
