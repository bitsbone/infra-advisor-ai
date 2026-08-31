# ── Stage 1: build ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY aca-agentic-poc-dotnet.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Stage 2: runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

LABEL org.opencontainers.image.title="aca-agentic-poc-dotnet" \
      org.opencontainers.image.description=".NET proof-of-concept agent service for Azure Container Apps identity/telemetry experiments (managed vs. sidecar patterns)." \
      org.opencontainers.image.source="https://github.com/bitsbone/infra-advisor-ai"

# Port + ASPNETCORE_URLS match infra/bicep/modules/aca-agentic-poc.bicep's
# targetPort: 8080 / ASPNETCORE_URLS env var for both Container Apps.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

# aspnet base image ships a non-root 'app' user — no dd-trace-dotnet install
# needed in either deployment of this image (see TelemetrySetup.cs), so no
# extra root-level setup step is required before switching users.
USER app

ENTRYPOINT ["dotnet", "aca-agentic-poc-dotnet.dll"]
