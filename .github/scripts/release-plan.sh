#!/usr/bin/env bash
# Decides whether and what the release workflow releases. Prints GITHUB_OUTPUT lines.
#
#   release-plan.sh dispatch <major|minor|patch>
#   release-plan.sh push <before-sha> <after-sha>
#   release-plan.sh changelog <notes-file>   (writes the notes into ./build.yaml)
#
# The version comes only from git tags vMAJOR.MINOR.PATCH; jprm gets it as MAJOR.MINOR.PATCH.0.
# Empty notes mean "let GitHub generate them".
set -euo pipefail

latest_version() {
  git tag --list 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sed 's/^v//' | sort -V | tail -n 1
}

next_version() {
  local major minor patch
  IFS=. read -r major minor patch <<< "$(latest_version || true)"
  major=${major:-0} minor=${minor:-0} patch=${patch:-0}
  case "$1" in
    major) echo "$((major + 1)).0.0" ;;
    minor) echo "$major.$((minor + 1)).0" ;;
    patch) echo "$major.$minor.$((patch + 1))" ;;
    *) echo "unknown bump '$1' (expected major, minor or patch)" >&2; return 1 ;;
  esac
}

target_abi_at() {
  git show "$1:build.yaml" | sed -n 's/^targetAbi:[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p'
}

print_release() {
  local version
  version="$(next_version "$1")"
  echo "release=true"
  echo "tag=v$version"
  echo "version=$version.0"
  echo "notes=$2"
}

case "${1:-}" in
  dispatch)
    print_release "${2:?bump required}" ""
    ;;
  push)
    before_abi="$(target_abi_at "${2:?before sha required}")"
    after_abi="$(target_abi_at "${3:?after sha required}")"
    if [[ "$before_abi" == "$after_abi" ]]; then
      echo "release=false"
    else
      # targetAbi 12.N.0.0 is the Jellyfin 12.N the Plugin is compiled and tested against.
      IFS=. read -r abi_major abi_minor _ <<< "$after_abi"
      print_release patch "Built and tested against Jellyfin $abi_major.$abi_minor"
    fi
    ;;
  changelog)
    # jprm puts build.yaml's changelog into the zip's meta.json and from there into manifest.json.
    # A JSON string is a valid YAML double-quoted scalar.
    CHANGELOG="$(jq -Rs . < "${2:?notes file required}")" \
      awk '/^changelog:/ { print "changelog: " ENVIRON["CHANGELOG"]; next } { print }' build.yaml > build.yaml.tmp
    mv build.yaml.tmp build.yaml
    ;;
  *)
    echo "usage: $0 dispatch <major|minor|patch> | push <before-sha> <after-sha> | changelog <notes-file>" >&2
    exit 2
    ;;
esac
