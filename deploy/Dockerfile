# ControlHome container image: the ASP.NET Core API serving the built Vue SPA from the same origin.
#
#   docker build -f deploy/Dockerfile -t controlhome:local .                      # from the repository root
#   docker buildx build --platform linux/amd64,linux/arm64 -f deploy/Dockerfile .
#
# The web and .NET build stages always run on the build machine's platform (--platform=$BUILDPLATFORM): the SPA is
# platform-neutral and `dotnet publish -a $TARGETARCH` cross-compiles, so multi-arch builds need no QEMU. Only the final
# stage uses the target platform, and it runs no commands.
#
# Every FROM is pinned by tag and multi-arch index digest (Dependabot, docker ecosystem, keeps both current).

# ---------------------------------------------------------------------------------------------------------------------
# web: npm workspace -> web/apps/web/dist
# ---------------------------------------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM node:24.21.0-alpine@sha256:ebfe2f90462722a7a4de65e91990e97fe0d401c70e0e762c5b53302f905ec1c1 AS web
WORKDIR /src/web
ENV CI=true \
    NPM_CONFIG_AUDIT=false \
    NPM_CONFIG_FUND=false \
    NPM_CONFIG_UPDATE_NOTIFIER=false

# Only what `npm ci` needs (manifests + lockfile), so the dependency layer is reused until one of them changes.
COPY web/package.json web/package-lock.json web/.npmrc ./
COPY web/apps/web/package.json apps/web/
COPY web/packages/api-client/package.json packages/api-client/
COPY web/packages/ui/package.json packages/ui/
RUN --mount=type=cache,id=controlhome-npm,target=/root/.npm,sharing=locked \
    npm ci

COPY web/ ./
RUN npm run build

# ---------------------------------------------------------------------------------------------------------------------
# build: dotnet publish for the target architecture -> /out
# ---------------------------------------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
ARG TARGETARCH
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# An empty /data for the final stage (it has no shell to create it). See the COPY there.
RUN mkdir -p /rootfs/data

# Restore from the project files only (layer reused until a project file or a package pin changes).
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ControlHome.Domain/ControlHome.Domain.csproj src/ControlHome.Domain/
COPY src/ControlHome.Connectors.Abstractions/ControlHome.Connectors.Abstractions.csproj src/ControlHome.Connectors.Abstractions/
COPY src/ControlHome.Connectors.Simulated/ControlHome.Connectors.Simulated.csproj src/ControlHome.Connectors.Simulated/
COPY src/ControlHome.Connectors.Mqtt/ControlHome.Connectors.Mqtt.csproj src/ControlHome.Connectors.Mqtt/
COPY src/ControlHome.Application/ControlHome.Application.csproj src/ControlHome.Application/
COPY src/ControlHome.Infrastructure/ControlHome.Infrastructure.csproj src/ControlHome.Infrastructure/
COPY src/ControlHome.Api/ControlHome.Api.csproj src/ControlHome.Api/
RUN --mount=type=cache,id=controlhome-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/ControlHome.Api/ControlHome.Api.csproj -a "$TARGETARCH"

# The committed web/packages/api-client/openapi.json is the source of the API client: no build-time OpenAPI generation here
# (it would run the freshly built app, which cannot work when cross-compiling).
COPY src/ src/
# Optional version override (release.yml): VERSION=0.1.0 for a tag, or VERSION_SUFFIX=edge.42 on main. Unset, the
# version is Directory.Build.props' VersionPrefix.
ARG VERSION=
ARG VERSION_SUFFIX=
RUN --mount=type=cache,id=controlhome-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/ControlHome.Api/ControlHome.Api.csproj \
        -c Release \
        -a "$TARGETARCH" \
        --no-restore \
        -o /out \
        -p:UseAppHost=false \
        -p:OpenApiGenerateDocuments=false \
        -p:OpenApiGenerateDocumentsOnBuild=false \
        ${VERSION:+"-p:Version=$VERSION"} \
        ${VERSION_SUFFIX:+"-p:VersionSuffix=$VERSION_SUFFIX"}

# ---------------------------------------------------------------------------------------------------------------------
# final: chiseled ASP.NET Core runtime (non-root, no shell or package manager; "extra" adds ICU and tzdata)
# ---------------------------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374 AS final

LABEL org.opencontainers.image.title="ControlHome" \
      org.opencontainers.image.description="Self-hosted home control and automation for the LAN (ASP.NET Core API + Vue SPA)." \
      org.opencontainers.image.source="https://github.com/RicardoZambon/ControlHome" \
      org.opencontainers.image.url="https://github.com/RicardoZambon/ControlHome" \
      org.opencontainers.image.licenses="Apache-2.0"

ENV ASPNETCORE_HTTP_PORTS=8711 \
    ControlHome__DataPath=/data \
    DOTNET_RUNNING_IN_CONTAINER=true

WORKDIR /app
COPY --from=build /out/ /app/
COPY --from=web /src/web/apps/web/dist/ /app/wwwroot/
# /data (mode 0700) owned by the default non-root user (APP_UID 1654), so the image also runs without a mount and a new
# named volume mounted there inherits that owner. Compose bind-mounts ./data instead and may run as any UID.
COPY --from=build --chown=1654:1654 --chmod=0700 /rootfs/ /

# No VOLUME: compose mounts /data (and the app only writes to /data and /tmp, so the root filesystem can be read-only).
USER 1654:1654
EXPOSE 8711

# Exec form; the probe reads ASPNETCORE_HTTP_PORTS, so a changed port needs no change here.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --start-interval=5s --retries=3 \
    CMD ["dotnet", "/app/ControlHome.Api.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "/app/ControlHome.Api.dll"]
