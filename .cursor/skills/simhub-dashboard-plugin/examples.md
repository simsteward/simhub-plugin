# SimHub Examples
- Live Speed Gauge: Send `data.speed` via WS. Update `document.getElementById('speed-num').textContent`.
- Pit Request: JS `ws.send(JSON.stringify({action:'RequestPit'}))`. C# `HandleClientMessage()` toggles `PitRequested`.
- Gear Indicator: Read `data.gear`, update DOM text.
- Full WS: Connect `new WebSocket('ws://localhost:19847')`, handle `onmessage` parsing JSON.
- Physics Crash Detection: Read `LongAccel`, `LatAccel`, `YawRate` via IRSDKSharper in `OnTelemetryData` to detect collisions independently of incident points.
