using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 书签服务：分页/关键字查询、详情、增删改、批量导入导出与排序。
/// 控制器只做参数绑定与结果返回，业务与数据访问全部收敛在本类。
/// </summary>
public class BookmarkService
{
    private readonly IQuantumDbContext _context;

    public BookmarkService(IQuantumDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 获取所有书签（支持分页和搜索）
    /// </summary>
    /// <param name="page">页码</param>
    /// <param name="pageSize">每页数量</param>
    /// <param name="search">搜索关键字</param>
    /// <returns>书签列表</returns>
    public async Task<PageResult<BookmarkDto>> GetBookmarks(int page = 1, int pageSize = 20, string search = "")
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _context.Bookmarks.AsQueryable();

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(b => b.Title.Contains(search) || b.Url.Contains(search));
        }

        var totalCount = await query.CountAsync();
        var bookmarks = await query
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new BookmarkDto
            {
                Id = b.Id,
                Title = b.Title,
                Url = b.Url,
                IconUrl = b.IconUrl,
                Description = b.Description,
                ParentId = b.ParentId,
                SortOrder = b.SortOrder,
                IsFolder = b.IsFolder,
                CreatedAt = b.CreatedAt,
                UpdatedAt = b.UpdatedAt
            })
            .ToListAsync();

        return new PageResult<BookmarkDto>
        {
            Data = bookmarks,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 根据ID获取书签详情
    /// </summary>
    /// <param name="id">书签ID</param>
    /// <returns>书签详情</returns>
    public async Task<BookmarkDto> GetBookmark(string id)
    {
        var bookmark = await _context.Bookmarks.FindAsync(id);

        if (bookmark == null)
        {
            return null;
        }

        var bookmarkDto = new BookmarkDto
        {
            Id = bookmark.Id,
            Title = bookmark.Title,
            Url = bookmark.Url,
            IconUrl = bookmark.IconUrl,
            Description = bookmark.Description,
            ParentId = bookmark.ParentId,
            SortOrder = bookmark.SortOrder,
            IsFolder = bookmark.IsFolder,
            CreatedAt = bookmark.CreatedAt,
            UpdatedAt = bookmark.UpdatedAt
        };

        return bookmarkDto;
    }

    /// <summary>
    /// 创建新书签
    /// </summary>
    /// <param name="bookmarkDto">书签信息</param>
    /// <returns>创建的书签</returns>
    public async Task<BookmarkDto> CreateBookmark(BookmarkDto bookmarkDto)
    {
        var bookmark = new Bookmark
        {
            Title = bookmarkDto.Title,
            Url = bookmarkDto.Url,
            IconUrl = bookmarkDto.IconUrl,
            Description = bookmarkDto.Description,
            ParentId = bookmarkDto.ParentId,
            SortOrder = bookmarkDto.SortOrder,
            IsFolder = bookmarkDto.IsFolder,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        _context.Bookmarks.Add(bookmark);
        await _context.SaveChangesAsync();

        bookmarkDto.CreatedAt = bookmark.CreatedAt;
        bookmarkDto.UpdatedAt = bookmark.UpdatedAt;
        return bookmarkDto;
    }

    /// <summary>
    /// 更新书签
    /// </summary>
    /// <param name="id">书签ID</param>
    /// <param name="bookmarkDto">更新的书签信息</param>
    /// <returns>无内容</returns>
    public async Task<bool> UpdateBookmark(string id, BookmarkDto bookmarkDto)
    {
        if (id != bookmarkDto.Id)
        {
            return false;
        }

        var bookmark = await _context.Bookmarks.FindAsync(id);
        if (bookmark == null)
        {
            throw new BusinessException("书签不存在，可能已被删除，请刷新列表！");
        }
        bookmark.Title = bookmarkDto.Title;
        bookmark.Url = bookmarkDto.Url;
        bookmark.IconUrl = bookmarkDto.IconUrl;
        bookmark.Description = bookmarkDto.Description;
        bookmark.ParentId = bookmarkDto.ParentId;
        bookmark.SortOrder = bookmarkDto.SortOrder;
        bookmark.IsFolder = bookmarkDto.IsFolder;
        bookmark.UpdatedAt = DateTime.Now;
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 删除书签
    /// </summary>
    /// <param name="id">书签ID</param>
    public async Task<bool> DeleteBookmark(string id)
    {
        var bookmark = await _context.Bookmarks.FindAsync(id);
        if (bookmark == null)
        {
            throw new BusinessException("书签不存在，可能已被删除，请刷新列表！");
        }

        _context.Bookmarks.Remove(bookmark);
        await _context.SaveChangesAsync();

        return true;
    }

    /// <summary>
    /// 批量导入书签
    /// </summary>
    /// <param name="bookmarks">书签列表</param>
    /// <returns>导入结果</returns>
    public async Task<int> ImportBookmarks(List<BookmarkDto> bookmarks)
    {
        var entities = bookmarks.Select(dto => new Bookmark
        {
            Title = dto.Title,
            Url = dto.Url,
            IconUrl = dto.IconUrl,
            Description = dto.Description,
            ParentId = dto.ParentId,
            SortOrder = dto.SortOrder,
            IsFolder = dto.IsFolder,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            Id = dto.Id
        }).ToList();

        _context.Bookmarks.AddRange(entities);
        await _context.SaveChangesAsync();

        return entities.Count;
    }

    /// <summary>
    /// 导出所有书签
    /// </summary>
    /// <returns>书签列表</returns>
    public async Task<List<BookmarkDto>> ExportBookmarks()
    {
        var bookmarks = await _context.Bookmarks
            .Select(b => new BookmarkDto
            {
                Id = b.Id,
                Title = b.Title,
                Url = b.Url,
                IconUrl = b.IconUrl,
                Description = b.Description,
                ParentId = b.ParentId,
                SortOrder = b.SortOrder,
                IsFolder = b.IsFolder,
                CreatedAt = b.CreatedAt,
                UpdatedAt = b.UpdatedAt
            })
            .ToListAsync();

        return bookmarks;
    }

    /// <summary>
    /// 更新书签排序
    /// </summary>
    /// <param name="sortData">排序数据</param>
    /// <returns>无内容</returns>
    public async Task<bool> UpdateSortOrder(List<BookmarkSortDto> sortData)
    {
        foreach (var item in sortData)
        {
            var bookmark = await _context.Bookmarks.FindAsync(item.Id);
            if (bookmark != null)
            {
                bookmark.SortOrder = item.SortOrder;
                bookmark.UpdatedAt = DateTime.Now;
                _context.Entry(bookmark).State = EntityState.Modified;
            }
        }

        await _context.SaveChangesAsync();
        return true;
    }
}
