namespace LlrpReaderManager.Services;

public interface IAppWindowService
{
    void EnterHandheldSimulator(int targetClientWidth, int targetClientHeight, int controlBarHeight = 36);
    void RestoreDesktop();
    void ExitApplication();
}
