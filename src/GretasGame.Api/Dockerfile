# syntax=docker/dockerfile:1.7
# Build context: repository root (gretas-game-api/)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/GretasGame.ServiceDefaults/GretasGame.ServiceDefaults.csproj src/GretasGame.ServiceDefaults/
COPY src/GretasGame.Api/GretasGame.Api.csproj src/GretasGame.Api/
RUN dotnet restore src/GretasGame.Api/GretasGame.Api.csproj
COPY src/GretasGame.ServiceDefaults/ src/GretasGame.ServiceDefaults/
COPY src/GretasGame.Api/ src/GretasGame.Api/
RUN dotnet publish src/GretasGame.Api/GretasGame.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "GretasGame.Api.dll"]
