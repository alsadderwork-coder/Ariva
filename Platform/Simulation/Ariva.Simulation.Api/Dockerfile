# Use runtime-deps for self-contained deployment

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.13@sha256:da5846024d0008b48259b53a8953d39381fe5b7ec99ef1449ba4ba927f201490 AS base

# OS security fixes published after Microsoft built the base image (Trivy gate in images.yml). The digest pins the
# starting point; Dependabot moves it forward when Microsoft rebuilds.
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
EXPOSE 8080

# The SDK that bundles the runtime-deps version above: a self-contained publish ships the runtime packs of the SDK's own
# runtime, whatever the base says (SDK 10.0.402 bundles 10.0.13; 10.0.103 bundled 10.0.3). MCR publishes patches of the
# newest feature band only, so the band moves with the runtime (ARV-184).
FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src

# Package sources (nuget.org only, with package source mapping), Central Package Management and the shared build settings
COPY ["nuget.config", "Directory.Packages.props", "Directory.Build.props", "./"]

# Every project of the simulator's closure with its lock file (ARV-096). The restore runs in locked mode: a package or
# version the lock files do not name, or a package whose content differs from its recorded hash, fails the build.
# scripts/base-images.mjs checks that this list is the whole closure and that each lock file records linux-x64.
COPY ["Platform/Simulation/Ariva.Simulation.Api/Ariva.Simulation.Api.csproj", "Platform/Simulation/Ariva.Simulation.Api/packages.lock.json", "Platform/Simulation/Ariva.Simulation.Api/"]
COPY ["Platform/Business/Ariva.Business.Contracts/Ariva.Business.Contracts.csproj", "Platform/Business/Ariva.Business.Contracts/packages.lock.json", "Platform/Business/Ariva.Business.Contracts/"]
COPY ["Platform/Backplane/Ariva.ServiceDefaults/Ariva.ServiceDefaults.csproj", "Platform/Backplane/Ariva.ServiceDefaults/packages.lock.json", "Platform/Backplane/Ariva.ServiceDefaults/"]

# Restore packages with caching, held to the lock files
RUN --mount=type=cache,id=nuget,sharing=locked,target=/root/.nuget/packages \
    dotnet restore "Platform/Simulation/Ariva.Simulation.Api/Ariva.Simulation.Api.csproj" -r linux-x64 --locked-mode

# Copy all source code including shared files
COPY Platform Platform
COPY GlobalSuppressions.cs GlobalSuppressions.cs

# Build and publish; the restore that publish runs is held to the lock files as well
FROM build AS publish
WORKDIR /src/Platform/Simulation/Ariva.Simulation.Api

RUN --mount=type=cache,id=nuget,sharing=locked,target=/root/.nuget/packages \
    dotnet publish "Ariva.Simulation.Api.csproj" -r linux-x64 -c Release -o /app/publish -p:PublishTrimmed=false -p:PublishReadyToRun=false --self-contained true -p:RestoreLockedMode=true

# ARV-184 (CWE-1395): check what ships, not what was asked for. Fails the build unless both runtime packs in the publish
# output are the runtime-deps version (10.0.13 or later) and System.Security.Cryptography.Pkcs.dll and the SignalR
# assemblies are at or above the file version of the release that fixed their advisories (scripts/published-runtime.mjs;
# scripts/base-images.mjs holds --runtime to the runtime-deps tag). The final stage copies the checked output only.
FROM node:22.23.3-alpine3.24@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS checked
COPY ["scripts/published-runtime.mjs", "/check/"]
COPY --from=publish /app/publish /app/publish
RUN node /check/published-runtime.mjs --check /app/publish --runtime 10.0.13

# Final stage
FROM base AS final
WORKDIR /app
COPY --from=checked /app/publish .

# Create non-root user for security
RUN groupadd -r -g 10001 nonroot \
    && useradd -r -u 10001 -g nonroot nonroot \
    && chown -R nonroot:nonroot /app
USER 10001
ENTRYPOINT ["./Ariva.Simulation.Api"]
