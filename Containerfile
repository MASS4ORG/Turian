# Turian CLI: headless editor automation (compile, playmode, screenshot, pick, oap)
# for gamedevs to build their Turian projects in their own CI/CD. Built with Podman.
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG VERSION=0.0.0
WORKDIR /src
COPY . .
# Same layout as the release archives: the turian-cli launcher beside lib/, which is how the CLI finds the
# engine assemblies a gamedev's project compiles against.
RUN dotnet publish Turian/Editor/CLI/Turian.Editor.CLI.csproj \
    -c Release \
    -o /app/publish/lib \
    -p:UseAppHost=false \
    -p:Version=${VERSION} -p:AssemblyVersion=${VERSION} -p:InformationalVersion=${VERSION} \
    && dotnet publish Turian/Editor/Bootstrap/Turian.Editor.Bootstrap.csproj \
    -c Release \
    -o /app/bootstrap \
    --use-current-runtime --self-contained false -p:PublishSingleFile=true \
    && mv /app/bootstrap/Turian.Editor.Bootstrap /app/publish/turian-cli

# The dotnet SDK (not just the runtime) is required at runtime: the CLI uses
# Microsoft.Build.Locator to compile a gamedev's own project via MSBuild.
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}
ARG VERSION=0.0.0
LABEL org.opencontainers.image.title="Turian CLI" \
      org.opencontainers.image.description="Headless Turian editor CLI for building Turian projects in CI/CD." \
      org.opencontainers.image.source="https://github.com/MASS4ORG/Turian" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.licenses="MPL-2.0"

# glslc: shader compilation. fontconfig/dejavu: SkiaSharp text rendering (Guinevere UI).
# libvulkan1/mesa-vulkan-drivers: software Vulkan (lavapipe) for headless screenshot/playmode.
RUN apt-get update -yqq \
    && apt-get install -yqq --no-install-recommends \
       glslc \
       libfontconfig1 fontconfig fonts-dejavu-core \
       libvulkan1 mesa-vulkan-drivers \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .
ENV PATH="/app:${PATH}"

ENTRYPOINT ["/app/turian-cli"]
CMD ["--help"]
