@echo off
rem 构建后端服务（自包含前端构建产物）并打包 Docker 镜像：
rem dotnet publish + 前端 vite 构建产出 quantum-release 目录（后端 + 内嵌 wwwroot），
rem 以根目录 Dockerfile 打成 asupc/quantum 镜像，并 docker save 导出到 quantum-image\ 目录
rem （仅本地构建，不推送；推送到 Docker Hub 用 build.bat）。
rem 版本号：下方 MAJOR_MINOR 指定大版本（如 1.2），每次构建补丁号自动 +1（1.2.0、1.2.1 …），
rem 换大版本后从 .0 重新计；状态存 %~dp0build-service.version（已 gitignore）。
setlocal
cd /d %~dp0

set "OUT=quantum-release"
set "WEB=quantum-web"
set "IMAGE=asupc/quantum"
set "IMGDIR=quantum-image"

rem ---- 版本号计算：大版本相同则补丁号+1，否则重置为 0 ----
set "MAJOR_MINOR=1.0"
set "VERFILE=build-service.version"
set "LAST="
if exist "%VERFILE%" set /p LAST=<"%VERFILE%"
set "LAST_MM="
if defined LAST for /f "tokens=1,2 delims=." %%a in ("%LAST%") do set "LAST_MM=%%a.%%b"
set "PATCH=0"
if defined LAST_MM if "%LAST_MM%"=="%MAJOR_MINOR%" for /f "tokens=3 delims=." %%c in ("%LAST%") do set /a PATCH=%%c+1
set "VERSION=%MAJOR_MINOR%.%PATCH%"
set /a NEXT_PATCH=%PATCH%+1
> "%VERFILE%" echo %VERSION%
echo 本次构建版本：%VERSION%（上次：%LAST%）
echo.

echo ===== [1/6] 清理输出目录 %OUT% =====
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%OUT%" (
    echo [失败] 旧目录 %OUT% 删除失败，请检查是否有程序占用
    goto :fail
)

echo ===== [2/6] 发布后端服务 dotnet publish =====
dotnet publish ./Quantum.API/Quantum.Web/Quantum.Web.csproj -c release -o "%OUT%"
if errorlevel 1 goto :fail

echo ===== [3/6] 构建前端，产物写入 %OUT%\wwwroot =====
pushd "%WEB%"
if not exist node_modules (
    echo node_modules 缺失，先执行 npm install ...
    call npm install
    if errorlevel 1 goto :fail_popd
)
call npm run build
if errorlevel 1 goto :fail_popd
popd

echo ===== [4/6] 校验产物完整性 =====
if not exist "%OUT%\Quantum.dll" (
    echo [失败] %OUT% 下缺少 Quantum.dll，后端发布异常
    goto :fail
)
if not exist "%OUT%\wwwroot\index.html" (
    echo [失败] %OUT%\wwwroot 下缺少 index.html，前端产物未嵌入
    goto :fail
)

echo ===== [5/6] 打包 Docker 镜像 =====
docker version >nul 2>&1
if errorlevel 1 (
    echo [失败] Docker 未运行，请先启动 Docker Desktop 再执行本脚本
    goto :fail
)
echo 镜像标签：%IMAGE%:%VERSION% 与 %IMAGE%:latest
docker build . -t %IMAGE%:%VERSION% -t %IMAGE%:latest
if errorlevel 1 goto :fail

echo ===== [6/6] 导出镜像 docker save =====
rem 注意：tar 不能放 %OUT%（它是 Dockerfile 的 COPY 源，混入会被打进镜像），统一放 %IMGDIR%
if not exist "%IMGDIR%" mkdir "%IMGDIR%"
set "IMGTAR=%IMGDIR%\asupc-quantum_%VERSION%.tar"
if exist "%IMGTAR%" del /f /q "%IMGTAR%"
echo 导出：%IMGTAR%（含版本与 latest 双标签）
docker save -o "%IMGTAR%" %IMAGE%:%VERSION% %IMAGE%:latest
if errorlevel 1 goto :fail

echo.
echo ===== 构建成功 =====
echo   构建版本：%VERSION%（下次构建将自动递增为 %MAJOR_MINOR%.%NEXT_PATCH%）
echo   服务目录：quantum-release\ —— 后端 + 内嵌前端（镜像内即此目录）
echo   镜像产物：%IMAGE%:%VERSION%、%IMAGE%:latest（仅本地构建未推送，推送可用 build.bat）
echo   镜像导出：%IMGTAR%，目标机部署：docker load -i 该文件
echo   运行参考：docker run -d -p 5088:5088 --name quantum %IMAGE%:latest
echo   配置提醒：appsettings.json 按 csproj 安全约定不打入镜像，部署时挂载或复制到容器 /app 根目录
echo   服务端口：以 appsettings.json 的 Port 为准，默认 5088
goto :end

:fail_popd
popd

:fail
echo.
echo ===== 构建失败，请根据上方报错排查 =====
exit /b 1

:end
if /i not "%QUANTUM_NO_PAUSE%"=="1" pause
exit /b 0
