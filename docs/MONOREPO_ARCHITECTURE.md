# Monorepo Architecture

## Current Structure

This repository (`loqutus490/azure-enterprise-bicep`) is a **monorepo** that contains three distinct technology stacks:

```
azure-enterprise-bicep/
├── infra/               # Azure Bicep templates (infrastructure-as-code)
├── src/                 # .NET 8 C# backend API (Legal RAG)
├── tests/               # Backend integration tests
├── scripts/             # Deployment and setup scripts
├── docs/                # Documentation
└── agent13-frontend/    # Node.js/Vite SPA (React frontend)
    ├── src/
    ├── package.json
    └── vite.config.js
```

### Why It Exists

The `agent13-frontend` directory was added alongside the backend and infrastructure code to allow a single repository to contain all components of the Legal RAG Platform. This was convenient early in development when:

- One team owned all components
- Deployments were coordinated manually
- The frontend was not yet production-ready

### Current CI/CD Coupling

The `.github/workflows/ci.yml` workflow builds **both** the backend and the frontend in a single job:

1. Restores and builds the .NET backend
2. Runs .NET tests
3. Publishes the backend artifact
4. Installs frontend Node.js dependencies (`npm ci`)
5. Builds the frontend (`npm run build`)
6. Uploads the frontend artifact

This means:
- A frontend build failure blocks backend CI from completing
- A backend test failure blocks the frontend artifact from being produced
- All contributors must have both .NET SDK and Node.js installed locally

---

## Problems with This Structure

| Problem | Impact |
|---------|--------|
| **Tight deployment coupling** | A frontend bug blocks infrastructure and backend deployments |
| **Mixed technology stacks** | .NET, Node.js, and Bicep have different toolchains, cycles, and expertise |
| **Repository bloat** | Hard to navigate; all contributors see all code regardless of their role |
| **Unclear ownership** | Infrastructure engineers, backend developers, and frontend developers share one repo with no boundary |
| **Dependency management** | `npm` and `dotnet` package updates are interleaved in the same PR history |

---

## Recommended Future State

Separate the frontend into its own repository:

```
loqutus490/azure-enterprise-bicep  (Backend + Infrastructure)
├── infra/              # Bicep templates
├── src/                # .NET 8 backend API
├── tests/              # Backend tests
├── scripts/            # Deployment and setup scripts
└── docs/               # Documentation

loqutus490/agent13-frontend  (New – Frontend only)
├── src/                # React source
├── scripts/            # Build and deployment scripts
├── package.json        # Node.js dependencies
├── vite.config.js      # Vite configuration
└── docs/               # Frontend documentation
```

### Benefits of Separation

| Benefit | Details |
|---------|---------|
| **Independent releases** | Deploy frontend and backend on separate schedules |
| **Isolated CI/CD** | Frontend build failures do not block infrastructure deployments |
| **Clear ownership** | Frontend team owns `agent13-frontend`; infra/backend team owns this repo |
| **Technology best practices** | Each repo can adopt tooling appropriate to its stack |
| **Easier onboarding** | Developers clone only the repository relevant to their work |
| **Granular permissions** | GitHub branch protection and CODEOWNERS rules per repository |

---

## Migration Path

See [`REPOSITORY_SEPARATION_GUIDE.md`](./REPOSITORY_SEPARATION_GUIDE.md) for a step-by-step migration guide including:

- Extracting `agent13-frontend` with full Git history
- Setting up independent CI/CD pipelines
- Defining API contracts between the frontend and backend
- Updating environment configuration references
