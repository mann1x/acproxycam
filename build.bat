@echo off
setlocal enabledelayedexpansion

:: Set up variables
set RELEASESDIR=D:\INSTALL\acproxycam\releases
set REPODIR=%~dp0
set PUBDIR=%REPODIR%publish
set SZEXE=c:\Program Files\7-Zip\7z.exe
set PROJ=%REPODIR%src\ACProxyCam\ACProxyCam.csproj

:: Get version from csproj using PowerShell (more reliable)
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Select-Xml -Path '%PROJ%' -XPath '//Version').Node.InnerText"`) do set RELVERSION=%%i
echo The version is v%RELVERSION%
echo.

:: Create directories
if not exist "%PUBDIR%" mkdir "%PUBDIR%"
if not exist "%RELEASESDIR%" mkdir "%RELEASESDIR%"

echo Publishing linux-x64...
dotnet publish "%PROJ%" -r linux-x64 -c Release -p:PublishSingleFile=true --self-contained true -p:PublishDir=%PUBDIR%\linux-x64
if errorlevel 1 goto :error

echo Publishing linux-arm64...
dotnet publish "%PROJ%" -r linux-arm64 -c Release -p:PublishSingleFile=true --self-contained true -p:PublishDir=%PUBDIR%\linux-arm64
if errorlevel 1 goto :error

echo.
echo Creating release archives...
del "%RELEASESDIR%\*.*" /Q 2>nul

pushd "%PUBDIR%"
"%SZEXE%" a -tzip "%RELEASESDIR%\acproxycam-linux-x64-v%RELVERSION%.zip" ".\linux-x64\acproxycam"
"%SZEXE%" a -tzip "%RELEASESDIR%\acproxycam-linux-arm64-v%RELVERSION%.zip" ".\linux-arm64\acproxycam"
popd

echo Generating checksums...
"%SZEXE%" h -scrcSHA256 -ba -slfhn "%RELEASESDIR%\acproxycam-linux-x64-v%RELVERSION%.zip" >"%RELEASESDIR%\acproxycam-linux-x64-v%RELVERSION%.zip.sha256"
"%SZEXE%" h -scrcSHA256 -ba -slfhn "%RELEASESDIR%\acproxycam-linux-arm64-v%RELVERSION%.zip" >"%RELEASESDIR%\acproxycam-linux-arm64-v%RELVERSION%.zip.sha256"

echo.
echo Build completed successfully!
echo.
echo Release artifacts in %RELEASESDIR%:
dir /b "%RELEASESDIR%"
goto :end

:error
echo.
echo Build failed!

:end
endlocal
