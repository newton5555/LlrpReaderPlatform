namespace LlrpReaderManager.Services;

public interface IAppWindowService
{
    void ResizeToMobile(int contentWidth, int contentHeight);
    void RestoreToDesktop();
}
