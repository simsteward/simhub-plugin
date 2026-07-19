import { describe, it, expect } from "vitest";
import { chooseWinningRow, rankForSource, incomingWins } from "../src/upsert";

describe("rankForSource", () => {
  it("ranks replay_reconciled above live", () => {
    expect(rankForSource("replay_reconciled")).toBe(2);
    expect(rankForSource("live")).toBe(1);
  });
  it("treats unknown sources as lowest trust", () => {
    expect(rankForSource("something_else")).toBe(1);
  });
});

describe("chooseWinningRow", () => {
  const live = { source_rank: 1, tag: "live" };
  const replay = { source_rank: 2, tag: "replay" };

  it("takes incoming when there is no existing row", () => {
    expect(chooseWinningRow(null, live)).toBe(live);
  });

  it("rank 2 (replay) beats rank 1 (live)", () => {
    expect(chooseWinningRow(live, replay)).toBe(replay);
  });

  it("rank 1 (live) never overwrites rank 2 (replay)", () => {
    expect(chooseWinningRow(replay, live)).toBe(replay);
  });

  it("equal ranks converge — incoming wins (idempotent overwrite)", () => {
    const a = { source_rank: 2, tag: "a" };
    const b = { source_rank: 2, tag: "b" };
    expect(chooseWinningRow(a, b)).toBe(b);
  });
});

describe("incomingWins (mirrors SQL WHERE)", () => {
  it("matches the >= semantics", () => {
    expect(incomingWins(1, 2)).toBe(true); // replay over live
    expect(incomingWins(2, 1)).toBe(false); // live can't overwrite replay
    expect(incomingWins(2, 2)).toBe(true); // equal converges
    expect(incomingWins(1, 1)).toBe(true);
  });
});
