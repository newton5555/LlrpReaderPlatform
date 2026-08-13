namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>关于页（对齐旧 AboutViewModel）。静态字段，无服务依赖。</summary>
public sealed record AboutViewModel
{
    public string AppName => "LLRP Reader Platform";
    public string Version => $"{typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown"} (Net10.0 WPF)";
    public string Description => "厂商无关的 LLRP 应用框架与 WPF 消费者。";
    public string LicenseNotice => "非 Impinj 官方软件；仅用于标准 LLRP 与已验收的厂商扩展。";
}
