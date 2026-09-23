cd /d %~dp0

:: 获取当前日期并格式化为 yyyyMMdd（Win11 已移除 WMIC，改用 PowerShell）
for /f "delims=" %%a in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set "date_tag=%%a"
echo %date_tag%

docker build . -t asupc/quantum:%date_tag%
if errorlevel 1 exit /b 1
docker build . -t asupc/quantum:latest
if errorlevel 1 exit /b 1
echo 镜像构建完成，开始PUSH

docker push asupc/quantum:%date_tag%
if errorlevel 1 exit /b 1
docker push asupc/quantum:latest
if errorlevel 1 exit /b 1
pause
