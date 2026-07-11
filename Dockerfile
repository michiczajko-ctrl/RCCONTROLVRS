# ─── Stage 1: Build ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first (for layer caching)
COPY src/VRS.RaceControl.Shared/VRS.RaceControl.Shared.csproj  src/VRS.RaceControl.Shared/
COPY src/VRS.RaceControl.Relay/VRS.RaceControl.Relay.csproj    src/VRS.RaceControl.Relay/

RUN dotnet restore src/VRS.RaceControl.Relay/VRS.RaceControl.Relay.csproj

# Copy all source
COPY src/VRS.RaceControl.Shared/  src/VRS.RaceControl.Shared/
COPY src/VRS.RaceControl.Relay/   src/VRS.RaceControl.Relay/

RUN dotnet publish src/VRS.RaceControl.Relay/VRS.RaceControl.Relay.csproj \
    -c Release -o /app/publish --no-restore

# ─── Stage 2: Runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Render / Railway pass PORT as an env variable
ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "VRS.RaceControl.Relay.dll"]
