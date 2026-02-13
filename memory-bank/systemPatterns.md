# System Patterns

## Architecture

```
┌──────────────────────────────────────────┐
│              SimHub Host                  │
│                                          │
│  ┌──────────────────────────────────┐    │
│  │     Sim Steward Plugin (C#)     │    │
│  │                                  │    │
│  │  Incident Detector               │    │
│  │    ├── Auto (IncidentCount Δ)    │    │
│  │    └── Manual (hotkey)           │    │
│  │                                  │    │
│  │  Replay Controller               │    │
│  │    ├── Jump to timestamp         │    │
│  │    └── Camera switching (Part 2) │    │
│  │                                  │    │
│  │  OBS WebSocket Client            │    │
│  │    ├── Connect/reconnect         │    │
│  │    └── Start/stop recording      │    │
│  │                                  │    │
│  │  UI Layer                        │    │
│  │    ├── Overlay (Dash Studio)     │    │
│  │    └── Settings (WPF tab)        │    │
│  └──────────────────────────────────┘    │
└──────────┬───────────────────┬───────────┘
           │                   │
           ▼                   ▼
     ┌───────────┐       ┌───────────┐
     │  iRacing   │       │    OBS    │
     │  (irsdk)   │       │ (ws 5.x) │
     └───────────┘       └───────────┘
```

**No backend.** All processing is local.

## Key Decisions

- **OBS as recording backbone.** Plugin orchestrates; OBS does the actual recording. No custom video capture.
- **iRacing SDK for replay control.** `BroadcastReplaySearchSessionTime` for jumps, `CamSwitchNum` for camera switching (Part 2).
- **Detect, don't analyze.** Plugin detects incidents by `PlayerCarTeamIncidentCount` delta. No classification, no AI. Just "incident happened at time X."
- **Two external dependencies.** iRacing (must be running) and OBS (must be running with WebSocket enabled). Plugin gracefully handles missing connections.

## Component Relationships

- Plugin monitors iRacing SDK -> detects incident -> fires event with session timestamp
- Overlay shows notification -> user clicks "Jump to Replay"
- Plugin sends replay broadcast command to iRacing
- User triggers recording -> plugin sends start/stop to OBS via WebSocket
- Part 2: Plugin automates the replay-camera-record loop across multiple angles
