#!/usr/bin/env bash
# PROTOTYPE — throwaway. Answers: "What does starting an OIDC Login look like on the
# Jellyfin 12.4 login page?" Three Login Entry Point variants, applied to a real
# Jellyfin through the Branding config, switchable with ./variant.sh A|B|C.
#
#   ./run.sh          start Jellyfin 12.4 on http://127.0.0.1:8096, finish setup, apply variant A
#   ./variant.sh B    switch variant, then reload the login page
#   ./run.sh stop     remove the container
set -euo pipefail
cd "$(dirname "$0")"

NAME=jf-entry-proto
URL=http://127.0.0.1:8096
ADMIN=proto
PASS=proto-password

if [[ "${1:-}" == stop ]]; then podman rm -f "$NAME" >/dev/null && echo "stopped"; exit 0; fi

podman rm -f "$NAME" >/dev/null 2>&1 || true
podman run -d --name "$NAME" -p 8096:8096 docker.io/jellyfin/jellyfin:12.4 >/dev/null
echo -n "waiting for Jellyfin"
until curl -sf "$URL/Startup/User" >/dev/null; do echo -n .; sleep 2; done; echo

J='Content-Type: application/json'
curl -sf -X POST "$URL/Startup/Configuration" -H "$J" \
  -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
curl -sf "$URL/Startup/User" >/dev/null
curl -sf -X POST "$URL/Startup/User" -H "$J" -d "{\"Name\":\"$ADMIN\",\"Password\":\"$PASS\"}"
curl -sf -X POST "$URL/Startup/RemoteAccess" -H "$J" -d '{"EnableRemoteAccess":true}'
curl -sf -X POST "$URL/Startup/Complete"

./variant.sh A
echo "open $URL/web/#/login  (admin: $ADMIN / $PASS)"
