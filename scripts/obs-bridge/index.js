#!/usr/bin/env node
/**
 * Sim Steward ↔ OBS bridge
 *
 * Connects to Sim Steward WebSocket (Fleck, default ws://localhost:19847) and
 * OBS WebSocket (obs-websocket 5.x, default ws://127.0.0.1:4455). On each
 * incidentEvents push from Sim Steward:
 *   1. Optionally sends ReplaySeekFrame to Sim Steward so iRacing seeks to the
 *      incident frame before recording.
 *   2. Starts OBS recording. Clip naming uses sessionId and replayFrameNum when
 *      available (see README for filename format).
 *
 * Contract: docs/INTERFACE.md (§3.2 incidentEvents, §5 sessionId).
 * OBS WebSocket: https://github.com/obsproject/obs-websocket (v5).
 */

import WebSocket from "ws";
import OBSWebSocket from "obs-websocket-js";
import dotenv from "dotenv";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, "../..");
dotenv.config({ path: path.join(REPO_ROOT, ".env") });

const SIMSTEWARD_WS_URL = process.env.SIMSTEWARD_WS_URL || "ws://127.0.0.1:19847";
const OBS_WS_URL = process.env.OBS_WS_URL || "ws://127.0.0.1:4455";
const OBS_PASSWORD = process.env.OBS_PASSWORD || "";
const SEEK_BEFORE_RECORD = process.env.SEEK_BEFORE_RECORD !== "0";
const RECORD_DURATION_SEC = parseInt(process.env.RECORD_DURATION_SEC || "30", 10) || 0;

let simStewardWs = null;
let obs = null;
let lastState = { sessionId: "" };
let recordStopTimer = null;

function log(msg) {
  const ts = new Date().toISOString();
  console.log(`[${ts}] ${msg}`);
}

function connectSimSteward() {
  return new Promise((resolve, reject) => {
    const url = new URL(SIMSTEWARD_WS_URL);
    if (process.env.SIMSTEWARD_WS_TOKEN) {
      url.searchParams.set("token", process.env.SIMSTEWARD_WS_TOKEN);
    }
    simStewardWs = new WebSocket(url.toString());
    simStewardWs.on("open", () => {
      log(`Sim Steward: connected to ${SIMSTEWARD_WS_URL}`);
      resolve();
    });
    simStewardWs.on("message", (data) => {
      try {
        const msg = JSON.parse(data.toString());
        if (msg.type === "state") {
          lastState.sessionId = msg.sessionId || lastState.sessionId;
        } else if (msg.type === "incidentEvents" && Array.isArray(msg.events) && msg.events.length > 0) {
          handleIncidentEvents(msg.events);
        }
      } catch (e) {
        // ignore parse errors
      }
    });
    simStewardWs.on("close", () => {
      log("Sim Steward: disconnected");
      scheduleReconnect(connectSimSteward);
    });
    simStewardWs.on("error", (err) => {
      log(`Sim Steward: ${err.message}`);
      reject(err);
    });
  });
}

function sendSimStewardAction(action, arg) {
  if (!simStewardWs || simStewardWs.readyState !== WebSocket.OPEN) return;
  const payload = arg !== undefined ? { action, arg: String(arg) } : { action };
  simStewardWs.send(JSON.stringify(payload));
}

function scheduleReconnect(connectFn, delayMs = 3000) {
  setTimeout(() => {
    connectFn().catch((err) => {
      log(`Reconnect failed: ${err.message}. Retrying in ${delayMs / 1000}s.`);
      scheduleReconnect(connectFn, Math.min(delayMs * 2, 30000));
    });
  }, delayMs);
}

async function handleIncidentEvents(events) {
  for (const ev of events) {
    const { replayFrameNum, sessionId, id, type, driverName, carNumber } = ev;
    const sid = sessionId || lastState.sessionId || "session";
    const frame = replayFrameNum || 0;
    log(`Incident: ${type} #${carNumber} ${driverName} (id=${id}, frame=${frame}, sessionId=${sid})`);

    if (SEEK_BEFORE_RECORD && frame > 0) {
      sendSimStewardAction("ReplaySeekFrame", String(frame));
      await new Promise((r) => setTimeout(r, 300));
    }

    if (obs) {
      try {
        await obs.call("StartRecord");
        log(`OBS: recording started (suggested filename: ${sid}_inc_${frame}.mkv)`);
        if (RECORD_DURATION_SEC > 0) {
          if (recordStopTimer) clearTimeout(recordStopTimer);
          recordStopTimer = setTimeout(async () => {
            try {
              await obs.call("StopRecord");
              log("OBS: recording stopped (duration limit)");
            } catch (e) {
              log(`OBS: StopRecord failed: ${e.message}`);
            }
            recordStopTimer = null;
          }, RECORD_DURATION_SEC * 1000);
        }
      } catch (e) {
        log(`OBS: StartRecord failed: ${e.message}`);
      }
    }
  }
}

async function connectOBS() {
  const client = new OBSWebSocket();
  try {
    await client.connect(OBS_WS_URL, OBS_PASSWORD);
    log(`OBS: connected to ${OBS_WS_URL}`);
    obs = client;
    obs.on("ConnectionClosed", () => {
      log("OBS: disconnected");
      obs = null;
      scheduleReconnect(connectOBS);
    });
  } catch (e) {
    log(`OBS: connect failed: ${e.message}. Will retry when incidents fire.`);
    obs = null;
  }
}

async function main() {
  log("Sim Steward ↔ OBS bridge");
  log(`  Sim Steward: ${SIMSTEWARD_WS_URL}`);
  log(`  OBS: ${OBS_WS_URL}`);
  log(`  Seek before record: ${SEEK_BEFORE_RECORD}`);
  if (RECORD_DURATION_SEC > 0) log(`  Auto-stop record after: ${RECORD_DURATION_SEC}s`);

  await connectOBS();
  await connectSimSteward();
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
