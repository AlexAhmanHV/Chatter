# Build from the repo root: docker build -f Chatter.Server/Dockerfile -t chatter-server .
# (context has to be the repo root, not Chatter.Server/, so this can see the Chatter.Shared
# project it references)

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first, with just the project files, so this layer is cached across code-only changes.
COPY Chatter.Server/Chatter.Server.csproj Chatter.Server/
COPY Chatter.Shared/Chatter.Shared.csproj Chatter.Shared/
RUN dotnet restore Chatter.Server/Chatter.Server.csproj -r linux-x64

COPY Chatter.Server/ Chatter.Server/
COPY Chatter.Shared/ Chatter.Shared/
RUN dotnet publish Chatter.Server/Chatter.Server.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    --no-restore \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# SQLite file lives in its own volume so `docker compose down`/container recreation doesn't
# wipe chat history - see ConnectionStrings__Chatter below and docker-compose.yml.
RUN mkdir -p /app/data
VOLUME ["/app/data"]
ENV ConnectionStrings__Chatter="Data Source=/app/data/chatter.db"

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Chatter.Server.dll"]
