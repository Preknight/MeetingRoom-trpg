namespace MeetingRoom.Tests.Support;

/// <summary>
/// 测试临时目录：落在测试程序集输出目录（bin/）之下，不落到系统临时目录或工作区之外，
/// Dispose 时整目录删除。bin/ 已被 .gitignore 忽略。
/// </summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = System.IO.Path.Combine(AppContext.BaseDirectory, "_test-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => System.IO.Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论。
        }
    }
}
