# 请参阅 https://aka.ms/customizecontainer 以了解如何自定义调试容器，以及 Visual Studio 如何使用此 Dockerfile 生成映像以更快地进行调试。
# 多项目分层结构：构建上下文为上一级目录（Quantum.API/），五个项目分层 COPY 加速增量还原。

# 此阶段用于在快速模式(默认为调试配置)下从 VS 运行时
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 5088


# 此阶段用于生成服务项目
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["Quantum.Web/Quantum.Web.csproj", "Quantum.Web/"]
COPY ["Quantum.Application/Quantum.Application.csproj", "Quantum.Application/"]
COPY ["Quantum.Data/Quantum.Data.csproj", "Quantum.Data/"]
COPY ["Quantum.Entities/Quantum.Entities.csproj", "Quantum.Entities/"]
COPY ["Quantum.Utils/Quantum.Utils.csproj", "Quantum.Utils/"]
RUN dotnet restore "Quantum.Web/Quantum.Web.csproj"
COPY . .
RUN dotnet build "Quantum.Web/Quantum.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

# 此阶段用于发布要复制到最终阶段的服务项目
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Quantum.Web/Quantum.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# 此阶段在生产中使用，或在常规模式下从 VS 运行时使用(在不使用调试配置时为默认值)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Quantum.dll"]
