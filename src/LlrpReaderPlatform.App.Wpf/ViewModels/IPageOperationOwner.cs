namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 页面离开时收口仍在执行的短操作。
/// 长生命周期业务（例如 Inventory）不实现此契约，避免侧栏导航意外停止寻卡。
/// </summary>
public interface IPageOperationOwner
{
    void CancelPendingOperations();
}
