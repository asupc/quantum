@echo off
rem 构建 quantum-app 安卓客户端 APK。
rem 默认 release：keystore.properties 存在时自动正式签名（自分发依赖该签名），
rem 缺失时回退 debug 签名仅供开发；构建调试包用 build-apk.bat debug。
rem 版本号：下方 MAJOR_MINOR 指定大版本（如 1.2），每次构建补丁号自动 +1（1.2.0、1.2.1 …），
rem 换大版本后从 .0 重新计；状态存 %~dp0build-apk.version（已 gitignore）。
rem versionCode 由版本号推导（major*1000000+minor*1000+patch），随版本单调递增。
setlocal
cd /d %~dp0

set "APP=quantum-app"
set "JDK_LOCAL=D:\DataCenter\jdk\jdk-17.0.20.1+1"

rem ---- 版本号计算：大版本相同则补丁号+1，否则重置为 0；versionCode 随版本推导 ----
set "MAJOR_MINOR=1.0"
set "VERFILE=build-apk.version"
set "LAST="
if exist "%VERFILE%" set /p LAST=<"%VERFILE%"
set "LAST_MM="
if defined LAST for /f "tokens=1,2 delims=." %%a in ("%LAST%") do set "LAST_MM=%%a.%%b"
set "PATCH=0"
if defined LAST_MM if "%LAST_MM%"=="%MAJOR_MINOR%" for /f "tokens=3 delims=." %%c in ("%LAST%") do set /a PATCH=%%c+1
set "VERSION=%MAJOR_MINOR%.%PATCH%"
set /a NEXT_PATCH=%PATCH%+1
set /a VCODE=0
for /f "tokens=1,2 delims=." %%a in ("%MAJOR_MINOR%") do set /a VCODE=%%a*1000000+%%b*1000+%PATCH%
> "%VERFILE%" echo %VERSION%
echo 本次构建版本：%VERSION%（上次：%LAST%），versionCode：%VCODE%
echo.

rem ---- 构建类型：默认 release，可传参 debug ----
set "VARIANT=%~1"
if /i "%VARIANT%"=="" set "VARIANT=release"
if /i "%VARIANT%"=="release" goto :variant_ok
if /i "%VARIANT%"=="debug" goto :variant_ok
echo 用法：build-apk.bat [release 或 debug]，默认 release
exit /b 1
:variant_ok

rem ---- JDK：项目要求 17+。JAVA_HOME 缺失或版本过低时，自动改用本机已知 JDK17 ----
if not defined JAVA_HOME if exist "%JDK_LOCAL%" set "JAVA_HOME=%JDK_LOCAL%"
if defined JAVA_HOME set "PATH=%JAVA_HOME%\bin;%PATH%"
call :java_major JV
if not defined JV (
    echo [失败] 未找到可用的 java：请安装 JDK 17+ 或设置 JAVA_HOME，本机可 set JAVA_HOME=%JDK_LOCAL%
    goto :fail
)
if %JV% LSS 17 if exist "%JDK_LOCAL%" (
    echo 当前 Java 主版本 %JV% 低于 17，切换到本机 JDK17：%JDK_LOCAL%
    set "JAVA_HOME=%JDK_LOCAL%"
    set "PATH=%JDK_LOCAL%\bin;%PATH%"
    call :java_major JV
)
if %JV% LSS 17 (
    echo [失败] quantum-app 需要 JDK 17+，当前 Java 主版本为 %JV%，请将 JAVA_HOME 指向 JDK17
    goto :fail
)

rem ---- 前置检查 ----
if not exist "%APP%\local.properties" (
    echo [失败] 缺少 %APP%\local.properties，请按 quantum-app\README.md 写入 sdk.dir=Android SDK 路径
    goto :fail
)
if exist "%APP%\keystore.properties" (
    echo 签名：keystore.properties 存在，release 使用正式证书
) else (
    echo 签名：未找到 keystore.properties，release 回退 debug 签名，仅供开发联调
)

echo ---- Java 环境 ----
java -version 2>&1
echo ---- 开始构建 %VARIANT%（版本 %VERSION%，versionCode %VCODE%）----

set "TASK=assembleRelease"
if /i "%VARIANT%"=="debug" set "TASK=assembleDebug"
pushd "%APP%"
call gradlew.bat %TASK% -PappVersionName=%VERSION% -PappVersionCode=%VCODE%
set "BUILD_EC=%errorlevel%"
popd
if not "%BUILD_EC%"=="0" goto :fail

rem ---- 收集产物：拷贝到 quantum-app\dist，文件名带版本号 ----
set "APK=%APP%\app\build\outputs\apk\%VARIANT%\app-%VARIANT%.apk"
if not exist "%APK%" (
    echo [失败] 构建成功但未找到产物 %APK%
    goto :fail
)

set "DIST=%APP%\dist"
if not exist "%DIST%" mkdir "%DIST%"
copy /y "%APK%" "%DIST%\quantum-v%VERSION%-%VARIANT%.apk" >nul

echo.
echo ===== 构建成功 =====
echo   APK     ：quantum-app\dist\quantum-v%VERSION%-%VARIANT%.apk
echo   版本    ：%VERSION%（versionCode %VCODE%，下次构建自动递增为 %MAJOR_MINOR%.%NEXT_PATCH%）
echo   原始产物：%APK%
goto :end

:fail
echo.
echo ===== 构建失败，请根据上方报错排查 =====
exit /b 1

:end
if /i not "%QUANTUM_NO_PAUSE%"=="1" pause
exit /b 0

rem 子过程：解析 java 主版本号写入 %1，解析失败保持为空
:java_major
for /f "tokens=3" %%a in ('java -version 2^>^&1 ^| findstr /i "version"') do (
    for /f "delims=." %%m in ("%%~a") do set "%1=%%m"
)
goto :eof
