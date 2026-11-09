using System.Net;
using MeetingRoom.Core.Config;

namespace MeetingRoom.Core.Images;

/// <summary>
/// 图片出网的 HTTP 设置（设计方案 §七）。特意抽成**可调用**的公开方法而不是藏在生成器里：
/// 代理三态是配置里最容易写错的一处，而一个 <see cref="SocketsHttpHandler"/> 从外面观察不到 ——
/// 只有把它暴露出来，测试才能断言"off 真的直连、空真的走默认、URL 真的设上了"。
/// </summary>
public static class ImageHttp
{
    /// <summary>
    /// 代理三态：
    /// <list type="bullet">
    ///   <item><c>""</c>（默认）—— 什么都不设：走 .NET 默认，即先看环境变量（<c>HTTP_PROXY</c> 等），
    ///         未设时回退当前用户的 WinINET/IE 设置 —— <b>系统里配了 PAC 就会自动用上</b>；</item>
    ///   <item><c>off</c> —— 直连（<c>UseProxy = false</c>）；</item>
    ///   <item>其余 —— 显式代理 URL（如 <c>http://127.0.0.1:10828</c>）。</item>
    /// </list>
    /// </summary>
    public static SocketsHttpHandler CreateHandler(ImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new SocketsHttpHandler();
        var proxy = options.Proxy.Trim();

        if (proxy.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            handler.UseProxy = false;
        }
        else if (proxy.Length > 0)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return handler;
    }
}
