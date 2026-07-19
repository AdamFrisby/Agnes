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
# Node (for `npx @zed-industries/claude-code-acp`) and git (for worktree isolation).
RUN apt-get update \
    && apt-get install -y --no-install-recommends nodejs npm git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

EXPOSE 5081
ENV ASPNETCORE_URLS=http://0.0.0.0:5081 \
    Agnes__Database=/data/agnes.db \
    Agnes__DevicesFile=/data/devices.json
VOLUME ["/data", "/work"]

ENTRYPOINT ["dotnet", "Agnes.Host.dll"]
