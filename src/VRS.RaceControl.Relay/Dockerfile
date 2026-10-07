# ─── Stage 1: Build ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
ARG RELAY_VERSION=1.2.2

# Build from the repository root:
# docker build -f src/VRS.RaceControl.Relay/Dockerfile .
COPY ["Directory.Build.props", "./"]
COPY ["src/VRS.RaceControl.Relay/VRS.RaceControl.Relay.csproj", "src/VRS.RaceControl.Relay/"]
COPY ["src/VRS.RaceControl.Shared/VRS.RaceControl.Shared.csproj", "src/VRS.RaceControl.Shared/"]
RUN dotnet restore "src/VRS.RaceControl.Relay/VRS.RaceControl.Relay.csproj"

COPY . .
RUN dotnet publish "src/VRS.RaceControl.Relay/VRS.RaceControl.Relay.csproj" -c Release -o /app/publish --no-restore \
    -p:Version=$RELAY_VERSION -p:InformationalVersion=$RELAY_VERSION \
    -p:AssemblyVersion=1.2.2.0 -p:FileVersion=1.2.2.0

# ─── Stage 2: Runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV PORT=8080 \
    DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimitPercent=0x3C \
    DOTNET_GCConserveMemory=9 \
    DOTNET_EnableDiagnostics=0 \
    VRS_MAX_SESSIONS=16 \
    VRS_MAX_CLIENTS_PER_SESSION=64 \
    VRS_MAX_CONNECTIONS=192 \
    VRS_MAX_MESSAGE_BYTES=262144 \
    VRS_MEMORY_REJECT_MB=440 \
    VRS_MEMORY_HEALTH_MB=480
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "VRS.RaceControl.Relay.dll"]
