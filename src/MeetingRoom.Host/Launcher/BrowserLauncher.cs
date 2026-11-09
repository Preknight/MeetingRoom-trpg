using System.Diagnostics;

namespace MeetingRoom.Host.Launcher;

/// <summary>
/// 打开界面窗口（实现步骤 2.5 + F5 全屏）。
/// <para>
/// 用户要求"窗口默认全屏"：优先用 **Edge / Chrome 的 app 模式**
/// （<c>--app=&lt;url&gt; --start-fullscreen --no-first-run</c>）——它没有地址栏，像个桌面应用。
/// 找不到这两个浏览器就**默默回退**到系统默认浏览器，绝不因为"想全屏"让程序起不来。
/// </para>
/// </summary>
public static class BrowserLauncher
{
    /// <summary>app 模式下额外加的启动参数（顺序无关）。</summary>
    private static readonly string[] AppArguments = ["--start-fullscreen", "--no-first-run", "--no-default-browser-check"];

    /// <summary>
    /// 打开给定地址。必须在 <c>ApplicationStarted</c> 之后调用——
    /// 端口尚未绑定就打开，浏览器会打到空页面。
    /// </summary>
    /// <param name="url">要打开的地址。</param>
    /// <param name="launchMode">配置里的 <c>ui.launchMode</c>：<c>app</c>（默认）或 <c>browser</c>。</param>
    public static void Open(string url, string? launchMode = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var mode = string.IsNullOrWhiteSpace(launchMode) ? "app" : launchMode.Trim();

        if (!string.Equals(mode, "app", StringComparison.OrdinalIgnoreCase))
        {
            OpenWithDefaultBrowser(url);
            return;
        }

        var browser = FindChromiumBrowser();

        if (browser is null)
        {
            // 没有 Edge / Chrome：安静地回退（用户至少还能用界面）。
            OpenWithDefaultBrowser(url);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(browser) { UseShellExecute = false };
            startInfo.ArgumentList.Add($"--app={url}");

            foreach (var argument in AppArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 浏览器路径存在但起不来（权限 / 被策略拦）：同样回退，不抛。
            OpenWithDefaultBrowser(url);
        }
    }

    /// <summary>
    /// 组装 app 模式的完整命令行（诊断用：<c>--probe-launcher</c> 要把"将要执行什么"打出来）。
    /// 找不到浏览器时返回 <c>null</c>。
    /// </summary>
    public static string? DescribeAppCommand(string url)
    {
        var browser = FindChromiumBrowser();

        if (browser is null)
        {
            return null;
        }

        var arguments = new[] { $"--app={url}" }.Concat(AppArguments);
        return $"{browser} {string.Join(' ', arguments)}";
    }

    /// <summary>
    /// 按常见安装位置找 Edge / Chrome；**只返回存在的路径**，找不到返回 <c>null</c>。
    /// 顺序：Edge（Program Files (x86) → Program Files）→ Chrome（LocalAppData → Program Files）。
    /// </summary>
    public static string? FindChromiumBrowser()
    {
        var candidates = new List<string>();

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            }
        }

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"));
        }

        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void OpenWithDefaultBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
