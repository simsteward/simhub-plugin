// Rank-based conflict resolution for incident rows.
//
// Incidents can arrive from two sources with different trust levels:
//   - "live"               → source_rank 1 (real-time detection, may be partial)
//   - "replay_reconciled"  → source_rank 2 (post-race index build, authoritative)
//
// A higher-or-equal rank wins. This pure function is the single source of truth
// for that decision and is mirrored in the D1 upsert SQL's WHERE clause
// (`WHERE excluded.source_rank >= incidents.source_rank`) so the two never drift.

export type IncidentSource = "live" | "replay_reconciled";

export interface RankedRow {
  source_rank: number;
}

export function rankForSource(source: string): number {
  switch (source) {
    case "replay_reconciled":
      return 2;
    case "live":
      return 1;
    default:
      // Unknown sources are treated as the lowest trust level.
      return 1;
  }
}

// Returns the row that should win. When ranks are equal the incoming row wins
// (converge / idempotent overwrite with identical-or-newer data — mirrors the
// SQL `>=`). A strictly lower-ranked incoming row never overwrites.
export function chooseWinningRow<E extends RankedRow, I extends RankedRow>(
  existing: E | null | undefined,
  incoming: I,
): E | I {
  if (!existing) return incoming;
  return incoming.source_rank >= existing.source_rank ? incoming : existing;
}

// Convenience predicate matching the SQL WHERE clause exactly.
export function incomingWins(existingRank: number, incomingRank: number): boolean {
  return incomingRank >= existingRank;
}
