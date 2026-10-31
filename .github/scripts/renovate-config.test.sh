#!/usr/bin/env bash
# Tests for renovate.json against real Renovate (container image) and live registries.
# A copy of the repo is moved back to older versions, so every rule has an update to act on.
# Run locally: bash .github/scripts/renovate-config.test.sh  (needs podman or docker and network)
set -euo pipefail
trap 'echo "error on line $LINENO" >&2' ERR
export LC_ALL=C

root="$(cd "$(dirname "$0")/../.." && pwd)"
image="${RENOVATE_IMAGE:-ghcr.io/renovatebot/renovate:44.176.0}"
engine="$(command -v podman || command -v docker)"
failures=0

expect() {
  local name="$1" expected="$2" actual="$3"
  if [[ "$expected" == "$actual" ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name"
    diff <(echo "$expected") <(echo "$actual") | sed 's/^/     /' || true
    failures=$((failures + 1))
  fi
}

renovate() {
  "$engine" run --rm -v "$1:/repo:ro,Z" -w /repo -e LOG_LEVEL=trace -e LOG_FORMAT=json "$image" "${@:2}"
}

test_config_is_valid() {
  local out
  if out="$("$engine" run --rm -v "$root:/repo:ro,Z" -w /repo "$image" renovate-config-validator --strict 2>&1)"; then
    expect "${FUNCNAME[0]}" "valid" "valid"
  else
    expect "${FUNCNAME[0]}" "valid" "$out"
  fi
}

# A git copy of the repo, moved back to Jellyfin 12.0 and older versions of a few other dependencies.
# Only the version numbers change, so the managers see the real file formats.
make_fixture() {
  fixture="$(mktemp -d)"
  chmod 755 "$fixture" # Renovate runs as a non-root user in the container
  git -C "$root" ls-files -co --exclude-standard -z | (cd "$root" && xargs -0 cp --parents -t "$fixture")
  sed -i 's/^targetAbi: "[0-9.]*"$/targetAbi: "12.0.0.0"/' "$fixture/build.yaml"
  sed -i 's#jellyfin/jellyfin:[^@"]*@#jellyfin/jellyfin:12.0@#; s#pocket-id:v[^@"]*@#pocket-id:v2.0.0@#' \
    "$fixture/tests/Jellyfin.Plugin.OidcLogin.E2ETests/ContainerImages.cs"
  sed -i -e 's/"\(Jellyfin\.[A-Za-z]*\)" Version="\[[^]]*\]"/"\1" Version="[12.0.0]"/' \
    -e 's/"xunit.v3" Version="\[[^]]*\]"/"xunit.v3" Version="[4.0.0]"/' \
    -e 's/"NSubstitute" Version="\[[^]]*\]"/"NSubstitute" Version="[6.0.0]"/' \
    -e 's/"Testcontainers" Version="\[[^]]*\]"/"Testcontainers" Version="[3.10.0]"/' \
    "$fixture/Directory.Packages.props"
  git -C "$fixture" init -q -b main
  git -C "$fixture" -c user.email=t@example.invalid -c user.name=t -c commit.gpgsign=false commit -q --allow-empty -m init
  git -C "$fixture" add -A
  git -C "$fixture" -c user.email=t@example.invalid -c user.name=t -c commit.gpgsign=false commit -q -m fixture
}

# One line per upgrade: depName updateType newValue | its branch: name groupName automerge minimumGroupSize schedule
lookup_updates() {
  # The branch configs (after packageRules) are only logged at trace level.
  renovate "$fixture" renovate --platform=local --dry-run=lookup | tee "${RENOVATE_LOG:-/dev/null}" |
    jq -r 'select(.msg == "branches") | .branches[] | . as $b | .upgrades[] |
      [.depName, .updateType, .newValue, $b.branchName, ($b.groupName // "-"), $b.automerge,
       $b.minimumGroupSize, ($b.schedule | join(";"))] | @tsv' |
    sort -u
}

updates_of() {
  grep -P "^$1\t" <<< "$updates" | cut -f2- || true
}

test_jellyfin_minor_moves_image_packages_and_target_abi_together() {
  local jellyfin
  jellyfin="$(grep -P '\trenovate/jellyfin\t' <<< "$updates" | cut -f1 | sort)"
  expect "${FUNCNAME[0]} members" "Jellyfin.Controller
Jellyfin.Controller
Jellyfin.Model
jellyfin/jellyfin" "$jellyfin"
  expect "${FUNCNAME[0]} rules" "Jellyfin true 4 at any time" \
    "$(grep -P '\trenovate/jellyfin\t' <<< "$updates" | cut -f5-8 | sort -u | tr '\t' ' ')"
}

test_jellyfin_target_abi_bump_has_the_form_release_plan_reads() {
  # targetAbi follows the NuGet pin: [12.N.0] in the props, 12.N.0 in build.yaml (written as "12.N.0.0").
  # release-plan.sh cuts the patch release when a push to main changes targetAbi.
  local pin
  pin="$(updates_of Jellyfin.Model | cut -f2)"
  expect "${FUNCNAME[0]}" "$(tr -d '[]' <<< "$pin")
$pin" "$(updates_of Jellyfin.Controller | cut -f2 | sort -u)"
}

test_pocket_id_is_automerged_on_its_own() {
  # Renovate drops groupName from a branch holding a single upgrade; the branch name shows the group.
  expect "${FUNCNAME[0]}" "renovate/pocketid true" \
    "$(updates_of ghcr.io/pocket-id/pocket-id | cut -f3,5 | sort -u | tr '\t' ' ')"
}

test_other_minor_and_patch_updates_are_a_weekly_automerged_group() {
  expect "${FUNCNAME[0]}" "patch renovate/other-dependencies Other dependencies true 1 * 0-3 * * 1" \
    "$(updates_of xunit.v3 | cut -f1,3- | tr '\t' ' ')"
}

test_majors_are_never_automerged() {
  local majors
  majors="$(awk -F'\t' '$2 == "major"' <<< "$updates")"
  expect "${FUNCNAME[0]} found" "yes" "$([[ -n "$majors" ]] && echo yes || echo no)"
  expect "${FUNCNAME[0]} Testcontainers" "major false" "$(updates_of Testcontainers | cut -f1,5 | tr '\t' ' ' | sort -u)"
  expect "${FUNCNAME[0]} none automerged" "" "$(awk -F'\t' '$6 == "true"' <<< "$majors")"
}

test_config_is_valid
make_fixture
updates="$(lookup_updates)"
echo "$updates" | sed 's/^/     /'
test_jellyfin_minor_moves_image_packages_and_target_abi_together
test_jellyfin_target_abi_bump_has_the_form_release_plan_reads
test_pocket_id_is_automerged_on_its_own
test_other_minor_and_patch_updates_are_a_weekly_automerged_group
test_majors_are_never_automerged
rm -rf "$fixture"

if ((failures)); then
  echo "$failures test(s) failed"
  exit 1
fi
echo "all tests passed"
