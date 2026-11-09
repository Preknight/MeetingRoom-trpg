using MeetingRoom.Core.Context;

namespace MeetingRoom.Tests.Context;

/// <summary>缓存诊断（设计文档 §7.4）：命中/未命中与"上次 miss 的原因"要能被读到。</summary>
public sealed class CacheDiagnosticsTests
{
    [Fact]
    public void ReportsHitRateAndMissReasonPerParticipant()
    {
        var diagnostics = new CacheDiagnostics();

        diagnostics.Record("product-manager", "aaaa1111", "role-1", cacheHitTokens: 0, cacheMissTokens: 1200, lastMissReason: "首次请求");
        diagnostics.Record("product-manager", "aaaa1111", "role-1", cacheHitTokens: 900, cacheMissTokens: 300, lastMissReason: null);
        diagnostics.Record("engineer", "bbbb2222", "role-2", cacheHitTokens: 800, cacheMissTokens: 100, lastMissReason: "steer");

        var report = diagnostics.Report();

        Assert.Equal(2, report.Count);

        var productManager = Assert.Single(report, item => item.ParticipantId == "product-manager");
        Assert.Equal(2, productManager.Turns);
        Assert.Equal(900, productManager.CacheHitTokens);
        Assert.Equal(300, productManager.CacheMissTokens);
        Assert.True(productManager.HitRate > 0.7);
        Assert.Null(productManager.LastMissReason);

        var engineer = Assert.Single(report, item => item.ParticipantId == "engineer");
        Assert.Equal("steer", engineer.LastMissReason);
    }
}
