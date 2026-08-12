# syntax=docker/dockerfile:1
# Container image for the Agnes host daemon.
#
#   docker build -t agnes-host .
#   docker run -p 5081:5081 \
#       -v agnes-data:/data \                 # event log + device tokens
#       -v /path/to/projects:/work \          # the code your agents work on
#       -v $HOME/.claude:/root/.claude:ro \   # agent credentials (example: Claude Code)
#       agnes-host
#
# TLS is terminated at a reverse proxy (see docs/deployment.md); the container serves plain
# HTTP on 5081. Agents run *inside* this container, so install the ones you use (this image
# ships Node + git for the Claude Code ACP bridge; derive your own image for others).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Agnes.Host/Agnes.Host.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Install the version-pinned ACP bridge at image build time. The running host never resolves
# or downloads executable packages from the network.
# ACP 0.16.2 pins minimatch 10.2.1; retain the ACP pin but replace that direct dependency
# with the vendor-fixed 10.2.3 release (CVE-2026-27903 / CVE-2026-27904).
RUN apt-get update \
    && apt-get install -y --no-install-recommends nodejs npm git ca-certificates \
    && npm install --global @zed-industries/claude-code-acp@0.16.2 --ignore-scripts \
    && npm install --prefix /usr/local/lib/node_modules/@zed-industries/claude-code-acp --omit=dev --ignore-scripts --save-exact minimatch@10.2.3 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 agnes \
    && useradd --uid 10001 --gid agnes --create-home --home-dir /home/agnes --shell /usr/sbin/nologin agnes \
    && install -d -o agnes -g agnes /data /work

WORKDIR /app
COPY --from=build /app .

EXPOSE 5081
ENV ASPNETCORE_URLS=http://0.0.0.0:5081 \
    Agnes__Database=/data/agnes.db \
    Agnes__DevicesFile=/data/devices.json
VOLUME ["/data", "/work"]

USER agnes

ENTRYPOINT ["dotnet", "Agnes.Host.dll"]
