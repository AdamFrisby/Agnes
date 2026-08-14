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

FROM build AS web-ui-build
# The browser client is deliberately a separate target.  Most hosts only need
# the protocol daemon; an operator deployment can opt into the same-origin UI
# with `--target web-ui-host` and `Agnes__WebRoot=/app/wwwroot`.
ARG AGNES_ENABLE_PWA=false
# The framework bundle's directory name is stable across an application-only change. Keep the
# bootstrap/config query version separate so a browser that cached an earlier PWA bootstrap loads
# the protected-admin variant on its next navigation.
ARG AGNES_UI_ASSET_REVISION=cf-access-v2
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && ln -s /usr/bin/python3 /usr/local/bin/python \
    && rm -rf /var/lib/apt/lists/*
RUN dotnet workload install wasm-tools \
    && dotnet publish src/Agnes.App/Agnes.App/Agnes.App.csproj \
        -f net10.0-browserwasm -c Release -o /web \
    && if [ "$AGNES_ENABLE_PWA" != "true" ]; then \
         for config in /web/wwwroot/package_*/uno-config.js; do \
           sed -i 's/config.enable_pwa = true;/config.enable_pwa = false;/' "$config"; \
           bootstrap="${config%/uno-config.js}/uno-bootstrap.js"; \
           sed -i "s|import('./uno-config.js')|import('./uno-config.js?access-build=$AGNES_UI_ASSET_REVISION')|" "$bootstrap"; \
         done; \
         sed -i '/<link rel="manifest"/d; s|/uno-bootstrap.js"|/uno-bootstrap.js?access-build='"$AGNES_UI_ASSET_REVISION"'"|' /web/wwwroot/index.html; \
       fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-base
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

# Use this target for the optional browser operator UI.  The normal browser
# route is still `/`; Agnes does not expose a separate `/admin` endpoint.
FROM runtime-base AS web-ui-host
# The host deliberately runs as the unprivileged `agnes` user. Uno's publish output
# includes a few owner-readable files, so make the copied browser bundle readable by
# that user as well; otherwise the root document loads but the browser fails on its
# first manifest asset.
COPY --from=web-ui-build --chown=agnes:agnes /web/wwwroot /app/wwwroot

# Keep the default Docker image API/desktop-client-only so existing headless
# installations do not gain a browser UI merely by rebuilding.
FROM runtime-base AS host-only
