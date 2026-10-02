# The tools image: the one-shot jobs that run beside the app. `migrate` brings the database to the
# latest migration; `seed` adds the demo data, for local runs and CI only. Build it from the
# repository root:
#   docker build -f backend/tools/AzureBank.Seeder/Dockerfile -t azurebank-tools .
# It listens on nothing, runs one command and exits: the container's arguments are the command.
# The connection string and, for `seed`, the PIN pepper come from the environment at run time
# (README.md beside this file has the variables and the exit codes).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# The project files first, so a source edit does not repeat the restore.
COPY global.json ./
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/src/AzureBank.Shared/AzureBank.Shared.csproj backend/src/AzureBank.Shared/
COPY backend/src/AzureBank.Infrastructure/AzureBank.Infrastructure.csproj backend/src/AzureBank.Infrastructure/
COPY backend/tools/AzureBank.Seeder/AzureBank.Seeder.csproj backend/tools/AzureBank.Seeder/
RUN dotnet restore backend/tools/AzureBank.Seeder/AzureBank.Seeder.csproj
COPY backend/src/AzureBank.Shared/ backend/src/AzureBank.Shared/
COPY backend/src/AzureBank.Infrastructure/ backend/src/AzureBank.Infrastructure/
COPY backend/tools/AzureBank.Seeder/ backend/tools/AzureBank.Seeder/
RUN dotnet publish backend/tools/AzureBank.Seeder/AzureBank.Seeder.csproj -c Release --no-restore -o /app

# The ASP.NET runtime, not the plain one: the Seeder uses Identity's UserManager, which lives in
# the Microsoft.AspNetCore.App shared framework.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
LABEL org.opencontainers.image.source="https://github.com/Gurgant/azurebank-v2"
USER $APP_UID
ENTRYPOINT ["dotnet", "azurebank-seeder.dll"]
