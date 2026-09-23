FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# 设置环境变量
ENV TZ=Asia/Shanghai \
    DEBIAN_FRONTEND=noninteractive

# 安装运行时基础依赖
# 2026-09-16 脚本执行引擎改造：任务改为 C# 源码进程内执行（Roslyn 编译），
# node/python3/build-essential 安装段与对应运行时清理段整体移除
RUN apt-get update && apt-get install -y --no-install-recommends \
        wget \
        curl \
        git \
        fontconfig \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# 设置工作目录
WORKDIR /app

# 复制应用文件
COPY ./quantum-release /app/

# 暴露端口
EXPOSE 5088

# 应用入口点
ENTRYPOINT ["dotnet", "Quantum.dll"]
