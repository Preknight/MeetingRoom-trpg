using MeetingRoom.Core.Images;

namespace MeetingRoom.Tests.Images;

/// <summary>图片路径防护：绝对路径、<c>..</c> 穿越都不许跑出 <c>images/</c>。</summary>
public class ImagePathGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "meetingroom-images-guard", "images");

    [Fact]
    public void ResolveWithin_AcceptsRelativePathInsideRoot()
    {
        var full = ImagePathGuard.ResolveWithin(Root, "refs/ab12.png");

        Assert.StartsWith(Path.GetFullPath(Root), full, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ab12.png", full);
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\evil.png")]
    [InlineData("/etc/passwd")]
    public void ResolveWithin_RejectsAbsolutePath(string absolute)
    {
        Assert.Throws<ImagePathException>(() => ImagePathGuard.ResolveWithin(Root, absolute));
    }

    [Theory]
    [InlineData("..\\..\\evil.png")]
    [InlineData("../../evil.png")]
    [InlineData("refs/../../evil.png")]
    public void ResolveWithin_RejectsTraversal(string relative)
    {
        Assert.Throws<ImagePathException>(() => ImagePathGuard.ResolveWithin(Root, relative));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveWithin_RejectsBlankPath(string relative)
    {
        Assert.Throws<ImagePathException>(() => ImagePathGuard.ResolveWithin(Root, relative));
    }

    // 符号链接逃逸那条没写成自动化用例：在 Windows 上建文件符号链接通常需要管理员权限或开发者模式，
    // 会变成"在别人机器上随机失败"的用例。防护本身已实现（ImagePathGuard 的第三条防线），
    // 需要时由人在具备权限的机器上手动验证。
}
