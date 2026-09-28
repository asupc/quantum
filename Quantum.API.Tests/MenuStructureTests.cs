using Microsoft.EntityFrameworkCore;
using Quantum.Application;
using Quantum.Entities.Model;

namespace Quantum.API.Tests;

/// <summary>
/// 菜单按功能重排后的结构约束：入口归属与「重置菜单不恢复隐藏显示状态」的行为。
/// </summary>
public sealed class MenuStructureTests
{
    [Fact]
    public async Task ChannelEntryPrefersMessageGroupOverSettings()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            db.Menus.Add(new MenuModel { Name = "chat", Component = "Main", Path = "/chat", Title = "消息中心" });
            db.Menus.Add(new MenuModel { Name = "settings", Component = "Main", Path = "/settings", Title = "系统管理" });
            await db.SaveChangesAsync();

            var service = new MenuService(db);
            await service.GetAsync();
            await service.GetAsync();

            var channel = await db.Menus.SingleAsync(x => x.Component == "channel/index");
            Assert.Equal("chat", channel.ParentName);
            // 补入的入口用绝对路径，保证换组后深链与既有页签地址不变
            Assert.Equal("/settings/channel", channel.Path);
            Assert.Equal(1, await db.Menus.CountAsync(x => x.Name == "chat"));
        }
    }

    [Fact]
    public async Task ReseedAsync_KeepsHiddenItemsHidden_AndDoesNotHideVisibleOnes()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            db.Menus.Add(new MenuModel { Name = "docker-index", Component = "docker/index", Path = "/docker/index", Title = "概览", HideInMenu = true });
            db.Menus.Add(new MenuModel { Name = "settings-index", Component = "setting/index", Path = "/settings/index", Title = "系统设置", HideInMenu = false });
            await db.SaveChangesAsync();

            await new MenuService(db).ReseedAsync();

            Assert.True((await db.Menus.SingleAsync(x => x.Name == "docker-index")).HideInMenu);
            Assert.False((await db.Menus.SingleAsync(x => x.Name == "settings-index")).HideInMenu);
        }
    }

    [Fact]
    public async Task ReseedAsync_BuildsFunctionGroupsWithoutDuplicatingLeafPaths()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            await new MenuService(db).ReseedAsync();
            var all = await db.Menus.ToListAsync();

            // 顶级只保留按功能划分的分组，环境变量/快捷回复/系统日志不再各自成组
            var roots = all.Where(x => string.IsNullOrEmpty(x.ParentName)).Select(x => x.Name).ToList();
            Assert.DoesNotContain("env", roots);
            Assert.DoesNotContain("replay", roots);
            Assert.DoesNotContain("logs", roots);
            Assert.Contains("chat", roots);
            Assert.Contains("task", roots);

            // 换组后叶子**完整路径**不变：与 Vue Router 同语义——子路径以 '/' 开头即为绝对地址，不再拼父级
            string FullPath(MenuModel m) => m.Path.StartsWith('/') ? m.Path
                : (string.IsNullOrEmpty(m.ParentName) ? m.Path : FullPath(all.Single(x => x.Name == m.ParentName)) + "/" + m.Path);
            var full = all.Where(x => x.Component != "Main").Select(FullPath).ToList();
            Assert.Equal(full.Count, full.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("/env/index", full);
            Assert.Contains("/replay/index", full);
            Assert.Contains("/logs/index", full);
            Assert.Contains("/settings/channel", full);
            Assert.Contains("/external-push/index", full);
            Assert.Contains("/chat/index", full);
            Assert.Contains("/task/index", full);
            Assert.Contains("/settings/index", full);

            // 自定义数据子菜单仍由 CustomDataTitle 联动生成到「数据管理」下
            db.CustomDataTitles.Add(new CustomDataTitleModel { Type = "unit_probe", TypeName = "单测探针" });
            await db.SaveChangesAsync();
            await new MenuService(db).ReseedAsync();
            var probe = await db.Menus.SingleAsync(x => x.Name == "单测探针");
            Assert.Equal("customData", probe.ParentName);
            Assert.Equal("/custom-data/unit_probe", probe.Path);
        }
    }
}
