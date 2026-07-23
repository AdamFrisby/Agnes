# syntax=docker/dockerfile:1
# Container image for the Agnes relay — a blind byte broker that lets clients reach a NAT'd host
# without either side opening an inbound port. It never parses TLS or Agnes.Protocol; it only pairs
# a client to the host that claimed a host-id and forwards opaque bytes.
#
#   docker build -f src/Agnes.Relay/Dockerfile -t agnes-relay .
#   docker run -p 5100:5100 \
#       -v $PWD/authorized_hosts:/etc/agnes/authorized_hosts:ro \  # allow-list of host public keys
#       -e AGNES_RELAY_AUTHORIZED_KEYS=/etc/agnes/authorized_hosts \
#       agnes-relay
#
# There is nothing to terminate TLS here: the client<->host TLS handshake and all app data flow
# INSIDE the opaque stream the relay forwards. The relay is deliberately trusted with as little as
# possible.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Agnes.Relay/Agnes.Relay.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

EXPOSE 5100
ENV AGNES_RELAY_LISTEN=0.0.0.0 \
    AGNES_RELAY_PORT=5100
VOLUME ["/etc/agnes"]

ENTRYPOINT ["dotnet", "agnes-relay.dll"]
