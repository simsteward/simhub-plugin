const LOKI_URL = process.env.SIMSTEWARD_LOKI_URL ?? 'http://localhost:3100';

async function queryLokiRaw(logqlQuery: string, lookbackMinutes = 30): Promise<Record<string, unknown>[]> {
  const startNs = (Date.now() - lookbackMinutes * 60_000) * 1_000_000;
  const endNs = Date.now() * 1_000_000;
  const qs = new URLSearchParams({
    query: logqlQuery,
    start: String(startNs),
    end: String(endNs),
    limit: '20',
    direction: 'BACKWARD',
  });
  const res = await fetch(`${LOKI_URL}/loki/api/v1/query_range?${qs}`);
  const json = (await res.json()) as { data?: { result?: { values?: [string, string][] }[] } };
  return (json.data?.result ?? []).flatMap((stream) =>
    (stream.values ?? []).map(([, line]) => JSON.parse(line) as Record<string, unknown>)
  );
}

/** Query SimHub plugin logs (app=sim-steward) by event name. */
export function queryPluginLoki(eventName: string, lookbackMinutes = 30) {
  return queryLokiRaw(`{app="sim-steward"} | json | event="${eventName}"`, lookbackMinutes);
}

/** Query Claude Code hook logs (app=claude-dev-logging) by hook type. */
export function queryClaudeLoki(hookType: string, lookbackMinutes = 30) {
  return queryLokiRaw(`{app="claude-dev-logging"} | json | hook_type="${hookType}"`, lookbackMinutes);
}
