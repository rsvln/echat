using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EChat.Core.Data;

/// <summary>
/// Used by EF Core tools (dotnet ef migrations) at design time.
/// Not used at runtime.
/// </summary>
public class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseSqlite("Data Source=echat_design.db")
            .Options;
        return new ChatDbContext(options);
    }
}
