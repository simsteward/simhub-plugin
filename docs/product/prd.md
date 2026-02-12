# Product Requirements Document: Sim Steward

**Status:** Approved for Alpha  
**Version:** 1.1  
**Target Platform:** SimHub / iRacing / Cloudflare / Whop

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Personas](#2-user-personas)
3. [Phase 1: Alpha Requirements](#3-phase-1-alpha-requirements)
4. [Phase 2: Beta Requirements](#4-phase-2-beta-requirements)
5. [Technical Architecture](#5-technical-architecture)
6. [Constraints & Risks](#6-constraints--risks)
7. [Appendix A: Steward System Prompt](#appendix-a-the-steward-system-prompt-strategy)

---

## 1. Executive Summary

Sim Steward is a SimHub plugin designed to automate the friction-heavy process of reviewing incidents and filing protests in iRacing. By combining an "always-on" telemetry buffer with a serverless AI backend, the tool acts as an automated Race Control, instantly providing the driver with a ruling on fault, a detailed timeline of events, and a pre-written protest statement.

### Phased Goals

| Phase | Goal |
|-------|------|
| **Alpha** | Prove the core loop (Telemetry → AI Ruling) works reliably. |
| **Beta** | Validate the business model using Whop for licensing and launch a dedicated marketing website. |

---

## 2. User Personas

| Persona | Description |
|---------|-------------|
| **The League Racer** | Wants unbiased rulings on incidents to improve their racecraft and settle disputes with data. |
| **The Public Lobby Driver** | Needs a fast, low-effort way to report bad actors (intentional wrecking, unsafe rejoins) without spending 20 minutes finding and editing video. |

---

## 3. Phase 1: Alpha Requirements

### 3.1. Telemetry Recorder (Client-Side / SimHub)

| ID | Requirement |
|----|-------------|
| FR-A-001 | The system MUST maintain a circular buffer of telemetry data in RAM while the game is running. |
| FR-A-002 | The buffer MUST hold a minimum of 30 seconds of pre-incident data and 30 seconds of post-incident data. |
| FR-A-003 | The system MUST detect an "Incident" via two triggers: **Auto:** Increase in `PlayerCarTeamIncidentCount` (0x, 1x, 2x, 4x). **Manual:** User maps a specific button/hotkey to "Mark Incident." |
| FR-A-004 | Upon trigger, the system MUST serialize the telemetry window to a minimized format (CSV-style) to reduce payload size before transmission. |
| FR-A-005 | The serialized data MUST include `SessionTick` (for replay syncing) and `SessionNum`. |

### 3.2. Cloudflare Integration (Server-Side)

| ID | Requirement |
|----|-------------|
| FR-A-006 | The SimHub plugin MUST send telemetry data via HTTPS POST to a Cloudflare Worker. |
| FR-A-007 | The Cloudflare Worker MUST archive the raw telemetry to Cloudflare R2 (Object Storage) for debugging and future model training ("The Evidence Locker"). |
| FR-A-008 | The Cloudflare Worker MUST utilize Cloudflare Workers AI (Llama-3-8b-instruct or similar) to process the telemetry. |
| FR-A-009 | **The Token Diet:** The backend MUST process data in a token-efficient text format (Time-Series CSV) rather than raw JSON to ensure low latency and high accuracy. |

### 3.3. The "Steward" Persona (AI Logic)

| ID | Requirement |
|----|-------------|
| FR-A-010 | The AI prompt MUST enforce a "Chief Steward" persona, referencing the iRacing Official Sporting Code (Sections 2 & 6). |
| FR-A-011 | The AI output MUST be returned as a JSON object containing: **Short Summary** (1-sentence synopsis); **Detailed Report** (chronological timeline of inputs and physics events); **The Ruling** (clear verdict of fault); **Protest Statement** (formal text block ready for submission). |

### 3.4. User Interface (SimHub)

| ID | Requirement |
|----|-------------|
| FR-A-012 | **Main Plugin Tab:** Desktop UI containing the Master Incident List and Detailed Report View (HTML Rendered). |
| FR-A-013 | **In-Game Overlay:** Transparent overlay showing Status, Last 3 Incidents, and a "Mark" button. |
| FR-A-014 | **Visual Grading System:** Incidents must be visually categorized: 🔴 Red (Opponent At Fault); 🟡 Yellow (Racing Incident); ☠️ Skull (Player At Fault). |
| FR-A-015 | **Replay Jumping:** Clicking "Review" MUST send `irsdk_BroadcastReplaySearch` to jump to `IncidentTime - 30s`. |

---

## 4. Phase 2: Beta Requirements

### 4.1. Whop Integration (Commerce & Licensing)

| ID | Requirement |
|----|-------------|
| FR-B-001 | **Whop Marketplace Listing:** The product MUST be listed on Whop with a "Pro" subscription tier. |
| FR-B-002 | **License Generation:** Whop MUST automatically generate a License Key upon purchase. |
| FR-B-003 | **Discord Sync:** Whop MUST automatically assign a "Sim Steward Pro" role to the user's Discord account upon purchase (and remove it on cancellation). |
| FR-B-004 | **Client Validation:** The SimHub Plugin Settings tab MUST have a field for "License Key." |
| FR-B-005 | **Server Validation:** The Cloudflare Worker MUST validate the incoming License Key against the Whop API (`GET /api/v2/memberships/{key}/validate`) before processing any AI requests. If invalid, return `403 Forbidden`. |

### 4.2. Product Website (Marketing)

| ID | Requirement |
|----|-------------|
| FR-B-006 | **Landing Page:** A dedicated website (simsteward.com) containing: Hero Section (value proposition + "Get Access" CTA linking to Whop); How it Works (visual diagram); Features (Instant Replay Search, Unbiased Rulings, Protest Generator); Social Proof (testimonials, partner logos). |
| FR-B-007 | **Documentation Section:** Installation Guide; Configuration Guide (License Key setup); Troubleshooting (common SimHub/iRacing issues). |
| FR-B-008 | **Support:** Direct link to the Discord community for support tickets. |

---

## 5. Technical Architecture

### 5.1. Client-Side: SimHub Plugin (C#)

| Component | Details |
|-----------|---------|
| Framework | .NET Framework 4.8 |
| Key Libraries | SimHub.Plugins, iRacingSdkWrapper, Newtonsoft.Json |
| Responsibility | Reading Game Data → Buffering → Feature Engineering (CSV conversion) → HTTPS POST (with License Header) |

### 5.2. Backend-Side: Cloudflare Serverless

| Component | Details |
|-----------|---------|
| Infrastructure | 100% Cloudflare (Worker + R2 + Workers AI) |
| Auth Middleware | Intercepts every request to validate `X-License-Key` against Whop API |
| Cost Control | Caching valid license checks in Cloudflare KV for 1 hour to reduce Whop API calls |

---

## 6. Constraints & Risks

| Constraint / Risk | Mitigation |
|------------------|------------|
| **iRacing SDK Limitations** | Cannot "cut" the .rpy file directly. User must manually record video if needed. |
| **Whop Dependency** | If Whop API goes down, new users cannot activate. Cached users may still work (depending on KV logic). |
| **LLM Hallucinations** | The AI may misinterpret telemetry. Manual Override in UI is the fail-safe. |

---

## Appendix A: The Steward System Prompt Strategy

*To be implemented in the Cloudflare Worker*

```
You are the Chief Steward for an iRacing league. Analyze this telemetry CSV.

Rules: iRacing Sporting Code Section 2 (On-Track Conduct).

Input: Time-Series CSV (Time, Speed, Brake%, Steer%, Gap, Overlap).

Output: JSON containing:
  - verdict: PlayerAtFault | RacingIncident | OpponentAtFault
  - protest_text: Formal FIA style
```
