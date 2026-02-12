# Project Brief

## Project

**Sim Steward** – AI-powered incident review and protest automation for iRacing.

## Core Requirements

- SimHub plugin with telemetry buffer → Cloudflare Worker → AI ruling → verdict + protest statement
- Phase Alpha: Prove Telemetry → AI Ruling loop works reliably
- Phase Beta: Whop licensing, marketing website

## Scope

- **In**: SimHub plugin (C#), Cloudflare Workers + R2 + Workers AI, iRacing SDK integration
- **Out (Alpha)**: Whop, licensing, public website

## Success Criteria

Alpha complete when: incident trigger → telemetry POST → AI ruling JSON returned → displayed in SimHub UI.
