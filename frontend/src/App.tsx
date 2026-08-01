import { useEffect, useState } from "react";
import "./App.css";

type ApiInformation = {
  name: string;
  version: string;
  environment: string;
};

const apiBaseUrl =
  import.meta.env.VITE_API_BASE_URL ??
  "http://localhost:5080";

function App() {
  const [apiInformation, setApiInformation] =
    useState<ApiInformation | null>(null);

  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    async function loadApiInformation() {
      try {
        const response = await fetch(
          `${apiBaseUrl}/api/version`,
          { signal: controller.signal }
        );

        if (!response.ok) {
          throw new Error(
            `The API returned HTTP ${response.status}.`
          );
        }

        const data =
          (await response.json()) as ApiInformation;

        setApiInformation(data);
      } catch (requestError) {
        if (
          requestError instanceof Error &&
          requestError.name !== "AbortError"
        ) {
          setError(requestError.message);
        }
      }
    }

    void loadApiInformation();

    return () => controller.abort();
  }, []);

  return (
    <main>
      <h1>Realtime Chat Platform</h1>
      <p>Stage 0 — Foundation</p>

      {apiInformation && (
        <section>
          <h2>Backend connected</h2>
          <p>API: {apiInformation.name}</p>
          <p>Version: {apiInformation.version}</p>
          <p>Environment: {apiInformation.environment}</p>
        </section>
      )}

      {!apiInformation && !error && (
        <p>Connecting to the backend...</p>
      )}

      {error && (
        <section>
          <h2>Backend connection failed</h2>
          <p>{error}</p>
        </section>
      )}
    </main>
  );
}

export default App;