# syntax=docker/dockerfile:1.7
# Multi-stage build for HomeAssistantMCPSharp — Docker is the primary deployment target.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so we can cache the layer.
COPY HomeAssistantMCPSharp.csproj ./
RUN dotnet restore HomeAssistantMCPSharp.csproj

# Now the rest of the source.
COPY . .
RUN dotnet publish HomeAssistantMCPSharp.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Non-root user for safety.
RUN groupadd -r hamcp && useradd -r -g hamcp -d /app -s /sbin/nologin hamcp \
    && mkdir -p /app/logs \
    && chown -R hamcp:hamcp /app

COPY --from=build --chown=hamcp:hamcp /app/publish ./

ENV ASPNETCORE_URLS=http://+:5100 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true \
    Server__Host=0.0.0.0 \
    Server__Port=5100 \
    Serilog__MinimumLevel__Override__Microsoft.Hosting.Lifetime=Information

EXPOSE 5100
USER hamcp

ENTRYPOINT ["dotnet", "HomeAssistantMCPSharp.dll"]
