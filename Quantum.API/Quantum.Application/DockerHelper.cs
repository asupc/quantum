using Docker.DotNet;
using Docker.DotNet.Models;

namespace Quantum.Application;

public static class DockerHelper
{
    readonly static DockerClient dockerClient;
    static DockerHelper()
    {
        dockerClient = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
    }

    public static async Task<string> StopAsync(string name)
    {
        try
        {
            IList<ContainerListResponse> containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters()
            {
                All = true,
            });
            foreach (ContainerListResponse container in containers)
            {
                if (container.Names[0].ToLower() == "/" + name.ToLower())
                {
                    if (container.State == "running")
                    {
                        await Console.Out.WriteLineAsync($"[{DateTime.Now}]正在停止容器：" + name);
                        await dockerClient.Containers.StopContainerAsync(container.ID, new ContainerStopParameters());
                        return $"停止容器【{name}】完成";
                    }
                    else
                    {
                        return $"【{name}】停止失败，容器未在运行状态。";
                    }
                }
            }
            return $"【{name}】停止失败，未找到指定容器。";
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync("DockerList方法出现异常：" + ex.Message);
            await Console.Out.WriteLineAsync("DockerList方法出现异常：" + ex.StackTrace);
        }
        return $"【${name}】停止失败，未知异常。";
    }


    public static async Task<string> RestartAsync(string name)
    {
        try
        {
            IList<ContainerListResponse> containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true
            });

            foreach (ContainerListResponse container in containers)
            {
                if (container.Names[0].ToLower() == "/" + name.ToLower())
                {
                    await Console.Out.WriteLineAsync($"[{DateTime.Now}]正在重启容器：" + name);
                    await dockerClient.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters { });
                    return $"重启容器【{name}】完成";
                }
            }
            return $"【{name}】重启失败，未找到指定容器。";
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync("DockerList方法出现异常：" + ex.Message);
            await Console.Out.WriteLineAsync("DockerList方法出现异常：" + ex.StackTrace);
        }
        return $"【${name}】重启失败，未知异常。";
    }


    public static async Task<IList<ContainerListResponse>> ListAsync(bool all)
    {
        return await dockerClient.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = all,
        });
    }
}
