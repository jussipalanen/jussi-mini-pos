FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source
COPY src/JussiMiniPos.Api/JussiMiniPos.Api.csproj src/JussiMiniPos.Api/
COPY src/JussiMiniPos.Core/JussiMiniPos.Core.csproj src/JussiMiniPos.Core/
RUN dotnet restore src/JussiMiniPos.Api/JussiMiniPos.Api.csproj
COPY src/JussiMiniPos.Api/ src/JussiMiniPos.Api/
COPY src/JussiMiniPos.Core/ src/JussiMiniPos.Core/
RUN dotnet publish src/JussiMiniPos.Api/JussiMiniPos.Api.csproj \
    --configuration Release --no-restore --output /out --property:UseAppHost=false

# Local development only: curl checks readiness and OpenSSL creates the
# localhost certificate at startup, in a volume rather than in the image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl openssl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
COPY docker/api/entrypoint.sh /entrypoint.sh
EXPOSE 8443
ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]
