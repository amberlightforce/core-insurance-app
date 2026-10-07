# syntax=docker/dockerfile:1
# One image per commit for api, worker and migrate; APP_ROLE selects the mode (INFRASTRUCTURE §1).

# --- 1. Front end: React + Vite build -------------------------------------------------------------
FROM node:24.21.0-bookworm-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json web/.npmrc ./
RUN npm ci --no-audit
COPY web/ ./
# Source maps are built `hidden` (no sourceMappingURL) and are never shipped in the image.
RUN npm run build && find dist -name '*.map' -delete

# --- 2. Back end: restore and publish the Host ----------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY tools/ tools/
COPY infra/database/ infra/database/
COPY src/ src/
RUN dotnet restore src/CoreIns.Host/CoreIns.Host.csproj
RUN dotnet publish src/CoreIns.Host/CoreIns.Host.csproj --configuration Release --no-restore --output /app \
    -p:ContinuousIntegrationBuild=true

# --- 3. Runtime: ASP.NET Core, non-root -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TZ=UTC
COPY --from=build /app ./
COPY --from=web /web/dist ./wwwroot
# The base image's built-in unprivileged user (UID 1654).
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CoreIns.Host.dll"]
