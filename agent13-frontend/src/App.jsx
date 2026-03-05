import { useMemo, useState } from "react";
import axios from "axios";
import { InteractionRequiredAuthError } from "@azure/msal-browser";
import { apiBaseUrl, loginRequest } from "./authConfig";

const demoPrompts = [
  {
    label: "Indemnification Summary",
    matterId: "MATTER-001",
    question: "Summarize indemnification obligations and key limitations."
  },
  {
    label: "Termination Rights",
    matterId: "MATTER-001",
    question: "What termination rights exist and what notice periods apply?"
  },
  {
    label: "Confidentiality Risks",
    matterId: "MATTER-002",
    question: "List confidentiality obligations and potential breach risks."
  }
];

function parseError(err) {
  if (!err) return "Request failed.";
  if (typeof err.response?.data === "string") return err.response.data;
  if (err.response?.data?.title) return err.response.data.title;
  if (err.response?.status) return `Request failed with status ${err.response.status}.`;
  return err.message || "Request failed.";
}

export default function App({ msalInstance }) {
  const [account, setAccount] = useState(msalInstance.getActiveAccount());
  const [question, setQuestion] = useState("");
  const [matterId, setMatterId] = useState("");
  const [response, setResponse] = useState(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const [history, setHistory] = useState([]);

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
    setMatterId("");
    setResponse(null);
    setHistory([]);
    setError("");
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
    if (!question.trim() || !matterId.trim() || !account) return;

    setLoading(true);
    setError("");
    setResponse(null);

    try {
      const accessToken = await getAccessToken();
      const res = await axios.post(
        apiUrl,
        { question: question.trim(), matterId: matterId.trim() },
        {
          headers: {
            Authorization: `Bearer ${accessToken}`
          }
        }
      );

      const answerText = res.data?.answer || "No response body returned.";
      const sources = Array.isArray(res.data?.sources)
        ? res.data.sources.filter((source) => typeof source === "string" && source.trim())
        : [];
      const payload = {
        question: question.trim(),
        matterId: matterId.trim(),
        answer: answerText,
        sources,
        at: new Date().toLocaleTimeString()
      };
      setResponse(payload);
      setHistory((prev) => [payload, ...prev].slice(0, 6));
    } catch (err) {
      setResponse(null);
      setError(parseError(err));
    } finally {
      setLoading(false);
    }
  };

  return (
    <main className="app-shell">
      <section className="card">
        <header className="headline">
          <div>
            <h1>Agent13 Legal AI Demo</h1>
            <p className="meta">Grounded legal Q&A with matter-level scoping.</p>
          </div>
          {!account ? (
            <button onClick={login}>Login with Entra ID</button>
          ) : (
            <button className="ghost" onClick={logout}>
              Logout
            </button>
          )}
        </header>

        {!account && (
          <div className="panel">
            <p className="meta">
              Sign in to query the protected <code>/ask</code> endpoint.
            </p>
          </div>
        )}

        {account && (
          <>
            <div className="panel">
              <p className="meta">Signed in as {account.username}</p>
              <div className="row">
                <input
                  value={matterId}
                  onChange={(e) => setMatterId(e.target.value)}
                  placeholder="Matter ID (required)"
                />
                <input
                  value={question}
                  onChange={(e) => setQuestion(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") ask();
                  }}
                  placeholder="Ask a legal question..."
                />
                <button onClick={ask} disabled={loading || !question.trim() || !matterId.trim()}>
                  {loading ? "Asking..." : "Ask"}
                </button>
              </div>
            </div>

            <div className="panel">
              <p className="meta">Demo prompts</p>
              <div className="chips">
                {demoPrompts.map((prompt) => (
                  <button
                    key={prompt.label}
                    className="ghost chip"
                    onClick={() => {
                      setMatterId(prompt.matterId);
                      setQuestion(prompt.question);
                    }}
                  >
                    {prompt.label}
                  </button>
                ))}
              </div>
            </div>
          </>
        )}

        {error && <pre className="error">{error}</pre>}

        {response && (
          <section className="response">
            <p className="meta">
              Latest answer for <strong>{response.matterId}</strong> at {response.at}
            </p>
            <pre>{response.answer}</pre>
            <div className="citations">
              <p className="meta">Sources ({response.sources.length})</p>
              {response.sources.length > 0 ? (
                <ul className="history">
                  {response.sources.map((source) => (
                    <li key={source}>
                      <code>{source}</code>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="meta">No source files returned for this response.</p>
              )}
            </div>
          </section>
        )}

        {account && history.length > 0 && (
          <section className="panel">
            <h2>Recent Questions</h2>
            <ul className="history">
              {history.map((item, idx) => (
                <li key={`${item.at}-${idx}`}>
                  <p>
                    <strong>{item.matterId}</strong> - {item.question}
                  </p>
                  <p className="meta">Sources: {item.sources.length}</p>
                </li>
              ))}
            </ul>
          </section>
        )}
      </section>
    </main>
  );
}
