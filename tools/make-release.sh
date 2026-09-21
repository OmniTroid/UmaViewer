#!/usr/bin/env bash
# Tag the current commit as a release. Usage: tools/make-release.sh <version>
set -euo pipefail

VERSION="${1:-}"
[ -n "$VERSION" ] || { echo "Usage: tools/make-release.sh <version>  (e.g. v1.2.0)"; exit 2; }
[[ "$VERSION" =~ ^v?[0-9]+(\.[0-9]+){0,2}([-+][0-9A-Za-z.-]+)?$ ]] || {
  echo "Not a version: '$VERSION' (expected like v1.2.0)"; exit 2; }

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"

# The tag points at HEAD, so uncommitted work wouldn't be part of the release.
if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "Working tree has uncommitted changes. Commit or stash them first."; exit 1
fi
if git rev-parse -q --verify "refs/tags/$VERSION" >/dev/null; then
  echo "Tag $VERSION already exists."; exit 1
fi

git tag -a "$VERSION" -m "Release $VERSION"
echo "Tagged $(git rev-parse --short HEAD) as $VERSION."
echo "Publish it with:  git push <remote> $VERSION"
