using Docker.DotNet;
using Docker.DotNet.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Quantum.Application;

/// <summary>
/// Docker管理服务
/// 提供对Docker容器、镜像、网络和卷的完整管理功能
/// </summary>
public class DockerManagementService
{
    private readonly DockerClient _dockerClient;
    private readonly CancellationTokenSource _cancellationTokenSource;

    public DockerManagementService()
    {
        // 检查是否在Windows或Unix环境下运行，设置相应的Docker endpoint
        string dockerEndpoint = Environment.OSVersion.Platform == PlatformID.Unix
            ? "unix:///var/run/docker.sock"
            : "npipe://./pipe/docker_engine";

        _dockerClient = new DockerClientConfiguration(new Uri(dockerEndpoint)).CreateClient();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    #region 容器管理

    /// <summary>
    /// 列出容器
    /// </summary>
    /// <param name="all">是否列出所有容器（包括停止的）</param>
    /// <returns>容器列表</returns>
    public async Task<IList<ContainerListResponse>> ListContainersAsync(bool all = false)
    {
        var parameters = new ContainersListParameters
        {
            All = all
        };

        return await _dockerClient.Containers.ListContainersAsync(parameters);
    }

    /// <summary>
    /// 获取容器详细信息
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <returns>容器详细信息</returns>
    public async Task<ContainerInspectResponse> InspectContainerAsync(string containerId)
    {
        return await _dockerClient.Containers.InspectContainerAsync(containerId);
    }

    /// <summary>
    /// 获取容器统计信息
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <returns>容器统计信息</returns>
    public async Task<ContainerStatsResponse> GetContainerStatsAsync(string containerId)
    {
        var parameters = new ContainerStatsParameters
        {
            Stream = false,
            OneShot = true
        };
        
        var stats = await _dockerClient.Containers.GetContainerStatsAsync(containerId, parameters,new CancellationToken());
        using var reader = new StreamReader(stats, leaveOpen: true);
        var content = await reader.ReadToEndAsync();
        // 将JSON字符串反序列化为对象
        var data =  JsonConvert.DeserializeObject<ContainerStatsResponse>(content);
        return data;
    }

    /// <summary>
    /// 批量获取多个容器的统计信息
    /// </summary>
    /// <param name="containerIds">容器ID列表</param>
    /// <returns>容器统计信息字典，键为容器ID，值为统计信息</returns>
    public async Task<IDictionary<string, ContainerStatsResponse>> GetContainersStatsAsync(IList<string> containerIds)
    {
        var result = new Dictionary<string, ContainerStatsResponse>();
        
        // 创建所有请求任务
        var tasks = new List<Task<KeyValuePair<string, ContainerStatsResponse>>>();
        
        foreach (var containerId in containerIds)
        {
            tasks.Add(GetContainerStatsWithIdAsync(containerId));
        }
        
        // 并行执行所有任务
        var taskResults = await Task.WhenAll(tasks);
        
        // 将结果添加到字典中
        foreach (var taskResult in taskResults)
        {
            result[taskResult.Key] = taskResult.Value;
        }
        
        return result;
    }
    
    /// <summary>
    /// 获取单个容器的统计信息并返回容器ID和统计信息的键值对
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <returns>容器ID和统计信息的键值对</returns>
    private async Task<KeyValuePair<string, ContainerStatsResponse>> GetContainerStatsWithIdAsync(string containerId)
    {
        var stats = await GetContainerStatsAsync(containerId);
        return new KeyValuePair<string, ContainerStatsResponse>(containerId, stats);
    }

    /// <summary>
    /// 创建容器
    /// </summary>
    /// <param name="config">容器配置</param>
    /// <returns>创建结果</returns>
    public async Task<CreateContainerResponse> CreateContainerAsync(CreateContainerParameters config)
    {
        return await _dockerClient.Containers.CreateContainerAsync(config);
    }

    /// <summary>
    /// 启动容器
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <returns>操作结果</returns>
    public async Task<bool> StartContainerAsync(string containerId)
    {
        return await _dockerClient.Containers.StartContainerAsync(containerId, null);
    }

    /// <summary>
    /// 停止容器
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <param name="waitBeforeKillSeconds">超时前等待秒数</param>
    /// <returns>操作结果</returns>
    public async Task<bool> StopContainerAsync(string containerId, uint waitBeforeKillSeconds = 10)
    {
        var parameters = new ContainerStopParameters
        {
            WaitBeforeKillSeconds = waitBeforeKillSeconds
        };

        return await _dockerClient.Containers.StopContainerAsync(containerId, parameters);
    }

    /// <summary>
    /// 重启容器
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <param name="waitBeforeKillSeconds">超时前等待秒数</param>
    /// <returns>操作结果</returns>
    public async Task<bool> RestartContainerAsync(string containerId, uint waitBeforeKillSeconds = 10)
    {
        var parameters = new ContainerRestartParameters
        {
            WaitBeforeKillSeconds = waitBeforeKillSeconds
        };

        await _dockerClient.Containers.RestartContainerAsync(containerId, parameters);
        return true;
    }

    /// <summary>
    /// 删除容器
    /// </summary>
    /// <param name="containerId">容器ID</param>
    /// <param name="force">是否强制删除</param>
    /// <param name="removeVolumes">是否同时删除关联卷</param>
    /// <returns>操作结果</returns>
    public async Task RemoveContainerAsync(string containerId, bool force = false, bool removeVolumes = false)
    {
        var parameters = new ContainerRemoveParameters
        {
            Force = force,
            RemoveVolumes = removeVolumes
        };

        await _dockerClient.Containers.RemoveContainerAsync(containerId, parameters);
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
    public async Task<string> GetContainerLogsAsync(string containerId, bool follow = false, bool stdout = true, bool stderr = true, string tail = "all")
    {
        var parameters = new ContainerLogsParameters
        {
            Follow = follow,
            ShowStdout = stdout,
            ShowStderr = stderr,
            Tail = tail
        };
        // 读取形态必须按容器创建时的 TTY 配置走（2026-09-20 真机定位：宿主机以 -t 创建的容器——
        // 如 quantum_product——日志是裸流无 8 字节帧头，固定按复用流解析会抛
        // "MultiplexedStream: unknown stream type" 500，非 TTY 容器（compose 常规）才需要解复用；
        // tty 参数只影响 MultiplexedStream 内部是否解帧，读取统一走 ReadOutputToEndAsync 即可）
        bool tty = (await InspectContainerAsync(containerId))?.Config?.Tty == true;
        var stream = await _dockerClient.Containers.GetContainerLogsAsync(containerId, tty: tty, parameters, CancellationToken.None);
        var (stdoutText, stderrText) = await stream.ReadOutputToEndAsync(CancellationToken.None);
        return stdoutText + stderrText;
    }

    ///// <summary>

    #endregion

    #region 镜像管理

    /// <summary>
    /// 列出镜像
    /// </summary>
    /// <param name="all">是否列出所有镜像</param>
    /// <returns>镜像列表</returns>
    public async Task<IList<ImagesListResponse>> ListImagesAsync(bool all = false)
    {
        var parameters = new ImagesListParameters
        {
            All = all
        };

        return await _dockerClient.Images.ListImagesAsync(parameters);
    }

    /// <summary>
    /// 拉取镜像
    /// </summary>
    /// <param name="image">镜像名称</param>
    /// <param name="tag">标签</param>
    /// <returns>操作结果</returns>
    public async Task PullImageAsync(string image, string tag = "latest")
    {
        var parameters = new ImagesCreateParameters
        {
            FromImage = image,
            Tag = tag
        };

        var progress = new Progress<JSONMessage>();
        await _dockerClient.Images.CreateImageAsync(parameters, null, progress);
    }

    /// <summary>
    /// 删除镜像
    /// </summary>
    /// <param name="imageId">镜像ID</param>
    /// <param name="force">是否强制删除</param>
    /// <returns>操作结果</returns>
    public async Task RemoveImageAsync(string imageId, bool force = false)
    {
        var parameters = new ImageDeleteParameters
        {
            Force = force
        };

        await _dockerClient.Images.DeleteImageAsync(imageId, parameters);
    }

    #endregion

    #region 网络管理

    /// <summary>
    /// 列出网络
    /// </summary>
    /// <returns>网络列表</returns>
    public async Task<IList<NetworkResponse>> ListNetworksAsync()
    {
        return await _dockerClient.Networks.ListNetworksAsync(new NetworksListParameters());
    }

    /// <summary>
    /// 创建网络
    /// </summary>
    /// <param name="name">网络名称</param>
    /// <param name="driver">驱动类型</param>
    /// <returns>创建结果</returns>
    public async Task<NetworksCreateResponse> CreateNetworkAsync(string name, string driver = "bridge")
    {
        var parameters = new NetworksCreateParameters
        {
            Name = name,
            Driver = driver
        };

        return await _dockerClient.Networks.CreateNetworkAsync(parameters);
    }

    /// <summary>
    /// 删除网络
    /// </summary>
    /// <param name="networkId">网络ID</param>
    /// <returns>操作结果</returns>
    public async Task RemoveNetworkAsync(string networkId)
    {
        await _dockerClient.Networks.DeleteNetworkAsync(networkId);
    }

    /// <summary>
    /// 连接容器到网络
    /// </summary>
    /// <param name="networkId">网络ID</param>
    /// <param name="containerId">容器ID</param>
    /// <returns>操作结果</returns>
    public async Task ConnectContainerToNetworkAsync(string networkId, string containerId)
    {
        var parameters = new NetworkConnectParameters
        {
            Container = containerId
        };

        await _dockerClient.Networks.ConnectNetworkAsync(networkId, parameters);
    }

    /// <summary>
    /// 断开容器与网络的连接
    /// </summary>
    /// <param name="networkId">网络ID</param>
    /// <param name="containerId">容器ID</param>
    /// <returns>操作结果</returns>
    public async Task DisconnectContainerFromNetworkAsync(string networkId, string containerId)
    {
        var parameters = new NetworkDisconnectParameters
        {
            Container = containerId,
            Force = false
        };

        await _dockerClient.Networks.DisconnectNetworkAsync(networkId, parameters);
    }

    #endregion

    #region 卷管理

    /// <summary>
    /// 列出卷
    /// </summary>
    /// <returns>卷列表</returns>
    public async Task<VolumesListResponse> ListVolumesAsync()
    {
        return await _dockerClient.Volumes.ListAsync(new VolumesListParameters());
    }

    /// <summary>
    /// 创建卷
    /// </summary>
    /// <param name="name">卷名称</param>
    /// <param name="driver">驱动类型</param>
    /// <returns>创建结果</returns>
    public async Task<VolumeResponse> CreateVolumeAsync(string name, string driver = "local")
    {
        var parameters = new VolumesCreateParameters
        {
            Name = name,
            Driver = driver
        };

        return await _dockerClient.Volumes.CreateAsync(parameters);
    }

    /// <summary>
    /// 删除卷
    /// </summary>
    /// <param name="volumeName">卷名称</param>
    /// <returns>操作结果</returns>
    public async Task RemoveVolumeAsync(string volumeName)
    {
        await _dockerClient.Volumes.RemoveAsync(volumeName);
    }

    #endregion

    #region 系统信息

    /// <summary>
    /// 获取Docker系统信息
    /// </summary>
    /// <returns>系统信息</returns>
    public async Task<SystemInfoResponse> GetSystemInfoAsync()
    {
        return await _dockerClient.System.GetSystemInfoAsync();
    }

    /// <summary>
    /// 获取Docker版本信息
    /// </summary>
    /// <returns>版本信息</returns>
    public async Task<VersionResponse> GetVersionAsync()
    {
        return await _dockerClient.System.GetVersionAsync();
    }

    #endregion
}

