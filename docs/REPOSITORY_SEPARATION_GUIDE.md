# Repository Separation Guide

This guide explains how to extract the `agent13-frontend` directory into its own standalone repository while preserving Git history, and how to update CI/CD pipelines for independent deployment.

---

## Prerequisites

- Git 2.25+ (for `git filter-repo` or `git subtree`)
- GitHub CLI (`gh`) installed and authenticated
- `git filter-repo` installed (`pip install git-filter-repo`)
- Owner or Admin access to the `loqutus490` GitHub organization

---

## Step 1 – Create the New Repository

```bash
gh repo create loqutus490/agent13-frontend \
  --private \
  --description "Legal RAG Platform – React/Vite frontend" \
  --clone=false
```

---

## Step 2 – Extract Frontend Code with Full Git History

Using `git filter-repo` to extract only the `agent13-frontend` subdirectory and rewrite paths so it becomes the root of the new repository.

```bash
# Clone a fresh copy of the monorepo (do NOT use your working copy)
git clone https://github.com/loqutus490/azure-enterprise-bicep.git agent13-frontend-migration
cd agent13-frontend-migration

# Extract the agent13-frontend subdirectory and rewrite paths
git filter-repo --subdirectory-filter agent13-frontend

# The repository root now contains the former agent13-frontend/ contents:
# src/, package.json, vite.config.js, etc.
```

---

## Step 3 – Push to the New Repository

> **Note:** The target repository must be completely empty (no initial commit, no branch protection rules) before running these commands.

```bash
git remote add origin https://github.com/loqutus490/agent13-frontend.git
git push -u origin main
git push --tags
```

---

## Step 4 – Set Up the Frontend CI/CD Pipeline

Create `.github/workflows/ci.yml` in the new `agent13-frontend` repository:

```yaml
name: Frontend CI

on:
  push:
  pull_request:

permissions:
  contents: read

concurrency:
  group: ci-${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

jobs:
  build:
    runs-on: ubuntu-latest
    timeout-minutes: 15

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: npm

      - name: Install dependencies
        run: npm ci

      - name: Build
        run: npm run build

      - name: Upload Frontend Artifact
        uses: actions/upload-artifact@v4
        with:
          name: frontend-dist
          path: dist
          if-no-files-found: error
          retention-days: 7
```

---

## Step 5 – Remove the Frontend from This Repository

Once the `agent13-frontend` repository is live and its CI is green, remove the directory from this repository:

```bash
# In a branch of azure-enterprise-bicep
git rm -r agent13-frontend
git commit -m "chore: remove agent13-frontend (migrated to loqutus490/agent13-frontend)"
```

---

## Step 6 – Update the CI Workflow in This Repository

After removing `agent13-frontend`, simplify `.github/workflows/ci.yml` to remove all frontend steps:

- Remove the `Setup Node.js` step
- Remove the `Verify npm build script exists` step
- Remove the `Install Frontend dependencies` step
- Remove the `Build Frontend` step
- Remove the `Upload Frontend Artifact` step

The workflow should only build, test, and publish the .NET backend.

---

## Step 7 – Update Environment Configuration References

The `README.md` and scripts reference `agent13-frontend/scripts/generate-vite-env.sh`. After the migration:

1. Update `README.md` to point to `https://github.com/loqutus490/agent13-frontend` for frontend setup instructions.
2. Update `.env.shared.example` comments if they reference frontend paths.
3. Remove any remaining mentions of `agent13-frontend/` in scripts that no longer apply.

---

## Step 8 – Define the API Contract

Establish a clear contract between the frontend and backend so both teams can evolve independently.

### Backend API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/ask` | Submit a RAG question |
| `POST` | `/debug/retrieval` | Diagnostic retrieval (protected) |

### `/ask` Request Schema

```json
{
  "question": "string (required)",
  "matterId": "string (required)",
  "practiceArea": "string (optional)",
  "client": "string (optional)",
  "confidentialityLevel": "string (optional)"
}
```

### `/ask` Response Schema

```json
{
  "answer": "string",
  "sources": ["string"],
  "sourceMetadata": [{}]
}
```

Document this contract in an `openapi.yaml` or `docs/API_CONTRACT.md` file in this repository and reference it from the `agent13-frontend` repository.

---

## Step 9 – Update CODEOWNERS (Optional)

After separation, update `.github/CODEOWNERS` in each repository to assign the correct team:

**`azure-enterprise-bicep` (Backend + Infrastructure):**
```
* @loqutus490/backend-infra-team
```

**`agent13-frontend` (Frontend):**
```
* @loqutus490/frontend-team
```

---

## Rollback Plan

If you need to revert the migration:

1. Re-add the `agent13-frontend` directory to the `azure-enterprise-bicep` repo from your backup branch.
2. Restore the original `ci.yml` steps.
3. Archive (but do not delete) the `agent13-frontend` repository to preserve history.

---

## Checklist

- [ ] New `agent13-frontend` repository created on GitHub
- [ ] Frontend code extracted with full Git history using `git filter-repo`
- [ ] History pushed to the new repository
- [ ] Frontend CI workflow created and passing in the new repository
- [ ] `agent13-frontend/` directory removed from `azure-enterprise-bicep`
- [ ] `ci.yml` updated to remove frontend steps
- [ ] `README.md` updated to link to the new frontend repository
- [ ] Environment configuration references updated
- [ ] API contract documented
- [ ] Team notified of the change
