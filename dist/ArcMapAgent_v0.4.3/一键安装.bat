@echo off
chcp 936 >nul
setlocal enabledelayedexpansion
title ArcMap Agent v0.4.3 安装程序

echo.
echo  ============================================================
echo    ArcMap Agent v0.4.3  安装程序
echo  ============================================================
echo.

set "ADDINID={6F3A9C52-8B14-4E2D-9A7B-1D5C0E4F83A6}"
set "SRC=%~dp0ArcMapAgent.esriaddin"
set "PF86=%ProgramFiles(x86)%"
if not defined PF86 set "PF86=%ProgramFiles%"

rem --- 0. 检查安装包是否解压了 -------------------------------------
if not exist "%SRC%" (
    echo  [错误] 没找到 ArcMapAgent.esriaddin。
    echo.
    echo    请先把压缩包完整解压到一个文件夹，再运行里面的本脚本。
    echo    直接在压缩包里双击是装不上的。
    echo.
    pause
    exit /b 1
)

rem --- 1. 检查 ArcMap 是否还在运行 ---------------------------------
tasklist /FI "IMAGENAME eq ArcMap.exe" 2>nul | find /I "ArcMap.exe" >nul
if not errorlevel 1 (
    echo  [提示] 检测到 ArcMap 正在运行。
    echo.
    echo    请先关闭所有 ArcMap 窗口再运行本脚本，
    echo    否则插件不会被正确加载。
    echo.
    pause
    exit /b 1
)

rem --- 2. 识别已安装的 ArcGIS 版本 ---------------------------------
set "AGSVER="
for /d %%d in ("%PF86%\ArcGIS\Desktop10.*") do set "AGSVER=%%~nxd"
if not defined AGSVER (
    echo  [提示] 没在默认位置找到 ArcGIS Desktop。
    echo.
    echo    如果你是装在别处、或者想先装插件再装 ArcGIS，
    echo    就按默认版本 Desktop10.4 继续。
    echo.
    set "AGSVER=Desktop10.4"
)
echo  [1/4] 检测到 ArcGIS 版本： !AGSVER!

rem --- 3. 定位「我的文档」目录（兼容 OneDrive 重定向）--------------
set "DOCS="
for /f "tokens=2,*" %%a in ('reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders" /v Personal 2^>nul ^| findstr /i "Personal"') do set "DOCS=%%b"
if not defined DOCS set "DOCS=%USERPROFILE%\Documents"
call set "DOCS=%DOCS%"
echo  [2/4] 文档目录：     !DOCS!

set "DEST=!DOCS!\ArcGIS\AddIns\!AGSVER!\!ADDINID!"
set "CACHE=%LOCALAPPDATA%\ESRI\!AGSVER!\AssemblyCache\!ADDINID!"

rem --- 4. 复制插件 ------------------------------------------------
if not exist "!DEST!" mkdir "!DEST!" 2>nul
copy /Y "%SRC%" "!DEST!\" >nul
if errorlevel 1 (
    echo  [错误] 复制失败，目标目录可能没有写入权限：
    echo         !DEST!
    echo.
    echo    可以改用「双击 ArcMapAgent.esriaddin」的方式安装。
    echo.
    pause
    exit /b 1
)
echo  [3/4] 插件已复制到： !DEST!

rem --- 5. 清掉旧缓存（不删的话 ArcMap 还会加载旧版本）---------------
if exist "!CACHE!" (
    rd /S /Q "!CACHE!" 2>nul
    if exist "!CACHE!" (
        echo  [提示] 旧缓存删除失败（可能正被占用），请重启电脑后再运行一次。
    ) else (
        echo  [4/4] 旧缓存已清理
    )
) else (
    echo  [4/4] 无需清理缓存
)

echo.
echo  ============================================================
echo    安装完成
echo  ============================================================
echo.
echo    接下来：
echo      1. 打开 ArcMap
echo      2. 菜单栏空白处右键，勾选 "ArcMap Agent" 工具条
echo      3. 点工具条上的按钮，右侧就会出现面板
echo      4. 到「模型」页填上你的 API 端点和密钥，就能开始用了
echo.
echo    详细用法见同目录下的「安装说明.md」。
echo.
pause
