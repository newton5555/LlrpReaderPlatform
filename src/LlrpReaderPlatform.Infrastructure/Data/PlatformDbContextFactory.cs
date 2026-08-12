using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LlrpReaderPlatform.Infrastructure.Data;

/// <summary>仅供 dotnet ef 使用；运行时数据库路径由 AddLlrpInfrastructure 配置。</summary>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite("Data Source=llrp-reader-platform-design.db")
            .Options;
        return new PlatformDbContext(options);
    }
}
