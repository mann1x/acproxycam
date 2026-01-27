@echo off
setlocal enabledelayedexpansion

:: ACProxyCam Docker Build Script
:: Builds multi-architecture Docker image locally

set REPODIR=%~dp0
set PROJ=%REPODIR%src\ACProxyCam\ACProxyCam.csproj
set IMAGE_NAME=acproxycam

:: Get version from csproj
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Select-Xml -Path '%PROJ%' -XPath '//Version').Node.InnerText"`) do set VERSION=%%i
echo Building Docker image version: %VERSION%
echo.

:: Check if Docker is available
docker version >nul 2>&1
if errorlevel 1 (
    echo Error: Docker is not installed or not running.
    echo Please install Docker Desktop or start the Docker daemon.
    goto :error
)

:: Check if buildx is available
docker buildx version >nul 2>&1
if errorlevel 1 (
    echo Error: Docker buildx is not available.
    echo Please install Docker buildx plugin.
    goto :error
)

:: Create builder if it doesn't exist
docker buildx inspect acproxycam-builder >nul 2>&1
if errorlevel 1 (
    echo Creating Docker buildx builder...
    docker buildx create --name acproxycam-builder --use
) else (
    docker buildx use acproxycam-builder
)

echo.
echo Building multi-architecture Docker image...
echo Platforms: linux/amd64, linux/arm64
echo.

:: Build for multiple platforms and load to local Docker
:: Note: --load only works for single platform, so we build separately for local testing
docker buildx build ^
    --platform linux/amd64,linux/arm64 ^
    --tag %IMAGE_NAME%:%VERSION% ^
    --tag %IMAGE_NAME%:latest ^
    --file docker/Dockerfile ^
    --push=false ^
    %REPODIR%

if errorlevel 1 goto :error

echo.
echo Build completed successfully!
echo.
echo To load a single platform for local testing:
echo   docker buildx build --platform linux/amd64 --load -t %IMAGE_NAME%:latest -f docker/Dockerfile .
echo.
echo To push to registry:
echo   publish_docker.bat
echo.
goto :end

:error
echo.
echo Build failed!
exit /b 1

:end
endlocal
