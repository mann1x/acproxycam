# ACProxyCam Docker Image
# Multi-stage build for minimal image size

# Stage 1: Build .NET application
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH

WORKDIR /src

# Copy project file and restore dependencies
COPY src/ACProxyCam/ACProxyCam.csproj ./
RUN dotnet restore -r linux-${TARGETARCH}

# Copy source code and build
COPY src/ACProxyCam/ ./
RUN dotnet publish -c Release -r linux-${TARGETARCH} --self-contained true \
    -p:PublishSingleFile=true -p:PublishTrimmed=false \
    -o /publish

# Stage 2: Runtime image
# Use Bookworm for Janus (not available in Trixie) + Trixie repo for FFmpeg 7.x
FROM debian:bookworm-slim

ARG TARGETARCH

# Install runtime dependencies
# Janus is only in Bookworm, FFmpeg 7.x is in Trixie
RUN apt-get update && apt-get install -y --no-install-recommends \
    # SkiaSharp dependencies
    libfontconfig1 libfreetype6 \
    # WebRTC gateway and Lua dependencies (from Bookworm)
    janus lua-cjson lua-ansicolors \
    # s6-overlay dependencies
    xz-utils \
    # Utilities
    curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    # Create json.lua alias for cjson (Janus expects 'json' module)
    && echo 'return require("cjson")' > /usr/share/lua/5.3/json.lua

# Install FFmpeg 7.x from Trixie (needed for FFmpeg.AutoGen 7.0.0)
RUN echo "deb http://deb.debian.org/debian trixie main" > /etc/apt/sources.list.d/trixie.list \
    && echo 'Package: *\nPin: release n=trixie\nPin-Priority: 100' > /etc/apt/preferences.d/trixie \
    && echo 'Package: libavcodec61 libavformat61 libavutil59 libswscale8 libswresample5\nPin: release n=trixie\nPin-Priority: 500' >> /etc/apt/preferences.d/trixie \
    && apt-get update \
    && apt-get install -y --no-install-recommends -t trixie \
        libavcodec61 libavformat61 libavutil59 libswscale8 \
    && rm -f /etc/apt/sources.list.d/trixie.list /etc/apt/preferences.d/trixie \
    && rm -rf /var/lib/apt/lists/*

# Install ttyd from GitHub releases (not available in Debian repos)
ARG TTYD_VERSION=1.7.7
RUN TTYD_ARCH=$(case "${TARGETARCH}" in \
        amd64) echo "x86_64" ;; \
        arm64) echo "aarch64" ;; \
        *) echo "${TARGETARCH}" ;; \
    esac) && \
    curl -fsSL "https://github.com/tsl0922/ttyd/releases/download/${TTYD_VERSION}/ttyd.${TTYD_ARCH}" -o /usr/local/bin/ttyd && \
    chmod +x /usr/local/bin/ttyd

# Install s6-overlay v3.1.6.2
ARG S6_OVERLAY_VERSION=3.1.6.2

# Map Docker's TARGETARCH to s6-overlay's naming convention
# Docker uses: amd64, arm64
# s6-overlay uses: x86_64, aarch64
RUN S6_ARCH=$(case "${TARGETARCH}" in \
        amd64) echo "x86_64" ;; \
        arm64) echo "aarch64" ;; \
        *) echo "${TARGETARCH}" ;; \
    esac) && \
    curl -fsSL "https://github.com/just-containers/s6-overlay/releases/download/v${S6_OVERLAY_VERSION}/s6-overlay-noarch.tar.xz" -o /tmp/s6-overlay-noarch.tar.xz && \
    curl -fsSL "https://github.com/just-containers/s6-overlay/releases/download/v${S6_OVERLAY_VERSION}/s6-overlay-${S6_ARCH}.tar.xz" -o /tmp/s6-overlay-arch.tar.xz && \
    tar -C / -Jxpf /tmp/s6-overlay-noarch.tar.xz && \
    tar -C / -Jxpf /tmp/s6-overlay-arch.tar.xz && \
    rm -f /tmp/s6-overlay-*.tar.xz

# Copy s6 service definitions
COPY docker/rootfs/ /

# Fix line endings for s6 service files (Windows CRLF -> Unix LF) and set execute permissions
RUN find /etc/s6-overlay -type f -exec sed -i 's/\r$//' {} \; && \
    find /etc/cont-init.d -type f -exec sed -i 's/\r$//' {} \; && \
    chmod +x /etc/cont-init.d/* && \
    chmod +x /etc/s6-overlay/s6-rc.d/*/run 2>/dev/null || true && \
    chmod +x /etc/s6-overlay/s6-rc.d/*/finish 2>/dev/null || true

# Copy application binary
COPY --from=build /publish/acproxycam /usr/local/bin/acproxycam
RUN chmod +x /usr/local/bin/acproxycam

# Create required directories
RUN mkdir -p /etc/acproxycam /run/acproxycam

# Environment variables
ENV TTYD_PORT=7681 \
    TTYD_USER="" \
    TTYD_PASS="" \
    JANUS_ENABLED=true \
    # s6-overlay settings
    S6_KEEP_ENV=1 \
    S6_BEHAVIOUR_IF_STAGE2_FAILS=2

# Expose ports
# 7681: ttyd web terminal
# 8080+: MJPEG streams (per printer, configured in acproxycam)
# 8188: Janus WebSocket API
EXPOSE 7681 8080 8188

# Volume for persistent configuration
VOLUME ["/etc/acproxycam"]

# Health check
HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD curl -f http://localhost:${TTYD_PORT}/ || exit 1

# Use s6-overlay as init
ENTRYPOINT ["/init"]
