#!/usr/bin/env bash
# Tests for release-plan.sh. Each test runs the script inside a throwaway git repo.
# Run: bash .github/scripts/release-plan.test.sh
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/release-plan.sh"
failures=0

new_repo() {
  repo="$(mktemp -d)"
  git -C "$repo" init -q -b main
  git -C "$repo" config user.email test@example.invalid
  git -C "$repo" config user.name test
  git -C "$repo" config commit.gpgsign false
  git -C "$repo" config tag.gpgsign false
}

commit_target_abi() {
  printf 'name: "OIDC Login"\ntargetAbi: "%s"\nframework: "net10.0"\n' "$1" > "$repo/build.yaml"
  git -C "$repo" add build.yaml
  git -C "$repo" commit -q -m "targetAbi $1"
  git -C "$repo" rev-parse HEAD
}

commit_other_change() {
  echo "$RANDOM" >> "$repo/README.md"
  git -C "$repo" add README.md
  git -C "$repo" commit -q -m "other change"
  git -C "$repo" rev-parse HEAD
}

plan() {
  (cd "$repo" && bash "$script" "$@")
}

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

test_dispatch_patch_without_tags_starts_at_0_0_1() {
  new_repo
  commit_target_abi 12.4.0.0 > /dev/null
  expect "${FUNCNAME[0]}" "release=true
tag=v0.0.1
version=0.0.1.0
notes=" "$(plan dispatch patch)"
}

tag_existing_releases() {
  # v1.9.4 sorts above v1.10.0 as text; junk tags aren't release versions.
  for tag in v1.9.4 v1.10.0 v2.0.0-rc.1 v9 foo1.2.3 v3.0.0.0; do
    git -C "$repo" tag "$tag"
  done
}

test_dispatch_bumps_the_highest_release_tag() {
  new_repo
  commit_target_abi 12.4.0.0 > /dev/null
  tag_existing_releases
  expect "${FUNCNAME[0]} patch" "tag=v1.10.1" "$(plan dispatch patch | grep '^tag=')"
  expect "${FUNCNAME[0]} minor" "tag=v1.11.0" "$(plan dispatch minor | grep '^tag=')"
  expect "${FUNCNAME[0]} major" "release=true
tag=v2.0.0
version=2.0.0.0
notes=" "$(plan dispatch major)"
}

test_dispatch_rejects_an_unknown_bump() {
  new_repo
  commit_target_abi 12.4.0.0 > /dev/null
  if plan dispatch huge > /dev/null 2>&1; then
    expect "${FUNCNAME[0]}" "non-zero exit" "exit 0"
  else
    expect "${FUNCNAME[0]}" "" ""
  fi
}

test_push_that_changes_target_abi_releases_the_next_patch() {
  new_repo
  before="$(commit_target_abi 12.4.0.0)"
  git -C "$repo" tag v0.3.1
  commit_other_change > /dev/null
  after="$(commit_target_abi 12.5.0.0)"
  expect "${FUNCNAME[0]}" "release=true
tag=v0.3.2
version=0.3.2.0
notes=Built and tested against Jellyfin 12.5" "$(plan push "$before" "$after")"
}

test_push_that_keeps_target_abi_releases_nothing() {
  new_repo
  before="$(commit_target_abi 12.4.0.0)"
  git -C "$repo" tag v0.3.1
  after="$(commit_other_change)"
  expect "${FUNCNAME[0]}" "release=false" "$(plan push "$before" "$after")"
}

test_changelog_writes_the_release_notes_into_build_yaml() {
  new_repo
  printf 'name: "OIDC Login"\nchangelog: ""\nartifacts:\n- "a.dll"\n' > "$repo/build.yaml"
  printf '## What'"'"'s Changed\n* Fix "quotes": a\\b by @x in #1\n' > "$repo/notes.md"
  plan changelog notes.md || true
  expect "${FUNCNAME[0]}" 'name: "OIDC Login"
changelog: "## What'"'"'s Changed\n* Fix \"quotes\": a\\b by @x in #1\n"
artifacts:
- "a.dll"' "$(cat "$repo/build.yaml")"
}

test_changelog_writes_the_release_notes_into_build_yaml
test_push_that_changes_target_abi_releases_the_next_patch
test_push_that_keeps_target_abi_releases_nothing
test_dispatch_patch_without_tags_starts_at_0_0_1
test_dispatch_bumps_the_highest_release_tag
test_dispatch_rejects_an_unknown_bump

if ((failures)); then
  echo "$failures test(s) failed"
  exit 1
fi
echo "all tests passed"
