# Faceless Studio cho Linux: .NET 11 RC2 + FFmpeg + Piper (giọng Việt, Anh) + font tiếng Việt.
# Build từ thư mục gốc repo:  docker build -f docker/Dockerfile -t faceless-studio .

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2 AS build
WORKDIR /src
COPY global.json Directory.Build.props FacelessStudio.slnx ./
COPY src/ src/
RUN dotnet publish src/FacelessStudio.Web -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.2
ARG TARGETARCH
ARG PIPER_VERSION=2023.11.14-2
ARG VOICES_URL=https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg fonts-dejavu-core fonts-noto-core curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# Piper TTS (self-hosted) và hai giọng mặc định.
RUN set -eux; \
    case "${TARGETARCH:-amd64}" in arm64) PIPER_ARCH=aarch64 ;; *) PIPER_ARCH=x86_64 ;; esac; \
    curl -fsSL "https://github.com/rhasspy/piper/releases/download/${PIPER_VERSION}/piper_linux_${PIPER_ARCH}.tar.gz" | tar xz -C /opt; \
    mkdir -p /opt/piper-voices && cd /opt/piper-voices; \
    for v in vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium en/en_US/lessac/medium/en_US-lessac-medium; do \
      curl -fsSLO "${VOICES_URL}/${v}.onnx"; curl -fsSLO "${VOICES_URL}/${v}.onnx.json"; \
    done

ENV FACELESS_HOME=/data \
    ASPNETCORE_URLS=http://+:8080 \
    PIPER_PATH=/opt/piper/piper \
    PIPER_VOICE_VI=/opt/piper-voices/vi_VN-vais1000-medium.onnx \
    PIPER_VOICE_EN=/opt/piper-voices/en_US-lessac-medium.onnx \
    SUBTITLE_FONT="DejaVu Sans"

WORKDIR /app
COPY --from=build /app .
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "FacelessStudio.Web.dll"]
