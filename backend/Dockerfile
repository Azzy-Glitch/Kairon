FROM node:22-alpine AS frontend
WORKDIR /source/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
COPY assets/ /source/assets/
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY backend/Kairon.Backend.csproj backend/
RUN dotnet restore backend/Kairon.Backend.csproj
COPY backend/ backend/
RUN dotnet publish backend/Kairon.Backend.csproj -c Release -o /out --no-restore -p:SkipFrontendBuild=true
COPY --from=frontend /source/frontend/dist/ /out/wwwroot/
COPY LICENSE NOTICE /out/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/kairon \
    && chown -R app:app /var/lib/kairon
WORKDIR /app
COPY --from=build --chown=app:app /out/ ./
USER app
ENV ASPNETCORE_URLS=http://+:8000 \
    ASPNETCORE_ENVIRONMENT=Production \
    XDG_DATA_HOME=/var/lib/kairon
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:8000/api/health/ready || exit 1
ENTRYPOINT ["dotnet", "Kairon.Backend.dll"]
