@echo off
setlocal enabledelayedexpansion

:: ACProxyCam Docker Publish Script
:: Builds and pushes multi-architecture Docker image to registry

set REPODIR=%~dp0
set PROJ=%REPODIR%src\ACProxyCam\ACProxyCam.csproj

:: Default registry (GitHub Container Registry)
set DEFAULT_REGISTRY=ghcr.io/mann1x
set REGISTRY=%~1
if "%REGISTRY%"=="" set REGISTRY=%DEFAULT_REGISTRY%

set IMAGE_NAME=acproxycam

:: Get version from csproj
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Select-Xml -Path '%PROJ%' -XPath '//Version').Node.InnerText"`) do set VERSION=%%i
echo Publishing Docker image version: %VERSION%
echo Registry: %REGISTRY%
echo.

:: Check if Docker is available
docker version >nul 2>&1
if errorlevel 1 (
    echo Error: Docker is not installed or not running.
    goto :error
)

:: Check if buildx is available
docker buildx version >nul 2>&1
if errorlevel 1 (
    echo Error: Docker buildx is not available.
    goto :error
)

:: Check if logged in to registry
if "%REGISTRY%"=="%DEFAULT_REGISTRY%" (
    echo Checking GitHub Container Registry login...
    echo Please ensure you are logged in with: docker login ghcr.io
    echo.
)

:: Create/use builder
docker buildx inspect acproxycam-builder >nul 2>&1
if errorlevel 1 (
    echo Creating Docker buildx builder...
    docker buildx create --name acproxycam-builder --use
) else (
    docker buildx use acproxycam-builder
)

echo.
echo Building and pushing multi-architecture Docker image...
echo Platforms: linux/amd64, linux/arm64
echo.

:: Build and push to registry
docker buildx build ^
    --platform linux/amd64,linux/arm64 ^
    --tag %REGISTRY%/%IMAGE_NAME%:%VERSION% ^
    --tag %REGISTRY%/%IMAGE_NAME%:latest ^
    --file docker/Dockerfile ^
    --push ^
    %REPODIR%

if errorlevel 1 goto :error

echo.
echo Successfully published to registry!
echo.
echo Images:
echo   %REGISTRY%/%IMAGE_NAME%:%VERSION%
echo   %REGISTRY%/%IMAGE_NAME%:latest
echo.
echo Pull with:
echo   docker pull %REGISTRY%/%IMAGE_NAME%:latest
echo.
goto :end

:error
echo.
echo Publish failed!
echo.
echo Common issues:
echo - Not logged in: docker login ghcr.io
echo - No push permission to registry
echo - Network/firewall blocking connection
exit /b 1

:end
endlocal
