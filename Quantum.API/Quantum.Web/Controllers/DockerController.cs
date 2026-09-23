using Docker.DotNet.Models;
using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Web.Filters;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Quantum.Web.Controllers;

/// <summary>
/// Docker管理控制器
/// 提供对Docker容器、镜像、网络和卷的管理接口（管理员专用：主机容器操作对普通用户开放风险不成比例）
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class DockerController : BaseController
{
    private readonly DockerManagementService _dockerService;

    public DockerController(DockerManagementService dockerService)
    {
        _dockerService = dockerService;
    }

    #region 容器管理接口

    /// <summary>
    /// 列出容器
    /// </summary>
    /// <param name="all">是否列出所有容器（包括停止的）</param>
    /// <returns>容器列表</returns>
    [HttpGet("containers")]
    public async Task<IList<ContainerListResponse>> ListContainers([FromQuery] bool all = false)
    {
        return await _dockerService.ListContainersAsync(all);
    }

    /// <summary>
    /// 获取容器详细信息
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <returns>容器详细信息</returns>
    [HttpGet("containers/{containerId}")]
    public async Task<ContainerInspectResponse> InspectContainerAsync(string containerId)
    {
        return await _dockerService.InspectContainerAsync(containerId);
    }

    /// <summary>
    /// 获取容器统计信息
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <returns>容器统计信息</returns>
    [HttpGet("containers/{containerId}/stats")]
    public async Task<ContainerStatsResponse> GetContainerStats(string containerId)
    {
        return await _dockerService.GetContainerStatsAsync(containerId);
    }

    /// <summary>
    /// 批量获取容器统计信息
    /// </summary>
    /// <param name="containerIds">容器ID列表</param>
    /// <returns>容器统计信息字典</returns>
    [HttpPost("containers/stats")]
    public async Task<IDictionary<string, ContainerStatsResponse>> GetContainersStats([FromBody] IList<string> containerIds)
    {
        return await _dockerService.GetContainersStatsAsync(containerIds);
    }

    ///// <summary>
    ///// 创建容器
    ///// </summary>
    ///// <param name="config">容器配置</param>
    ///// <returns>创建结果</returns>
    //[HttpPost("containers")]
    //public async Task<CreateContainerResponse> CreateContainer([FromBody] CreateContainerParameters config)
    //{
    //    return await _dockerService.CreateContainerAsync(config);
    //}

    /// <summary>
    /// 启动容器
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <returns>操作结果</returns>
    [HttpPost("containers/{containerId}/start")]
    public async Task<bool> StartContainer(string containerId)
    {
        await _dockerService.StartContainerAsync(containerId);
        return true;
    }

    /// <summary>
    /// 停止容器
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <returns>操作结果</returns>
    [HttpPost("containers/{containerId}/stop")]
    public async Task<bool> StopContainer(string containerId)
    {
        await _dockerService.StopContainerAsync(containerId);
        return true;
    }

    /// <summary>
    /// 重启容器
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <returns>操作结果</returns>
    [HttpPost("containers/{containerId}/restart")]
    public async Task<bool> RestartContainer(string containerId)
    {
        await _dockerService.RestartContainerAsync(containerId);
        return true;
    }

    /// <summary>
    /// 删除容器
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <param name="force">是否强制删除</param>
    /// <param name="removeVolumes">是否同时删除关联卷</param>
    /// <returns>操作结果</returns>
    [HttpDelete("containers/{containerId}")]
    public async Task<bool> RemoveContainer(string containerId, [FromQuery] bool force = false, [FromQuery] bool removeVolumes = false)
    {
        await _dockerService.RemoveContainerAsync(containerId, force, removeVolumes);
        return true;
    }

    /// <summary>
    /// 获取容器日志
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <param name="follow">是否持续跟踪日志</param>
    /// <param name="stdout">是否包含标准输出</param>
    /// <param name="stderr">是否包含错误输出</param>
    /// <param name="tail">获取最后几行日志</param>
    /// <returns>日志内容</returns>
    [HttpGet("containers/{containerId}/logs")]
    public async Task<string> GetContainerLogs(
        string containerId,
        [FromQuery] bool follow = false,
        [FromQuery] bool stdout = true,
        [FromQuery] bool stderr = true,
        [FromQuery] string tail = "all")
    {
        return await _dockerService.GetContainerLogsAsync(containerId, follow, stdout, stderr, tail);
    }

    #endregion

    #region 镜像管理接口

    /// <summary>
    /// 列出镜像
    /// </summary>
    /// <param name="all">是否列出所有镜像</param>
    /// <returns>镜像列表</returns>
    [HttpGet("images")]
    public async Task<IList<ImagesListResponse>> ListImages([FromQuery] bool all = false)
    {
        return await _dockerService.ListImagesAsync(all);
    }

    /// <summary>
    /// 拉取镜像
    /// </summary>
    /// <param name="image">镜像名称</param>
    /// <param name="tag">标签</param>
    /// <returns>操作结果</returns>
    [HttpPost("images/pull")]
    public async Task<bool> PullImage([FromQuery] string image, [FromQuery] string tag = "latest")
    {
        await _dockerService.PullImageAsync(image, tag);
        return true;
    }

    /// <summary>
    /// 删除镜像
    /// </summary>
    /// <param name="imageId">镜像ID</param>
    /// <param name="force">是否强制删除</param>
    /// <returns>操作结果</returns>
    [HttpDelete("images/{imageId}")]
    public async Task<bool> RemoveImage(string imageId, [FromQuery] bool force = false)
    {
        await _dockerService.RemoveImageAsync(imageId, force);
        return true;
    }

    #endregion

    #region 网络管理接口

    /// <summary>
    /// 列出网络
    /// </summary>
    /// <returns>网络列表</returns>
    [HttpGet("networks")]
    public async Task<IList<NetworkResponse>> ListNetworks()
    {
        return await _dockerService.ListNetworksAsync();
    }

    /// <summary>
    /// 连接容器到网络
    /// </summary>
    /// <param name="networkId">网络ID</param>
    /// <param name="containerId">容器ID</param>
    /// <returns>操作结果</returns>
    [HttpPost("networks/{networkId}/connect")]
    public async Task<bool> ConnectContainerToNetwork(string networkId, [FromQuery] string containerId)
    {
        await _dockerService.ConnectContainerToNetworkAsync(networkId, containerId);
        return true;
    }

    /// <summary>
    /// 断开容器与网络的连接
    /// </summary>
    /// <param name="networkId">网络ID</param>
    /// <param name="containerId">容器ID</param>
    /// <returns>操作结果</returns>
    [HttpPost("networks/{networkId}/disconnect")]
    public async Task<bool> DisconnectContainerFromNetwork(string networkId, [FromQuery] string containerId)
    {
        await _dockerService.DisconnectContainerFromNetworkAsync(networkId, containerId);
        return true;
    }

    #endregion

    #region 卷管理接口

    /// <summary>
    /// 列出卷
    /// </summary>
    /// <returns>卷列表</returns>
    [HttpGet("volumes")]
    public async Task<VolumesListResponse> ListVolumes()
    {
        return await _dockerService.ListVolumesAsync();
    }

    /// <summary>
    /// 创建卷
    /// </summary>
    /// <param name="name">卷名称</param>
    /// <param name="driver">驱动类型</param>
    /// <returns>创建结果</returns>
    [HttpPost("volumes")]
    public async Task<VolumeResponse> CreateVolume([FromQuery] string name, [FromQuery] string driver = "local")
    {
        return await _dockerService.CreateVolumeAsync(name, driver);
    }

    /// <summary>
    /// 删除卷
    /// </summary>
    /// <param name="volumeName">卷名称</param>
    /// <returns>操作结果</returns>
    [HttpDelete("volumes/{volumeName}")]
    public async Task<bool> RemoveVolume(string volumeName)
    {
        await _dockerService.RemoveVolumeAsync(volumeName);
        return true;
    }

    #endregion

    #region 系统信息接口

    /// <summary>
    /// 获取Docker系统信息
    /// </summary>
    /// <returns>系统信息</returns>
    [HttpGet("info")]
    public async Task<SystemInfoResponse> GetSystemInfo()
    {
        return await _dockerService.GetSystemInfoAsync();
    }

    /// <summary>
    /// 获取Docker版本信息
    /// </summary>
    /// <returns>版本信息</returns>
    [HttpGet("version")]
    public async Task<VersionResponse> GetVersion()
    {
        return await _dockerService.GetVersionAsync();
    }

    #endregion
}