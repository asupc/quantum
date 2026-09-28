using Microsoft.EntityFrameworkCore;
using Quantum.Application;
using Quantum.Entities.Model;

namespace Quantum.API.Tests;

public sealed class ChannelMenuTests
{
    [Fact]
    public async Task ExistingMenuGetsChannelManagementEntryOnce_WithoutResettingCustomMenus()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            db.Menus.Add(new MenuModel { Name = "settings", Component = "Main", Path = "/settings", Title = "系统管理" });
            db.Menus.Add(new MenuModel { Name = "own-menu", Component = "navigation/index", Path = "/own", Title = "自定义" });
            await db.SaveChangesAsync();
            var service = new MenuService(db);
            await service.GetAsync();
            await service.GetAsync();
            Assert.Equal(1, await db.Menus.CountAsync(x => x.Component == "channel/index"));
            Assert.Equal(1, await db.Menus.CountAsync(x => x.Name == "own-menu"));
            Assert.Equal("settings", (await db.Menus.SingleAsync(x => x.Component == "channel/index")).ParentName);
        }
    }
}
