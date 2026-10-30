#!/usr/bin/env bash
# PROTOTYPE — throwaway. Answers: "What should the OIDC Login config page look like?"
# Builds a host plugin serving three config page variants (A long form, B tabs, C guided
# setup), starts Jellyfin 12.4 with it, and completes setup. Switch variants with the pink
# bar (or ←/→, or &variant=A|B|C). Nothing is saved; click "state" to see in-memory state.
#
#   ./run.sh        build + start on http://127.0.0.1:8097
#   ./run.sh stop   remove the container
set -euo pipefail
cd "$(dirname "$0")"
NAME=jf-config-proto
URL=http://127.0.0.1:8097

if [[ "${1:-}" == stop ]]; then podman rm -f "$NAME" >/dev/null && echo stopped; exit 0; fi

podman run --rm -v "$PWD/src:/src:Z" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish -c Release -o /src/out --nologo -v q >/dev/null
mkdir -p plugins/OidcLoginProto && cp src/out/OidcLoginProto.dll plugins/OidcLoginProto/

podman rm -f "$NAME" >/dev/null 2>&1 || true
podman run -d --name "$NAME" -p 8097:8096 \
  -v "$PWD/plugins/OidcLoginProto:/config/plugins/OidcLoginProto:Z" docker.io/jellyfin/jellyfin:12.4 >/dev/null
echo -n "waiting for Jellyfin"
until curl -sf "$URL/Startup/User" >/dev/null; do echo -n .; sleep 2; done; echo

J='Content-Type: application/json'
curl -sf -X POST "$URL/Startup/Configuration" -H "$J" -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
curl -sf -X POST "$URL/Startup/User" -H "$J" -d '{"Name":"proto","Password":"proto-password"}'
curl -sf -X POST "$URL/Startup/RemoteAccess" -H "$J" -d '{"EnableRemoteAccess":true}'
curl -sf -X POST "$URL/Startup/Complete"
echo "sign in as proto / proto-password, then open:"
echo "  $URL/web/#/configurationpage?name=OidcLogin&variant=A"
