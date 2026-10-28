# NoMercy MediaServer Dockerfile
# Multi-stage build for optimal image size
#
# FFmpeg is downloaded by the MediaServer at runtime with appropriate
# hardware acceleration support for the platform.

# =============================================================================
# Stage 1: Build
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy source and restore dependencies
COPY NoMercy.Server.sln ./
COPY src/ src/
COPY assets/ assets/
RUN dotnet restore src/NoMercy.Service/NoMercy.Service.csproj

# Build and publish
RUN dotnet publish src/NoMercy.Service/NoMercy.Service.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    -p:PublishSingleFile=false \
    -p:DebugType=None \
    -p:DebugSymbols=false

# =============================================================================
# Stage 2: Runtime (CPU only)
# =============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install minimal dependencies (FFmpeg is downloaded by MediaServer)
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Required for FFmpeg runtime
    libva2 \
    libva-drm2 \
    libdrm2 \
    # Media info
    mediainfo \
    # Utilities
    curl \
    ca-certificates \
    sqlite3 \
    htop \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN useradd -m -o -u 1000 nomercy

# Create data directories
RUN mkdir -p /data/config /data/cache /data/media /data/log \
    && chown -R nomercy:nomercy /data

# Copy published application
COPY --from=build /app/publish .

# Set ownership
RUN chown -R nomercy:nomercy /app

# Switch to non-root user
USER nomercy

# Environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV HOME=/data
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8

# Expose port
EXPOSE 7626

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -fsk https://127.0.0.1:7626/health || curl -fs http://127.0.0.1:7626/health || exit 1

# Entry point
ENTRYPOINT ["dotnet", "NoMercyMediaServer.dll"]
CMD ["--internal-port", "7626"]
