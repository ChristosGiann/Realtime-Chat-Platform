FROM node:24-bookworm-slim@sha256:873f586f3794f52281bd039bf02c048a0be08fde619338f9941bad1eb6b34530 AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS backend
WORKDIR /src
COPY global.json dotnet-tools.json RealtimeChat.sln ./
COPY src/Chat.Api/Chat.Api.csproj src/Chat.Api/
RUN dotnet restore src/Chat.Api/Chat.Api.csproj
COPY src/Chat.Api/ src/Chat.Api/
RUN dotnet publish src/Chat.Api/Chat.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM backend AS migrations
RUN dotnet tool restore
ENTRYPOINT ["dotnet", "ef", "database", "update", "--project", "src/Chat.Api", "--configuration", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Deployment__RequirePersistentKeys=true \
    DOTNET_EnableDiagnostics=0
COPY --from=backend /out ./
COPY --from=frontend /src/frontend/dist ./wwwroot/
COPY docker-entrypoint.sh ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "/app/docker-entrypoint.sh"]
