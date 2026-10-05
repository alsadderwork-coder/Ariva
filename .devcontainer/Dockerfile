# Dev container for Codespaces and cloud agent sessions (ARV-076): every gate of scripts/verify.mjs runs inside it.
# Every input of this Dockerfile is an image pinned by version and digest (scripts/base-images.mjs checks the FROM lines),
# and nothing is downloaded while it builds (the docker-outside-of-docker feature in devcontainer.json adds a layer that
# installs the Docker CLI 29.4.3 from Docker's signed apt repository, pinned by version): the .NET SDK is the one the host images build with, Node 22 is CI's major version, the
# Playwright image brings Chromium for @playwright/test 1.63.0 (the version in Ariva.E2E's package-lock.json) with the
# libraries it needs and the same image the visual baselines are rendered in, uv runs the semgrep and postgres MCP
# servers, the Docker CLI with Compose reaches the Docker engine the dev container mounts (Testcontainers, Compose), and
# Helm 3.19.0 (CI's version) renders the chart for the chart security gate.
FROM mcr.microsoft.com/dotnet/sdk:10.0.103@sha256:e362a8dbcd691522456da26a5198b8f3ca1d7641c95624fadc5e3e82678bd08a AS dotnet
FROM node:22.23.3-bookworm-slim@sha256:43ac6c60b8f89723f746e8a92ce91abd5017e627ce1ddfe4238355d3a30b772c AS node
FROM ghcr.io/astral-sh/uv:0.12.23@sha256:61d393e44e249f2e4b526b6c7ddcecce245946826e608e11c93ad4f5bba55b21 AS uv
FROM docker:29.4.3-cli@sha256:51e23845f5caff1e688a2fae003b0c69d635c9200ad544731db1593731df1d3a AS docker
FROM alpine/helm:4.3.0@sha256:a6cf54599ccb99d90cf0712b30f03fdb3cab062e6b94e0418cc4db7e8a1464b2 AS helm

FROM mcr.microsoft.com/playwright:v1.63.0-noble@sha256:eff16c30e6f3f4af0a03fa4b706120d5e9b0891c344a27d64559aff5900a4a27

ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PACT_DO_NOT_TRACK=true

COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
# Node 22 ahead of the image's own Node in PATH (/usr/local/bin before /usr/bin).
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
COPY --from=uv /uv /uvx /usr/local/bin/
COPY --from=docker /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker /usr/local/libexec/docker/cli-plugins /usr/local/libexec/docker/cli-plugins
COPY --from=helm /usr/bin/helm /usr/local/bin/helm

RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
 && ln -s ../lib/node_modules/corepack/dist/corepack.js /usr/local/bin/corepack \
 && dotnet --version && node --version && npm --version && uv --version && docker --version && docker compose version \
 && helm version --short

# The non-root user of the Playwright image (CWE-269): no sudo; the Docker engine is reached through the mounted socket.
USER pwuser
WORKDIR /workspaces/Ariva
