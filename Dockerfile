# syntax=docker/dockerfile:1
# TriggerCheck web app + monitoring engine (single process). See docs/DEPLOYMENT.md.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first (cached while only sources change).
COPY global.json TriggerCheck.sln ./
COPY src/TriggerCheck.Domain/TriggerCheck.Domain.csproj src/TriggerCheck.Domain/
COPY src/TriggerCheck.Infrastructure/TriggerCheck.Infrastructure.csproj src/TriggerCheck.Infrastructure/
COPY src/TriggerCheck.Monitoring/TriggerCheck.Monitoring.csproj src/TriggerCheck.Monitoring/
COPY src/TriggerCheck.Ai/TriggerCheck.Ai.csproj src/TriggerCheck.Ai/
COPY src/TriggerCheck.Migrations.Sqlite/TriggerCheck.Migrations.Sqlite.csproj src/TriggerCheck.Migrations.Sqlite/
COPY src/TriggerCheck.Web/TriggerCheck.Web.csproj src/TriggerCheck.Web/
RUN dotnet restore src/TriggerCheck.Web/TriggerCheck.Web.csproj

COPY src/ src/
RUN dotnet publish src/TriggerCheck.Web/TriggerCheck.Web.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# /data holds the SQLite database (+ -wal/-shm) and the ASP.NET Core data-protection keys (HOME=/data, so the keys
# land in /data/.aspnet/DataProtection-Keys and survive restarts: otherwise every restart logs everybody out).
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME /data

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    HOME=/data \
    ConnectionStrings__Default="Data Source=/data/triggercheck.db" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /app .

# Non-root: the "app" user (UID $APP_UID = 1654) that ships with the official .NET images.
USER $APP_UID
EXPOSE 8080
# `docker run ... <image> migrate` applies the EF migrations and exits (docs/DEPLOYMENT.md "Database and migrations").
ENTRYPOINT ["dotnet", "TriggerCheck.Web.dll"]
