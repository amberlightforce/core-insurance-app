#!/usr/bin/env bash
# Docker path of the spike (Gotenberg 8.37.0 + veraPDF container). Not executed on the spike host
# (Docker daemon unavailable) - see FINDINGS.md "What was not tested".
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p out
docker compose up -d gotenberg
docker compose build spike
docker compose run --rm spike --mode=all
docker compose run --rm spike --mode=gotenberg-native
# memory: sample the gotenberg container while the large render runs
( while true; do docker stats --no-stream --format '{{.Name}} {{.MemUsage}}' | grep gotenberg >> out/docker-stats-large.txt; sleep 1; done ) &
STATS=$!
docker compose run --rm spike --mode=large --rows=16000 || true
kill $STATS
for f in out/det-run*.pdfa.pdf out/gtb-*.pdf; do
  for flavour in 3a 3b 3u ua1; do
    docker compose run --rm verapdf --flavour "$flavour" --format text "/data/$(basename "$f")" || true
  done
done | tee out/verapdf-docker.txt
