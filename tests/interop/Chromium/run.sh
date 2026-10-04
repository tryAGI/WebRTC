#!/usr/bin/env bash
set -euo pipefail
# Local test endpoints only, isolated from the host and all internet/provider routes.
cd "$(dirname "$0")/../../.."
run_id="tryagi-browser-$$"
network="$run_id-local"
browser="$run_id-browser"
peer="$run_id-peer"
image="$run_id-peer:local"
cleanup() {
  docker rm -f "$peer" "$browser" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  docker image rm "$image" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker build -f tests/interop/Chromium/Dockerfile -t "$image" .
docker network create --internal "$network" >/dev/null
docker run -d --name "$browser" --network "$network" \
  --mount "type=bind,src=$PWD/tests/interop/Chromium/probe.mjs,dst=/probe.mjs,readonly" \
  --entrypoint sh \
  mcr.microsoft.com/playwright:v1.62.1-noble@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e \
  -c 'sleep 600' >/dev/null
address="$(docker inspect "$browser" --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}')"
for mode in answer-active answer-passive offer; do
  docker run -d --name "$peer" --network "container:$browser" "$image" "$address" "$mode" >/dev/null
  if ! docker exec "$browser" node /probe.mjs "$mode"; then
    docker logs "$peer"
    exit 1
  fi
  peer_exit="$(docker wait "$peer")"
  docker logs "$peer"
  test "$peer_exit" = 0
  docker rm "$peer" >/dev/null
done
