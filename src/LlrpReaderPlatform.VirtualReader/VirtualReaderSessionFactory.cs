using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Sdk;

namespace LlrpReaderPlatform.VirtualReader;

/// <summary>把 ReaderProfile 映射到已加载的虚拟场景。</summary>
public sealed class VirtualReaderSessionFactory(VirtualReaderCatalog catalog) : IReaderSessionFactory
{
    public IReaderSession Create(
        ReaderProfile profile,
        IReadOnlyList<IReaderExtensionModule>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new VirtualReaderSession(
            catalog.GetRequired(profile.Id),
            catalog.GetOrCreateState(profile.Id));
    }
}
