# The API image. Build it from the repository root:
#   docker build -f backend/src/AzureBank.Api/Dockerfile -t azurebank-api .
# It listens on 8080 unless ASPNETCORE_URLS says otherwise, and every secret comes from the
# environment at run time -- none is in the image (engineering-practices.md, "Local setup").

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# The project files first, so a source edit does not repeat the restore.
COPY global.json ./
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/src/AzureBank.Shared/AzureBank.Shared.csproj backend/src/AzureBank.Shared/
COPY backend/src/AzureBank.Infrastructure/AzureBank.Infrastructure.csproj backend/src/AzureBank.Infrastructure/
COPY backend/src/AzureBank.Api/AzureBank.Api.csproj backend/src/AzureBank.Api/
RUN dotnet restore backend/src/AzureBank.Api/AzureBank.Api.csproj
COPY backend/src/AzureBank.Shared/ backend/src/AzureBank.Shared/
COPY backend/src/AzureBank.Infrastructure/ backend/src/AzureBank.Infrastructure/
COPY backend/src/AzureBank.Api/ backend/src/AzureBank.Api/
RUN dotnet publish backend/src/AzureBank.Api/AzureBank.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AzureBank.Api.dll"]
