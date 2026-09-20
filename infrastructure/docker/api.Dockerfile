# Control-plane API image.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first so dependency layers cache independently of source changes.
COPY services/api/Directory.Build.props services/api/Directory.Packages.props services/api/LiveStream.sln ./services/api/
COPY services/api/src/LiveStream.Domain/*.csproj ./services/api/src/LiveStream.Domain/
COPY services/api/src/LiveStream.Application/*.csproj ./services/api/src/LiveStream.Application/
COPY services/api/src/LiveStream.Infrastructure/*.csproj ./services/api/src/LiveStream.Infrastructure/
COPY services/api/src/LiveStream.Api/*.csproj ./services/api/src/LiveStream.Api/
RUN dotnet restore services/api/src/LiveStream.Api/LiveStream.Api.csproj

COPY services/api/ ./services/api/
RUN dotnet publish services/api/src/LiveStream.Api/LiveStream.Api.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Run unprivileged: the API needs no root capability.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin livestream

COPY --from=build /app .
USER 10001
EXPOSE 8080
ENTRYPOINT ["dotnet", "LiveStream.Api.dll"]
