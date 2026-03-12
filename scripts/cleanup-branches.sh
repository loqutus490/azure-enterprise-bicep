#!/usr/bin/env bash
set -euo pipefail

REMOTE="origin"
BASE_BRANCH="main"
PROTECTED_BRANCHES=("main" "master" "develop" "dev" "work")
APPLY=false
MERGE=false

usage() {
  cat <<USAGE
Usage: $(basename "$0") [--remote origin] [--base main] [--apply] [--merge]

Safely clean up remote branches already merged to <remote>/<base>.

Options:
  --remote <name>   Remote name (default: origin)
  --base <name>     Base branch on remote (default: main)
  --apply           Actually delete remote branches (default: dry run)
  --merge           Merge candidate branches into the current branch before deletion
                    (not recommended unless you know candidates are required)
  -h, --help        Show this help
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --remote)
      REMOTE="$2"
      shift 2
      ;;
    --base)
      BASE_BRANCH="$2"
      shift 2
      ;;
    --apply)
      APPLY=true
      shift
      ;;
    --merge)
      MERGE=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "Error: run this inside a git repository." >&2
  exit 1
fi

if ! git remote get-url "$REMOTE" >/dev/null 2>&1; then
  echo "Error: remote '$REMOTE' is not configured." >&2
  echo "Add one with: git remote add $REMOTE <repo-url>" >&2
  exit 1
fi

echo "Fetching and pruning refs from '$REMOTE'..."
git fetch "$REMOTE" --prune

if ! git show-ref --verify --quiet "refs/remotes/$REMOTE/$BASE_BRANCH"; then
  echo "Error: '$REMOTE/$BASE_BRANCH' does not exist." >&2
  exit 1
fi

mapfile -t CANDIDATES < <(
  git for-each-ref --format='%(refname:short)' "refs/remotes/$REMOTE" \
  | rg -v "^$REMOTE/(HEAD|$BASE_BRANCH)$" \
  | while read -r ref; do
      branch="${ref#${REMOTE}/}"
      if git merge-base --is-ancestor "$ref" "$REMOTE/$BASE_BRANCH"; then
        printf '%s\n' "$branch"
      fi
    done
)

if [[ ${#CANDIDATES[@]} -eq 0 ]]; then
  echo "No merged candidate branches found under $REMOTE."
  exit 0
fi

echo "Merged candidate branches (safe to delete if no active PR/release use):"
for b in "${CANDIDATES[@]}"; do
  printf '  - %s\n' "$b"
done

is_protected() {
  local name="$1"
  for p in "${PROTECTED_BRANCHES[@]}"; do
    [[ "$name" == "$p" ]] && return 0
  done
  return 1
}

for branch in "${CANDIDATES[@]}"; do
  if is_protected "$branch"; then
    echo "Skipping protected branch: $branch"
    continue
  fi

  if [[ "$MERGE" == true ]]; then
    echo "Merging $REMOTE/$branch into current branch..."
    git merge --no-ff "$REMOTE/$branch"
  fi

  if [[ "$APPLY" == true ]]; then
    echo "Deleting remote branch: $branch"
    git push "$REMOTE" --delete "$branch"
  else
    echo "[dry-run] Would delete remote branch: $branch"
  fi
done

if [[ "$APPLY" == false ]]; then
  echo
  echo "Dry run complete. Re-run with --apply to delete listed non-protected branches."
fi
