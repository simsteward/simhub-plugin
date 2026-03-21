# Sim Steward ↔ OBS Bridge

Bridges the Sim Steward plugin WebSocket to OBS WebSocket (obs-websocket 5.x). When the plugin pushes **incidentEvents**, the bridge:

1. **Seeks iRacing replay** (optional) — Sends `ReplaySeekFrame` to Sim Steward so the replay is at the incident frame before recording.
2. **Starts OBS recording** — Calls OBS `StartRecord`. Suggested clip naming: `{sessionId}_inc_{replayFrameNum}.mkv` (see below).

## Requirements

- **Node.js** 18+
- **Sim Steward** plugin running in SimHub (WebSocket on port 19847).
- **OBS Studio** with **obs-websocket** 5.x (OBS 28+, default port 4455).

## Install

```bash
cd scripts/obs-bridge
npm install
```

## Run

```bash
npm start
```

Or from the repo root:

```bash
node scripts/obs-bridge/index.js
```

### Using `.env` (recommended)

The bridge auto-loads environment variables from the **repo root** `.env` file using `dotenv`.

```bash
copy .env.example .env
# edit the root .env with your local values
cd scripts/obs-bridge
npm start
```

## Environment

| Variable | Default | Description |
|----------|---------|-------------|
| `SIMSTEWARD_WS_URL` | `ws://127.0.0.1:19847` | Sim Steward WebSocket URL. |
| `SIMSTEWARD_WS_TOKEN` | — | If set, appended as `?token=...` to the URL. |
| `OBS_WS_URL` | `ws://127.0.0.1:4455` | OBS WebSocket URL (obs-websocket 5). |
| `OBS_PASSWORD` | — | OBS WebSocket server password (if required). |
| `SEEK_BEFORE_RECORD` | `1` | Set to `0` to disable sending `ReplaySeekFrame` before starting record. |
| `RECORD_DURATION_SEC` | `0` | If &gt; 0, automatically stop recording after this many seconds; `0` = do not auto-stop. |

## Clip naming

The plugin sends **sessionId** in state (`{trackName}_{yyyyMMdd}`) and **replayFrameNum** on each incident. The bridge logs a suggested filename: `{sessionId}_inc_{replayFrameNum}.mkv`.

OBS WebSocket 5 does not allow passing a custom filename into `StartRecord`. To get this naming:

- Set OBS **Settings → Output → Recording** path to a folder and use a **Recording Format** that you change before each run, or
- Use a post-processing step to rename the last recording (e.g. from `GetRecordStatus` / `RecordingStopped` and then rename the file), or
- Rely on OBS’s default naming and use the bridge only for “seek + start record” automation.

## Data contract

- **Sim Steward:** WebSocket JSON — `state` includes `sessionId`, `incidentEvents`; dashboard sends action `ReplaySeekFrame` with frame number. See plugin/dashboard sources and **docs/TROUBLESHOOTING.md** for connection/setup.
- **OBS:** [obs-websocket protocol](https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md) — `StartRecord`, `StopRecord`.
