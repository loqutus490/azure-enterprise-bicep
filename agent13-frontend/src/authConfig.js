const frontendClientId = import.meta.env.VITE_AZURE_FRONTEND_CLIENT_ID;
const tenantId = import.meta.env.VITE_AZURE_TENANT_ID;
const backendClientId = import.meta.env.VITE_AZURE_BACKEND_CLIENT_ID;

if (!frontendClientId || !tenantId || !backendClientId) {
  // Fail fast during startup if identity settings are missing.
  throw new Error(
    "Missing Vite env vars. Set VITE_AZURE_FRONTEND_CLIENT_ID, VITE_AZURE_TENANT_ID, and VITE_AZURE_BACKEND_CLIENT_ID."
  );
}

export const apiBaseUrl =
  import.meta.env.VITE_AGENT13_API_BASE_URL ||
  "https://agent13-api-dev.azurewebsites.net";

export const msalConfig = {
  auth: {
    clientId: frontendClientId,
    authority: `https://login.microsoftonline.com/${tenantId}`,
    redirectUri: "http://localhost:5173"
  },
  cache: {
    cacheLocation: "sessionStorage"
  }
};

export const loginRequest = {
  scopes: [`api://${backendClientId}/access_as_user`]
};
