using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 书签管理控制器
/// </summary>
[CustomAuthorizationFilter]
public class BookmarkController : BaseController
{
    private readonly BookmarkService _bookmarkService;

    public BookmarkController(BookmarkService bookmarkService)
    {
        _bookmarkService = bookmarkService;
    }

    /// <summary>
    /// 获取所有书签（支持分页和搜索）
    /// </summary>
    /// <param name="page">页码</param>
    /// <param name="pageSize">每页数量</param>
    /// <param name="search">搜索关键字</param>
    /// <returns>书签列表</returns>
    [HttpGet]
    public Task<PageResult<BookmarkDto>> GetBookmarks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string search = "")
    {
        return _bookmarkService.GetBookmarks(page, pageSize, search);
    }

    /// <summary>
    /// 根据ID获取书签详情
    /// </summary>
    /// <param name="id">书签ID</param>
    /// <returns>书签详情</returns>
    [HttpGet("{id}")]
    public Task<BookmarkDto> GetBookmark(string id)
    {
        return _bookmarkService.GetBookmark(id);
    }

    /// <summary>
    /// 创建新书签
    /// </summary>
    /// <param name="bookmarkDto">书签信息</param>
    /// <returns>创建的书签</returns>
    [HttpPost]
    public Task<BookmarkDto> CreateBookmark(BookmarkDto bookmarkDto)
    {
        return _bookmarkService.CreateBookmark(bookmarkDto);
    }

    /// <summary>
    /// 更新书签
    /// </summary>
    /// <param name="id">书签ID</param>
    /// <param name="bookmarkDto">更新的书签信息</param>
    /// <returns>无内容</returns>
    [HttpPut("{id}")]
    public Task<bool> UpdateBookmark(string id, BookmarkDto bookmarkDto)
    {
        return _bookmarkService.UpdateBookmark(id, bookmarkDto);
    }

    /// <summary>
    /// 删除书签
    /// </summary>
    /// <param name="id">书签ID</param>
    [HttpDelete("{id}")]
    public Task<bool> DeleteBookmark(string id)
    {
        return _bookmarkService.DeleteBookmark(id);
    }

    /// <summary>
    /// 批量导入书签
    /// </summary>
    /// <param name="bookmarks">书签列表</param>
    /// <returns>导入结果</returns>
    [HttpPost("import")]
    public Task<int> ImportBookmarks(List<BookmarkDto> bookmarks)
    {
        return _bookmarkService.ImportBookmarks(bookmarks);
    }

    /// <summary>
    /// 导出所有书签
    /// </summary>
    /// <returns>书签列表</returns>
    [HttpGet("export")]
    public Task<List<BookmarkDto>> ExportBookmarks()
    {
        return _bookmarkService.ExportBookmarks();
    }

    /// <summary>
    /// 更新书签排序
    /// </summary>
    /// <param name="sortData">排序数据</param>
    /// <returns>无内容</returns>
    [HttpPost("sort")]
    public Task<bool> UpdateSortOrder(List<BookmarkSortDto> sortData)
    {
        return _bookmarkService.UpdateSortOrder(sortData);
    }
}
