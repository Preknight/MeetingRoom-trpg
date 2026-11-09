using MeetingRoom.Core.Logging;
using Microsoft.Extensions.Logging;

namespace MeetingRoom.Host.Logging;

/// <summary>
/// 把 ASP.NET Core 的日志管道接到 <see cref="FileLog"/>。
/// <para>
/// 为什么值得接：框架自己就会记"哪个请求进来了、以什么状态码结束"以及未处理异常——
/// 排错时最需要的正是这几条（待办 A6 的起因就是"双击 exe 起服务，窗口一关什么都查不到"）。
/// </para>
/// </summary>
public sealed class FileLogProvider : ILoggerProvider
{
    private readonly FileLog _log;

    public FileLogProvider(FileLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_log, categoryName);

    public void Dispose()
    {
        // 日志对象由 Host 持有并释放（这里不抢所有权）。
    }

    /// <summary>
    /// 只留"有用且不吵"的那部分框架日志：<b>Warning 及以上全留</b>；Information 只留请求行 / 状态码与
    /// 生命周期（监听地址、启动、停止）——路由、结果类型、"Writing value of type ..." 这类内部噪声
    /// 会把文件淹掉，真要看细节的人可以再用别的手段。
    /// </summary>
    private static readonly string[] InformativeCategories =
    [
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.Hosting.Lifetime",
    ];

    private sealed class FileLogger(FileLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning
            || (logLevel >= LogLevel.Information && IsInformative(category));

        private static bool IsInformative(string name) =>
            InformativeCategories.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);

            if (exception is not null)
            {
                message = message + Environment.NewLine + exception;
            }

            log.Write(Level(logLevel), $"{category}: {message}");
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            LogLevel.Trace or LogLevel.Debug => "DEBUG",
            _ => "INFO ",
        };
    }
}
