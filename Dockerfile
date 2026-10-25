# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2@sha256:e2438fd800a09d2d761aefe4edd30675c8f1db8958e4423c0844cc9a0240e60f AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Studio/ src/Studio/
RUN --mount=type=secret,id=proxy_ca \
    if [ -f /run/secrets/proxy_ca ]; then export SSL_CERT_FILE=/run/secrets/proxy_ca; fi; \
    dotnet restore src/Studio/Studio.csproj --locked-mode && \
    dotnet publish src/Studio/Studio.csproj -c Release --no-restore -o /publish /p:UseAppHost=false && \
    rm -rf /tmp/.dotnet /root/.dotnet/shm

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.2@sha256:ca60823900e34e488caed1df187266fa02e3b59d1180c8db728b6cb727212ba2 AS runtime
USER root
RUN --mount=type=secret,id=proxy_ca \
    if [ -f /run/secrets/proxy_ca ]; then export SSL_CERT_FILE=/run/secrets/proxy_ca; \
      printf 'Acquire::https::CaInfo "/run/secrets/proxy_ca";\n' > /etc/apt/apt.conf.d/99studio-build-ca; fi; \
    apt-get update && apt-get install -y --no-install-recommends ffmpeg fonts-dejavu-core curl ca-certificates && \
    rm -f /etc/apt/apt.conf.d/99studio-build-ca && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=app:app /publish .
RUN mkdir -p /data /home/app/.dotnet /home/app/.cache /home/app/.config && chown -R app:app /data /home/app
ENV ASPNETCORE_URLS=http://+:8080 Studio__DataPath=/data DOTNET_CLI_TELEMETRY_OPTOUT=1
USER app
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s CMD curl --fail --silent http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "Studio.dll"]
