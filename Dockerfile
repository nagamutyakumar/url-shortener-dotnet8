# syntax=docker/dockerfile:1
# Multi-stage build for the URL shortener API. Targets:
#   api      (default) - the API on the ASP.NET Core runtime, as a non-root user, port 8080
#   migrator           - an EF Core migration bundle that applies pending migrations and exits
# See docs/docker.md.

ARG DOTNET_VERSION=8.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore first, from the project file only, so the layer is cached until dependencies change.
COPY src/UrlShortener.Api/UrlShortener.Api.csproj src/UrlShortener.Api/
RUN dotnet restore src/UrlShortener.Api/UrlShortener.Api.csproj

COPY src/ src/
RUN dotnet publish src/UrlShortener.Api/UrlShortener.Api.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM build AS migrator-build
# Same dotnet-ef version as the EF Core packages.
RUN dotnet tool install --global dotnet-ef --version 8.0.31
ENV PATH="${PATH}:/root/.dotnet/tools"
# Program requires a connection string to build its host, which dotnet-ef does to find the
# DbContext. This placeholder has no credentials and is never connected to; the real one is
# supplied at run time.
RUN ConnectionStrings__UrlShortener="Server=build-placeholder;Database=UrlShortener" \
    dotnet ef migrations bundle \
        --project src/UrlShortener.Api/UrlShortener.Api.csproj \
        --configuration Release --output /app/migrator/efbundle --verbose

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS migrator
WORKDIR /app
COPY --from=migrator-build /app/migrator/ ./
# The bundle reads ConnectionStrings__UrlShortener through the app's own configuration.
USER $APP_UID
ENTRYPOINT ["./efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS api
WORKDIR /app
COPY --from=build /app/publish ./
# The .NET 8 images listen on 8080 (ASPNETCORE_HTTP_PORTS) and ship a non-root "app" user.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "UrlShortener.Api.dll"]
