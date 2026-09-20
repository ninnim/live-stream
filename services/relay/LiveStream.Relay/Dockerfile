# Egress relay: pulls a session from the media gateway and republishes it to external platforms.
#
# Runs FFmpeg, so unlike the API image this one needs the encoder installed. That is precisely why
# it is a separate service — the control plane image stays free of media tooling.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY services/relay/LiveStream.Relay/LiveStream.Relay.csproj services/relay/LiveStream.Relay/
RUN dotnet restore services/relay/LiveStream.Relay/LiveStream.Relay.csproj

COPY services/relay/ services/relay/
RUN dotnet publish services/relay/LiveStream.Relay/LiveStream.Relay.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# ffmpeg is the only added package. --no-install-recommends keeps the image from pulling in a
# desktop media stack behind it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Non-root: the relay executes an external binary with operator-supplied arguments, which is the
# strongest reason in this system not to run as root.
RUN useradd --create-home --shell /usr/sbin/nologin relay \
    && chown -R relay:relay /app
USER relay

ENTRYPOINT ["dotnet", "LiveStream.Relay.dll"]
