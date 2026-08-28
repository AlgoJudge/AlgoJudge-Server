#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER app
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["AlgoJudge.Server/AlgoJudge.Server.csproj", "AlgoJudge.Server/"]
RUN dotnet restore "./AlgoJudge.Server/./AlgoJudge.Server.csproj"
COPY . .
WORKDIR "/src/AlgoJudge.Server"
RUN dotnet build "./AlgoJudge.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./AlgoJudge.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# The operator's tool. On PATH, so `docker compose exec server aj-admin status`
# is the whole of it.
#
# `--chmod` because the final stage runs as `app` and could not chmod a
# root-owned file afterwards. It is bash, which this image already has — no
# package is installed for it, and nothing an attacker could not already do with
# the same shell the HEALTHCHECK below uses.
COPY --chmod=755 AlgoJudge.Server/aj-admin /usr/local/bin/aj-admin

# Whether the API is actually answering, not merely whether the process exists.
#
# Without this `docker compose up --wait` returns the moment the container
# starts — the .NET host is still booting, migrations have not run, and whatever
# runs next gets a connection refused. The Server's own CI works around it by
# polling; a compose file that waits on this service could not.
#
# Written with bash's /dev/tcp because this image has no curl and no wget, and
# adding one would mean a package install — a network dependency at build time
# for four lines of shell. `grep -q` closes the descriptor as soon as it matches.
HEALTHCHECK --interval=10s --timeout=3s --start-period=40s --retries=5 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 \
        && printf "GET /api/v1/health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3 \
        && grep -q "200 OK" <&3'

ENTRYPOINT ["dotnet", "AlgoJudge.Server.dll"]