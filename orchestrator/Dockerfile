# advideo API + analysis worker (in-process) + Python pipeline.
# Build from the repo root:  docker build -f orchestrator/Dockerfile -t advideo-api .
# ML backends (YOLO/CLIP/OCR/Whisper, large):  --build-arg PIPELINE_EXTRAS=ml   (default: mock analyzers only)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY schema/ schema/
COPY orchestrator/ orchestrator/
RUN dotnet publish orchestrator/src/Advideo.Orchestrator.Api/Advideo.Orchestrator.Api.csproj \
      -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PIPELINE_EXTRAS=""
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-pip python3-venv ffmpeg \
 && rm -rf /var/lib/apt/lists/*
# pipeline in its own venv, CLI on PATH as advideo-analyze
COPY pipeline/ /opt/advideo/pipeline/
COPY schema/ /opt/advideo/schema/
RUN python3 -m venv /opt/advideo/venv \
 && /opt/advideo/venv/bin/pip install --no-cache-dir "/opt/advideo/pipeline${PIPELINE_EXTRAS:+[$PIPELINE_EXTRAS]}"
ENV PATH="/opt/advideo/venv/bin:${PATH}" \
    ADVIDEO_TAXONOMY=/opt/advideo/schema/taxonomy.json \
    AdServing__TaxonomyPath=/opt/advideo/schema/taxonomy.json \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1

WORKDIR /app
COPY --from=build /out .
COPY orchestrator/seed/ /app/seed/
RUN mkdir -p /app/data/maps && chown -R $APP_UID /app/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Advideo.Orchestrator.Api.dll"]
