# Archimedes – Architecture

## Overview

Archimedes is a local-first autonomous PC agent with three main components:

```
┌─────────────┐     ┌─────────────┐     ┌─────────────┐
│   Android   │◄───►│     Net     │◄───►│    Core     │
│  (Kotlin)   │     │ (Node TS)   │     │ (C# .NET 8) │
└─────────────┘     └─────────────┘     └─────────────┘
       │                    │                    │
       └────────────────────┼────────────────────┘
                            │
                     ┌──────▼──────┐
                     │  Firebase   │
                     │ (Firestore) │
                     └─────────────┘
```

## Components

### Core (C# .NET 8)
- Task execution engine
- Scheduler (jobs + runs)
- Local HTTP server
- Keypair generation and E2E encryption

### Net (Node.js TypeScript)
- HTTP server (bridge to Core)
- Firebase/Firestore integration
- Message queue with Core
- Push (FCM) ping structure

### Android (Kotlin)
- Pairing (QR scan)
- Inbox / approval flow
- End-to-end messaging

## Data Flow (Target)

1. User sends task → Core
2. Core executes → may request approval
3. Core → Net → Firebase → Android
4. User approves/denies on Android
5. Android → Firebase → Net → Core
6. Core resumes and completes

## Current Phase

**MVP Complete.** All 12 phases done.

### Endpoints

**Core (5051):** /health, /ping-net, /envelope, /send-envelope, /pairing-data, /pairing-complete, /task/run-with-approval, /approvals, /approval-response, /job, /job/{id}/run, /run/{id}, /monitor/ticks, /state/save, /state/load, /state/clear, /crypto-test, /log-test/fail

**Net (5052):** /health, /envelope, /from-android, /approvals, /approval-response, /firestore-test
