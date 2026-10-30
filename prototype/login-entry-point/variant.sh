#!/usr/bin/env bash
# PROTOTYPE — switch the Login Entry Point variant on the running prototype Jellyfin.
# Writes variants/<V>.html into Branding → Login disclaimer and variants/<V>.css into
# Branding → Custom CSS, exactly as the Plugin would through the Branding config API.
set -euo pipefail
cd "$(dirname "$0")"

V=${1:?usage: ./variant.sh A|B|C}
URL=http://127.0.0.1:8096
AUTH='MediaBrowser Client="proto", Device="proto", DeviceId="proto", Version="1"'

TOKEN=$(curl -sf -X POST "$URL/Users/AuthenticateByName" -H 'Content-Type: application/json' \
  -H "Authorization: $AUTH" -d '{"Username":"proto","Pw":"proto-password"}' | jq -r .AccessToken)

css=""; [[ -f "variants/$V.css" ]] && css=$(cat "variants/$V.css")
jq -n --rawfile html "variants/$V.html" --arg css "$css" \
  '{LoginDisclaimer:$html, CustomCss:$css, SplashscreenEnabled:false}' |
  curl -sf -X POST "$URL/System/Configuration/branding" -H 'Content-Type: application/json' \
    -H "Authorization: $AUTH, Token=\"$TOKEN\"" -d @-

echo "variant $V applied — reload $URL/web/#/login (log out first if signed in)"
echo "--- state: Login disclaimer ---"; cat "variants/$V.html"
echo "--- state: Custom CSS ---"; echo "${css:-<none>}"
