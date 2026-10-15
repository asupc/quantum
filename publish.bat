@echo off
cd /d %~dp0

rmdir /s /q quantum-release
rem 2026-10-15 内存优化 P1-6：ReadyToRun 预编译（-r 必备，否则 R2R 被 SDK 静默跳过；
rem --self-contained false 保持框架依赖、复用镜像内运行时；
rem Windows 本地直跑时 linux-x64 的 R2R 镜像被忽略、自动回退 JIT，不影响复跑）
dotnet publish ./Quantum.API/Quantum.Web/Quantum.Web.csproj -c release -o quantum-release -r linux-x64 --self-contained false -p:PublishReadyToRun=true
if errorlevel 1 exit /b 1

pushd .\quantum-web
call npm run build
if errorlevel 1 (
    popd
    exit /b 1
)
popd

echo ===== verify build artifacts =====
if not exist "quantum-release\Quantum.dll" (
    echo [FAIL] quantum-release missing Quantum.dll - backend publish incomplete
    exit /b 1
)
if not exist "quantum-release\wwwroot\index.html" (
    echo [FAIL] quantum-release wwwroot missing index.html - frontend build incomplete
    exit /b 1
)
rem appsettings.json is out of the repo by design; a missing one means the image would boot without config, so fail fast and hint how to mount it.
if not exist "quantum-release\appsettings.json" (
    echo [FAIL] quantum-release missing appsettings.json - not committed by design
    echo        Mount it when running the container, e.g. -v hostpath/appsettings.json:/app/appsettings.json
    exit /b 1
)
echo [OK] publish artifacts verified: quantum-release
pause
