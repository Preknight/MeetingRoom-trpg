using System.Diagnostics;
using System.Text;
using MeetingRoom.Core.Logging;

// 注意命名空间别叫 .System：那会让 System.ComponentModel 之类的裸引用解析到本命名空间下。
namespace MeetingRoom.Host.Shell;

/// <summary>
/// 调**系统原生对话框**选目录 / 选文件（用户 2026-09 要求）。
/// <para>
/// 为什么必须由服务进程来弹：网页里的 <c>&lt;input type="file"&gt;</c> 拿不到绝对路径（浏览器安全限制），
/// <c>showDirectoryPicker()</c> 同样只给 handle——而工作空间要的正是磁盘上的真实路径。
/// 服务是用户自己起的、与他在同一个桌面会话里，所以它弹得出来。
/// </para>
/// <para>
/// 实现走 PowerShell（<c>-EncodedCommand</c> 传脚本，免得给标题里的引号留下注入缝）：
/// 目录用 <c>Shell.Application.BrowseForFolder</c>，文件用 WinForms 的 <c>OpenFileDialog</c>——
/// 这样不必把整个项目改成 <c>net10.0-windows</c>。
/// </para>
/// </summary>
public static class SystemDialogEndpoints
{
    /// <summary>原生窗口等人操作的上限：到点就当用户取消，别把那个 HTTP 请求挂死。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>PowerShell 里"用户取消"的约定退出码。</summary>
    private const int CancelledExitCode = 3;

    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapPost("/api/system/pick-folder", async Task<IResult> (
            PickFolderRequest request,
            FileLog log,
            CancellationToken ct) =>
        {
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $shell = New-Object -ComObject Shell.Application
                $folder = $shell.BrowseForFolder(0, '{{Quote(request.Title ?? "选择一个目录")}}', 0, '{{Quote(request.InitialPath ?? string.Empty)}}')
                if ($null -eq $folder) { exit {{CancelledExitCode}} }
                Write-Output $folder.Self.Path
                """;

            return await RunAsync(script, log, ct).ConfigureAwait(false) is { } paths
                ? Results.Ok(new { path = paths.FirstOrDefault(), cancelled = paths.Count == 0 })
                : DialogFailed();
        });

        app.MapPost("/api/system/pick-files", async Task<IResult> (
            PickFilesRequest request,
            FileLog log,
            CancellationToken ct) =>
        {
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                Add-Type -AssemblyName System.Windows.Forms
                $dialog = New-Object System.Windows.Forms.OpenFileDialog
                $dialog.Title = '{{Quote(request.Title ?? "选择要导入的文件")}}'
                $dialog.Filter = '{{Quote(request.Filter ?? "所有文件 (*.*)|*.*")}}'
                $dialog.Multiselect = $true
                if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit {{CancelledExitCode}} }
                $dialog.FileNames | ForEach-Object { Write-Output $_ }
                """;

            return await RunAsync(script, log, ct).ConfigureAwait(false) is { } paths
                ? Results.Ok(new { paths, cancelled = paths.Count == 0 })
                : DialogFailed();
        });
    }

    /// <summary>返回选中的路径（空列表 = 用户取消）；<c>null</c> = 没跑起来 / 出错。</summary>
    private static async Task<IReadOnlyList<string>?> RunAsync(string script, FileLog log, CancellationToken ct)
    {
        var start = new ProcessStartInfo("powershell")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-STA");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        using var process = Process.Start(start);

        if (process is null)
        {
            log.Warn("系统对话框：PowerShell 没起来。");
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);

        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            if (process.ExitCode == CancelledExitCode)
            {
                log.Info("系统对话框：用户取消。");
                return [];
            }

            if (process.ExitCode != 0)
            {
                log.Warn($"系统对话框失败（exit {process.ExitCode}）：{error.Trim()}");
                return null;
            }

            var paths = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            log.Info($"系统对话框选中 {paths.Length} 项：{string.Join("；", paths)}");
            return paths;
        }
        catch (OperationCanceledException)
        {
            log.Warn("系统对话框：等超时了（可能没有桌面会话可弹窗）。");
            TryKill(process);
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 已经退了或杀不动：无所谓。
        }
    }

    private static IResult DialogFailed() =>
        Results.Problem(detail: "调系统对话框失败（超时、没有桌面会话，或这台机器没有 PowerShell）。", statusCode: 500);

    /// <summary>塞进 PowerShell 单引号字符串：单引号双写，其余照旧。</summary>
    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

/// <summary>选目录。</summary>
public sealed record PickFolderRequest(string? Title = null, string? InitialPath = null);

/// <summary>选文件（可多选）。<c>Filter</c> 用 WinForms 的写法：<c>文本 (*.md)|*.md|所有文件 (*.*)|*.*</c>。</summary>
public sealed record PickFilesRequest(string? Title = null, string? Filter = null);
