# System Patterns

## Architecture

```
SimHub Plugin (C#)          Cloudflare
     │                           │
     │  HTTPS POST (CSV)         │
     ├──────────────────────────►│  Worker
     │                           │    ├── R2 (archive)
     │                           │    └── Workers AI (Llama)
     │  JSON response            │
     │◄──────────────────────────┤
     │                           │
     ▼                           │
  SimHub UI                      │
  (incident list,               │
   verdict, protest)            │
```

## Key Decisions

- **Token Diet**: Time-series CSV input to AI, not raw JSON – lower latency, higher accuracy
- **Evidence Locker**: All telemetry archived to R2 for debugging and future model training
- **Steward Persona**: AI references iRacing Sporting Code Sections 2 & 6

## Component Relationships

- Plugin reads iRacing SDK → circular buffer → on trigger: serialize → POST
- Worker: validate → archive R2 → call Workers AI → return JSON
- Plugin: display verdict, timeline, protest; replay jump via irsdk_BroadcastReplaySearch
