using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Utils;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 外部推送接入凭据管理（管理员专用）：创建/列表元数据/轮换/启停/删除。
/// 与发送端点分离，且本控制器走普通 JWT + [LoggedInUser]——PushKey 凭据到不了这里。
/// 明文密钥只在创建/轮换那一次回显；列表与操作日志都不含密钥或摘要。
/// </summary>
[CustomAuthorizationFilter]
[LoggedInUser]
public class ExternalPushCredentialController : BaseController
{
    readonly ExternalPushCredentialService _credentialService;

    public ExternalPushCredentialController(ExternalPushCredentialService credentialService)
    {
        _credentialService = credentialService;
    }

    /// <summary>凭据列表（元数据）</summary>
    [HttpGet]
    public async Task<List<ExternalPushCredentialRow>> Index()
    {
        var rows = await _credentialService.ListAsync();
        return rows.Select(n => new ExternalPushCredentialRow
        {
            Id = n.Id,
            DisplayName = n.DisplayName,
            Enabled = n.Enabled,
            RateLimitPerMinute = n.RateLimitPerMinute,
            DailyQuota = n.DailyQuota,
            Remark = n.Remark,
            TotalSent = n.TotalSent,
            CreatedAtUtc = n.CreatedAtUtc,
            LastUsedAtUtc = n.LastUsedAtUtc,
            ExpiresAtUtc = n.ExpiresAtUtc,
            RevokedAtUtc = n.RevokedAtUtc
        }).ToList();
    }

    /// <summary>创建凭据（默认禁用，需再显式启用）</summary>
    [HttpPost]
    [ActionLogFilter("创建外部推送凭据")]
    public async Task<ExternalPushCredentialCreated> Create([FromBody] ExternalPushCredentialCreateDto dto)
    {
        if (dto == null)
        {
            throw new BusinessException("缺少凭据内容");
        }

        var (credential, secret) = await _credentialService.CreateAsync(dto.DisplayName,
            dto.RateLimitPerMinute, dto.DailyQuota, dto.Remark, dto.ExpiresAtUtc);
        return new ExternalPushCredentialCreated
        {
            Id = credential.Id,
            DisplayName = credential.DisplayName,
            Secret = secret,
            Enabled = credential.Enabled,
            RateLimitPerMinute = credential.RateLimitPerMinute,
            DailyQuota = credential.DailyQuota,
            ExpiresAtUtc = credential.ExpiresAtUtc
        };
    }

    /// <summary>轮换密钥（旧密钥立即失效，新明文只回显一次）</summary>
    [HttpPut("{id}/rotate")]
    [ActionLogFilter("轮换外部推送凭据")]
    public async Task<ExternalPushCredentialCreated> Rotate([FromRoute] string id)
    {
        var secret = await _credentialService.RotateAsync(id);
        var rows = await _credentialService.ListAsync();
        var row = rows.FirstOrDefault(n => n.Id == id);
        return new ExternalPushCredentialCreated
        {
            Id = id,
            DisplayName = row?.DisplayName,
            Secret = secret,
            Enabled = row?.Enabled ?? false,
            RateLimitPerMinute = row?.RateLimitPerMinute ?? 0,
            DailyQuota = row?.DailyQuota ?? 0,
            ExpiresAtUtc = row?.ExpiresAtUtc
        };
    }

    /// <summary>启用/禁用（禁用即吊销投递能力，已存消息不抹除）</summary>
    [HttpPut("{id}/enabled")]
    [ActionLogFilter("变更外部推送凭据状态")]
    public Task<bool> SetEnabled([FromRoute] string id, [FromQuery] bool enabled)
    {
        return _credentialService.SetEnabledAsync(id, enabled, GetUserId());
    }

    /// <summary>删除凭据（连带清理其幂等记录；已投递的通知与会话消息保留）</summary>
    [HttpDelete("{id}")]
    [ActionLogFilter("删除外部推送凭据")]
    public Task<bool> Delete([FromRoute] string id)
    {
        return _credentialService.DeleteAsync(id);
    }
}

/// <summary>凭据创建入参</summary>
public class ExternalPushCredentialCreateDto
{
    public string DisplayName { get; set; }
    public int RateLimitPerMinute { get; set; } = 60;
    public int DailyQuota { get; set; } = 1000;
    public string Remark { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}
