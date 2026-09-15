# syntax=docker/dockerfile:1.7
# Build context: repository root (gretas-game-api/)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/GretasGame.ServiceDefaults/GretasGame.ServiceDefaults.csproj src/GretasGame.ServiceDefaults/
COPY src/GretasGame.Migrations/GretasGame.Migrations.csproj src/GretasGame.Migrations/
COPY src/GretasGame.MigrationService/GretasGame.MigrationService.csproj src/GretasGame.MigrationService/
RUN dotnet restore src/GretasGame.MigrationService/GretasGame.MigrationService.csproj
COPY src/GretasGame.ServiceDefaults/ src/GretasGame.ServiceDefaults/
COPY src/GretasGame.Migrations/ src/GretasGame.Migrations/
COPY src/GretasGame.MigrationService/ src/GretasGame.MigrationService/
RUN dotnet publish src/GretasGame.MigrationService/GretasGame.MigrationService.csproj -c Release -o /app --no-restore

# ServiceDefaults references the ASP.NET shared framework, so use the aspnet runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "GretasGame.MigrationService.dll"]
