namespace LlrpReaderPlatform.Contracts.Persistence;

/// <summary>应用级键值设置，值由具体功能自行版本化/序列化。</summary>
public interface IAppSettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
}
