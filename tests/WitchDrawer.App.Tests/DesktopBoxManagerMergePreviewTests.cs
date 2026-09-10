using System.Windows;
using WitchDrawer.App.Infrastructure;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 锁定「拖动盒重叠触发标签合并预览」的中心点判定规则：
/// 被拖盒的中心点落入某个候选盒的可见边界内，即视为合并宿主候选。
/// </summary>
public sealed class DesktopBoxManagerMergePreviewTests
{
    [Fact]
    public void FindMergePreviewCandidateId_ReturnsBoxWhoseBoundsContainDraggedCenter()
    {
        var dragged = new Rect(100, 100, 80, 60);
        var host = (Guid.NewGuid(), new Rect(120, 120, 160, 120));
        var other = (Guid.NewGuid(), new Rect(1000, 800, 120, 90));

        var result = DesktopBoxManager.FindMergePreviewCandidateId(dragged, [host, other]);

        Assert.Equal(host.Item1, result);
    }

    [Fact]
    public void FindMergePreviewCandidateId_ReturnsNullWhenCenterNotInsideAnyCandidate()
    {
        var dragged = new Rect(100, 100, 80, 60);
        var candidates = new[]
        {
            (Guid.NewGuid(), new Rect(400, 400, 120, 90)),
            (Guid.NewGuid(), new Rect(800, 300, 140, 100))
        };

        var result = DesktopBoxManager.FindMergePreviewCandidateId(dragged, candidates);

        Assert.Null(result);
    }

    [Fact]
    public void FindMergePreviewCandidateId_IgnoresEmptyBounds()
    {
        var dragged = new Rect(100, 100, 80, 60);
        var emptyBounds = Rect.Empty;
        var host = (Guid.NewGuid(), new Rect(120, 120, 160, 120));

        var result = DesktopBoxManager.FindMergePreviewCandidateId(
            dragged,
            [(Guid.NewGuid(), emptyBounds), host]);

        Assert.Equal(host.Item1, result);
    }

    [Fact]
    public void FindMergePreviewCandidateId_UsesCenterPointNotCorner()
    {
        // 被拖盒左上角在候选内、但中心点在候选外 → 不命中（避免轻微贴合即误合并）。
        var dragged = new Rect(360, 230, 120, 80);
        var candidate = new Rect(340, 120, 160, 120);

        var result = DesktopBoxManager.FindMergePreviewCandidateId(
            dragged,
            [(Guid.NewGuid(), candidate)]);

        Assert.Null(result);
    }
}