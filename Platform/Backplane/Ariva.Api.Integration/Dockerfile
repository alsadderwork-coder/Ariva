# Use runtime-deps for self-contained deployment

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.12@sha256:099f6f87ed745377dd27bd722f0d1a352bca71b4fddaabfd75e7c064bcaa82da AS base

# OS security fixes published after Microsoft built the base image (Trivy gate in images.yml). The digest pins the
# starting point; Dependabot moves it forward when Microsoft rebuilds.
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

# Copy Directory.Packages.props for Central Package Management
COPY ["Directory.Packages.props", "./"]
COPY ["Directory.Build.props", "./"]

# Copy all project files for restore
COPY ["Platform/Backplane/Ariva.Api.Integration/Ariva.Api.Integration.csproj", "Platform/Backplane/Ariva.Api.Integration/"]
COPY ["Platform/Backplane/Ariva.Api.Common/Ariva.Api.Common.csproj", "Platform/Backplane/Ariva.Api.Common/"]
COPY ["Platform/Backplane/Ariva.ServiceDefaults/Ariva.ServiceDefaults.csproj", "Platform/Backplane/Ariva.ServiceDefaults/"]
COPY ["Platform/Backplane/Ariva.Di/Ariva.Di.csproj", "Platform/Backplane/Ariva.Di/"]
COPY ["Platform/Backplane/Ariva.Infra/Ariva.Infra.csproj", "Platform/Backplane/Ariva.Infra/"]
COPY ["Platform/Backplane/Ariva.Core/Ariva.Core.csproj", "Platform/Backplane/Ariva.Core/"]
COPY ["Platform/Backplane/Ariva.Utilities/Ariva.Utilities.csproj", "Platform/Backplane/Ariva.Utilities/"]

# Restore packages with caching
RUN --mount=type=cache,id=nuget,sharing=locked,target=/root/.nuget/packages \
    dotnet restore "Platform/Backplane/Ariva.Api.Integration/Ariva.Api.Integration.csproj" -r linux-x64

# Copy all source code including shared files
COPY Platform Platform
COPY GlobalSuppressions.cs GlobalSuppressions.cs

# Build and publish
FROM build AS publish
WORKDIR /src/Platform/Backplane/Ariva.Api.Integration

RUN --mount=type=cache,id=nuget,sharing=locked,target=/root/.nuget/packages \
    dotnet publish "Ariva.Api.Integration.csproj" -r linux-x64 -c Release -o /app/publish -p:PublishTrimmed=false -p:PublishReadyToRun=false --self-contained true

# Final stage
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create non-root user for security
RUN groupadd -r -g 10001 nonroot \
    && useradd -r -u 10001 -g nonroot nonroot \
    && chown -R nonroot:nonroot /app
USER 10001
ENTRYPOINT ["./Ariva.Api.Integration"]
