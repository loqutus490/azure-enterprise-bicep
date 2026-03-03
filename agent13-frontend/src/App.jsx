import { useMemo, useState } from "react";
import axios from "axios";
import { InteractionRequiredAuthError } from "@azure/msal-browser";
import { apiBaseUrl, loginRequest } from "./authConfig";

export default function App({ msalInstance }) {
  const [account, setAccount] = useState(msalInstance.getActiveAccount());
  const [question, setQuestion] = useState("");
  const [response, setResponse] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);

  const apiUrl = useMemo(() => `${apiBaseUrl.replace(/\/$/, "")}/ask`, []);

  const login = async () => {
    setError("");
    const result = await msalInstance.loginPopup(loginRequest);
    msalInstance.setActiveAccount(result.account);
    setAccount(result.account);
  };

  const logout = async () => {
    if (!account) return;
    await msalInstance.logoutPopup({ account });
    setAccount(null);
    setQuestion("");
    setResponse("");
  };

  const getAccessToken = async () => {
    try {
      const tokenResponse = await msalInstance.acquireTokenSilent({
        ...loginRequest,
        account
      });
      return tokenResponse.accessToken;
    } catch (err) {
      if (err instanceof InteractionRequiredAuthError) {
        const tokenResponse = await msalInstance.acquireTokenPopup(loginRequest);
        return tokenResponse.accessToken;
      }
      throw err;
    }
  };

  const ask = async () => {
    if (!question.trim() || !account) return;

    setLoading(true);
    setError("");

    try {
      const accessToken = await getAccessToken();
      const res = await axios.post(
        apiUrl,
        { question: question.trim() },
        {
          headers: {
            Authorization: `Bearer ${accessToken}`
          }
        }
      );

      setResponse(res.data?.answer || JSON.stringify(res.data));
    } catch (err) {
      setResponse("");
      setError(err.response?.data || err.message || "Request failed");
    } finally {
      setLoading(false);
    }
  };

  return (
    <main className="app-shell">
      <section className="card">
        <h1>Agent13 Legal AI</h1>

        {!account ? (
          <button onClick={login}>Login with Entra ID</button>
        ) : (
          <>
            <p className="meta">Signed in as {account.username}</p>
            <div className="row">
              <input
                value={question}
                onChange={(e) => setQuestion(e.target.value)}
                placeholder="Ask a legal question..."
              />
              <button onClick={ask} disabled={loading || !question.trim()}>
                {loading ? "Asking..." : "Ask"}
              </button>
              <button className="ghost" onClick={logout}>
                Logout
              </button>
            </div>
          </>
        )}

        {error && <pre className="error">{error}</pre>}
        {response && <pre className="response">{response}</pre>}
      </section>
    </main>
  );
}
