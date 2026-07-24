# syntax=docker/dockerfile:1

# Native AOT and trimming are not options here: Razor Pages and EF Core both declare themselves
# incompatible. Size comes from a RID-specific publish (which drops ~84 MB of native SQLite builds for
# platforms we do not target) and from a chiselled runtime image with no shell or package manager.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
ARG VERSION=0.1.0
WORKDIR /src

# Restore against the project files alone so the layer is reused when only sources change.
COPY Directory.Build.props ./
COPY SimpleRadius/SimpleRadius.csproj SimpleRadius/
RUN dotnet restore SimpleRadius/SimpleRadius.csproj -r linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)

COPY SimpleRadius/ SimpleRadius/
RUN dotnet publish SimpleRadius/SimpleRadius.csproj \
      -c Release \
      -r linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
      --self-contained false \
      --no-restore \
      -p:Version=${VERSION} \
      -p:PublishSingleFile=false \
      -o /app \
    && rm -f /app/*.pdb

# "chiseled" is a distroless-style image: no shell, no package manager, non-root by default.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS runtime
WORKDIR /app

ARG VERSION=0.1.0
LABEL org.opencontainers.image.title="SimpleRadius" \
      org.opencontainers.image.description="RADIUS server with dynamic VLAN assignment, accounting and a web admin UI" \
      org.opencontainers.image.source="https://github.com/XtremeOwnage/SimpleRadius" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}"

ENV RadiusServerSettings__DatabasePath=/data/simple-radius.db \
    RadiusServerSettings__WebUiUrl=http://0.0.0.0:8080 \
    RadiusServerSettings__ListenAddress=0.0.0.0 \
    RadiusServerSettings__AuthPort=1812 \
    RadiusServerSettings__AcctPort=1813 \
    DOTNET_gcServer=0

# The database, Data Protection keys and any log files live here; mount a volume or they are lost.
VOLUME ["/data"]

EXPOSE 8080/tcp
EXPOSE 1812/udp
EXPOSE 1813/udp

COPY --from=build /app .

# The image has no shell and no curl, so the health check runs the app in probe mode instead. Exec form
# is required: without a shell there is nothing to parse a command string.
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "SimpleRadius.dll", "--healthcheck"]

USER $APP_UID

ENTRYPOINT ["dotnet", "SimpleRadius.dll"]
