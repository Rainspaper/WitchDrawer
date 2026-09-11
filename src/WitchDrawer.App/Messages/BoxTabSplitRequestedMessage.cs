namespace WitchDrawer.App.Messages;

/// <summary>请求把成员盒从标签宿主中拆出为独立盒子。</summary>
public sealed record BoxTabSplitRequestedMessage(Guid MemberId);
