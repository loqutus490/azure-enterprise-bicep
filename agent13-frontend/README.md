# Agent13 Frontend (Local Dev)

## Prerequisites

- Node.js 20+
- Frontend SPA app registration in Entra ID
- Backend API app registration exposing `access_as_user`

## Setup

```bash
cp ../.env.shared.example ../.env.shared
# update values in ../.env.shared

cd agent13-frontend
./scripts/generate-vite-env.sh ../.env.shared .env.local
npm install
npm run dev
```

Open `http://localhost:5173`.

## Token Flow

1. User logs in with Entra popup.
2. Frontend requests `api://<BACKEND_CLIENT_ID>/access_as_user`.
3. Frontend sends bearer token to `POST /ask`.
4. API validates audience + scope/role.

## Notes

- API URL is generated from `RAG_API_BASE_URL` in `../.env.shared`.
- CORS must include `http://localhost:5173`.
