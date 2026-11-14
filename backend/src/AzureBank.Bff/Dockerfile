# The BFF image, with the built SPA it serves (ADR-0054). Build it from the repository root:
#   docker build -f backend/src/AzureBank.Bff/Dockerfile -t azurebank-bff .
# It listens on 8080 unless ASPNETCORE_URLS says otherwise; its one secret, the service key, comes
# from the environment at run time.

# The patch is pinned: a cached node:24 image older than 24.12.0 carries a Corepack that cannot
# start pnpm 12, and compose up --build does not pull a newer one.
FROM node:24.22.3-bookworm-slim AS spa
WORKDIR /src/frontend
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./
ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0
RUN corepack enable pnpm
RUN pnpm install --frozen-lockfile
COPY frontend/ ./
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/src/AzureBank.Shared/AzureBank.Shared.csproj backend/src/AzureBank.Shared/
COPY backend/src/AzureBank.Bff/AzureBank.Bff.csproj backend/src/AzureBank.Bff/
RUN dotnet restore backend/src/AzureBank.Bff/AzureBank.Bff.csproj
COPY backend/src/AzureBank.Shared/ backend/src/AzureBank.Shared/
COPY backend/src/AzureBank.Bff/ backend/src/AzureBank.Bff/
RUN dotnet publish backend/src/AzureBank.Bff/AzureBank.Bff.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=spa /src/frontend/dist ./spa
ENV Spa__RootPath=/app/spa
LABEL org.opencontainers.image.source="https://github.com/Gurgant/azurebank-v2"
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AzureBank.Bff.dll"]
