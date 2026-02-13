# Project Brief

## Project

**Sim Steward** -- Fast incident clipping tool for iRacing protests. Detect incidents, jump to replay, record clips via OBS, save locally.

## Core Requirements

- SimHub plugin that detects iRacing incidents (auto + manual)
- In-game overlay notification with replay jump
- OBS WebSocket integration for one-click clip recording
- Part 2: Automated multi-camera replay loops with video stitching

## Scope

- **In:** SimHub plugin (C#), iRacing SDK (replay/camera control), OBS WebSocket 5.x (recording)
- **Out:** Backend server, AI analysis, monetization, web platform

## Success Criteria

MVP complete when: incident detected -> overlay notification -> replay jump -> OBS records clip -> file saved locally. End-to-end in seconds, not minutes.
