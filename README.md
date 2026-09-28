# THRESHOLD — Agentic AI Top-Down Shooter

![THRESHOLD project thumbnail artwork showing a sci-fi character and robotic enemy](Assets/Images/Thumbnail%20Cropped.png)

*Existing project thumbnail artwork; not a gameplay screenshot.*

> **Google Antigravity Mobile Game Challenge 2026**  
> *A procedurally generated dungeon crawler where every NPC thinks, every level is unique, and the game adapts to YOU.*

---

## Table of Contents

1. [Game Overview](#game-overview)
2. [Architecture](#architecture)
3. [The 5-Agent System](#the-5-agent-system)
4. [Data Schemas](#data-schemas)
5. [Tools & APIs](#tools--apis)
6. [Antigravity's Role in Development](#antigravitys-role-in-development)
7. [Setup & Installation](#setup--installation)
8. [Assumptions](#assumptions)
9. [Privacy & Safety](#privacy--safety)
10. [Cost & Latency](#cost--latency)
11. [Scalability](#scalability)
12. [Baseline Comparison](#baseline-comparison)
13. [Limitations](#limitations)

---

## Game Overview

**THRESHOLD** is a top-down roguelike shooter built in Unity where the entire experience is orchestrated by a 5-agent AI pipeline. Every dungeon is procedurally generated and validated by AI. NPCs observe, reason, and adapt mid-combat using a real-time Brain agent. Difficulty scales contextually based on player performance, and rewards are earned through effort — not randomness.

### Core Loop
```
Player Action → Agent Observation → AI Reasoning → Adaptive Response → Contextual Reward → Progression
```

### Key Features
- **Procedural Dungeon Generation** — Rooms are spatially validated and AI-populated with contextual roles (COMBAT, AMBUSH, BOSS, LOOT)
- **Intelligent NPCs** — 4 archetypes (Grunt, Flanker, Suppressor, Elite) with state machines driven by AI reasoning
- **NPC Defection** — NPCs can evaluate whether to betray their faction and assist the player
- **Adaptive Difficulty** — 5+ signal difficulty adjustment (kills, deaths, accuracy, rooms cleared, time)
- **Contextual Rewards** — XP and unlocks determined by AI evaluation of effort, not fixed formulas
- **Fair Play Referee** — Anti-exploit detection with suspicion scoring

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                    GAME MANAGER (Core)                        │
│   Orchestrates game state, spawning, and agent coordination   │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌────────────┐  ┌────────────┐  ┌────────────────────────┐ │
│  │  Director   │→ │  Level Gen  │→ │  QC Validator          │ │
│  │  Agent (A1) │  │  Agent (A2) │  │  Agent (A3)            │ │
│  │  [Flash]    │  │ [Hybrid]    │  │  [Flash]               │ │
│  └────────────┘  └────────────┘  └────────────────────────┘ │
│         │                                                    │
│         ↓                                                    │
│  ┌──────────────────────────────────────────────────────┐   │
│  │               FLOOR GENERATOR                         │   │
│  │   Instantiates rooms, NPCs, items from config         │   │
│  └──────────────────────────────────────────────────────┘   │
│         │                                                    │
│    ┌────┴────┐                                              │
│    ↓         ↓                                              │
│  ┌────────┐ ┌────────────┐                                  │
│  │ NPC    │ │ Reward     │                                  │
│  │ Brain  │ │ Agent (A5) │                                  │
│  │ (A4)   │ │ [Flash]    │                                  │
│  │[Flash] │ └────────────┘                                  │
│  └────────┘                                                  │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│  GeminiAgentBridge — Unified LLM gateway                     │
│  Supports: Gemini, Groq (Llama), NVIDIA NIM                 │
│  Features: Rate limiting, 429 retry, trace logging, export   │
└──────────────────────────────────────────────────────────────┘
```

### File Structure

```
Assets/Scripts/
├── Agents/                          # AI Agent System
│   ├── GeminiAgentBridge.cs         # Singleton LLM gateway (Gemini/Groq/NVIDIA)
│   ├── AgentDataTypes.cs            # Request/Response/Trace data types
│   ├── DirectorAgentCaller.cs       # Director Agent (A1) logic
│   └── RewardManager.cs             # Reward Agent (A5) + XP progression
├── Core/                            # Game Infrastructure
│   ├── GameManager.cs               # Master orchestrator
│   ├── RoomGraphConfig.cs           # Dungeon graph data model
│   ├── SessionMetrics.cs            # Agentic vs Baseline metric tracking
│   ├── FixedRuleDirector.cs         # Non-agentic baseline comparison
│   └── AgentDebugPanel.cs           # Real-time trace visualization
├── Generation/                      # Procedural Content
│   ├── LevelGenerationPipeline.cs   # Full AI + Hybrid generation pipeline
│   ├── ProceduralRoomGenerator.cs   # Spatial layout generation
│   ├── FloorGenerator.cs            # Physical room instantiation
│   ├── RoomModule.cs                # Individual room prefab logic
│   └── LayoutHistoryManager.cs      # Novelty tracking across runs
├── NPC/                             # Enemy AI
│   ├── NPCStateMachine.cs           # 7-state FSM with difficulty levers
│   └── NPCBrainController.cs        # LLM-powered NPC decision engine (A4)
├── Player/                          # Player Systems
│   ├── PlayerController.cs          # Rigidbody movement + joystick input
│   ├── PlayerHealth.cs              # Health, damage, iFrames, hit feedback
│   ├── PlayerWeapon.cs              # Shooting, ammo, tracers, reload
│   └── PickupItem.cs                # Universal pickup system
└── UI/                              # User Interface
    └── ThresholdUIManager.cs        # Virtual joystick, HUD, menus
```

---

## The 5-Agent System

Every agent call returns a **mandatory 5-step reasoning trace**:

```json
{
  "observation":      "<what the agent sees in the game state>",
  "inference":        "<conclusion drawn from observation>",
  "decision":         "<chosen action and reasoning>",
  "action":           "<JSON payload — the actual output>",
  "evaluation_plan":  "<metric to validate success>"
}
```

### Agent A1: Director
- **Role:** Analyzes player metrics across runs and sets the `DifficultyProfile` for the next floor
- **Model:** Flash (Llama 3.1 8B / Gemini 2.0 Flash)
- **Inputs:** Kill count, death count, accuracy, rooms cleared, run duration, win/loss streak
- **Outputs:** `difficultyMultiplier`, `roomCount`, NPC archetype distribution, event suggestions
- **Calls/Run:** 1

### Agent A2: Level Generator (Hybrid Mode)
- **Role:** Generates dungeon layouts. In Hybrid mode, local code handles spatial validity while AI assigns room roles (COMBAT, AMBUSH, BOSS, LOOT)
- **Model:** Flash (role assignment) / Pro (full spatial generation in AI_Full mode)
- **Inputs:** Director's `DifficultyProfile`, floor number, layout history
- **Outputs:** `RoomGraphConfig` with room roles, spawn zones, item placements
- **Calls/Run:** 1-2

### Agent A3: Quality Controller
- **Role:** Validates generated levels for spatial integrity, connectivity (BFS reachability), doorway consistency, and role distribution
- **Model:** Flash
- **Inputs:** Generated `RoomGraphConfig`
- **Outputs:** accept/reject with specific failure reasons
- **Calls/Run:** 1-3 (retry loop)

### Agent A4: NPC Brain
- **Role:** Real-time tactical decision-making for NPCs during combat. Evaluates whether to attack, flank, suppress, retreat, or **defect** to the player's side
- **Model:** Flash (3s timeout for real-time performance)
- **Inputs:** NPC health, player distance, ammo state, nearby ally count, suppression status
- **Outputs:** State transitions (PATROL, ATTACK, FLANK, SUPPRESS, RETREAT, DEFECT)
- **Calls/Run:** 6-10

### Agent A5: Reward Manager
- **Role:** Evaluates end-of-run performance to determine contextual rewards. Considers effort, progress, retention risk, and fair play
- **Model:** Flash
- **Inputs:** Run metrics (kills, deaths, rooms, duration, defections), progression history
- **Outputs:** `baseXP`, `bonusMultiplier`, unlock suggestions, challenge assignments, fair play flag
- **Calls/Run:** 1

---

## Data Schemas

### RoomGraphConfig (Level Data)
```json
{
  "rooms": [
    {
      "id": "room_0",
      "role": "ENTRY",
      "gridPosition": { "x": 0, "y": 0 },
      "shape": "RECTANGLE",
      "doorways": ["NORTH", "EAST"],
      "connections": [{ "targetRoomId": "room_1", "direction": "EAST" }],
      "spawnZones": [{ "archetype": "grunt", "count": 2, "position": {...} }],
      "items": [{ "type": "HealthKit", "position": {...} }]
    }
  ],
  "metadata": { "generatedBy": "hybrid", "floorNumber": 1 },
  "difficulty": { "difficultyMultiplier": 1.0, "totalNPCs": 8 }
}
```

### AgentTrace (5-Step Reasoning)
```json
{
  "observation": "Player died 3 times in 2 runs, avg 45% accuracy, 2 rooms cleared",
  "inference": "Player is struggling — difficulty is too high for skill level",
  "decision": "Reduce difficulty to 0.7x, increase health pickups, add more Grunts (less lethal)",
  "action": "{\"difficultyMultiplier\": 0.7, \"roomCount\": 5, \"npcDistribution\": {...}}",
  "evaluation_plan": "Track if player survives 3+ rooms in next run"
}
```

### NPC Brain Output
```json
{
  "state": "FLANK",
  "reasoning": "Player is suppressed behind cover. Moving to flank position at 90° angle.",
  "defection_check": { "should_defect": false, "loyalty_score": 0.8 }
}
```

---

## Tools & APIs

| Tool | Purpose | Usage |
|---|---|---|
| **Gemini 2.0 Flash/Pro** | Primary LLM for all 5 agents | Director, QC, NPC Brain, Reward |
| **Groq (Llama 3.1/3.3)** | Alternative LLM provider (faster, free tier) | Swappable via Inspector toggle |
| **NVIDIA NIM** | Alternative LLM provider (Nemotron 49B for complex spatial reasoning) | Level Generation in AI_Full mode |
| **Unity NavMesh** | Runtime pathfinding for NPC movement | NPC navigation and room traversal |
| **Google Antigravity** | Development orchestrator — code generation, debugging, architecture | Entire development lifecycle |

---

## Antigravity's Role in Development

Google Antigravity served as the **primary development partner** throughout the entire project lifecycle:

1. **Architecture Design** — Designed the 5-agent pipeline, data flow, and state management patterns
2. **Code Generation** — Wrote all agent scripts, NPC state machine, procedural generation, player systems
3. **System Integration** — Connected agents to game systems (FloorGenerator ↔ LevelGenPipeline ↔ GameManager)
4. **Debugging** — Diagnosed complex runtime issues:
   - Identified stale enum serialization causing pickup failures (`pickupType: 3` → `pickupType: 2`)
   - Traced NPC collider overwrites to the FloorGenerator wrapping logic
   - Resolved NPC audio assignment failures during prefab instantiation
5. **Optimization** — Designed the Hybrid generation mode (Solution B) to eliminate LLM timeout issues
6. **Testing** — Created multi-phase test runners for validating generation, NPC behavior, and agent pipelines
7. **Documentation** — Generated architecture diagrams, audit reports, and this submission documentation

---

## Setup & Installation

### Prerequisites
- Unity 2022.3 LTS or later (Unity 6 recommended)
- Android Build Support module
- At least one LLM API key:
  - `GEMINI_API_KEY` (Google Gemini)
  - `GROQ_API_KEY` (Groq / Llama models)
  - `NVIDIA_API_KEY` (NVIDIA NIM)

### Steps
1. Clone the repository:
   ```bash
   git clone https://github.com/your-team/ThresholdGame.git
   ```
2. Open in Unity Hub → Add project from disk
3. Set your API key via **one** of:
   - Environment variable: `set GEMINI_API_KEY=your_key_here`
   - Inspector: Select `GeminiAgentBridge` → paste key in the API Key field
4. Select `LevelGenerationPipeline` → set **Generation Mode** to `Hybrid` (recommended)
5. Select `GeminiAgentBridge` → set **LLM Provider** dropdown (Gemini, Groq, or NVIDIA)
6. Press Play in Editor, or build for Android:
   - File → Build Settings → Android → Build

### Quick Start (No API Key)
The game works without an API key — all agents gracefully fall back to local procedural generation and fixed-rule behavior. The AI enriches the experience but is never a hard dependency.

---

## Assumptions

1. **Free-tier LLM APIs** — All models used are available on free tiers (Gemini Flash: 15 RPM / 1500 RPD, Groq: 30 RPM / 14400 RPD, NVIDIA NIM: 40 RPM)
2. **Single-player** — No multiplayer networking; all AI calls are made from the client device
3. **Mobile-first** — Designed for touchscreen input via virtual joystick; tested on Android
4. **Offline fallback** — If no internet or API key is available, the game uses local procedural generation and fixed-rule NPCs. The experience is functional but not adaptive
5. **Session-based progression** — XP and unlocks persist locally via `Application.persistentDataPath`

---

## Privacy & Safety

- **No personal data collected** — The game does not request, store, or transmit any personally identifiable information
- **LLM inputs are game-state only** — API calls send only gameplay metrics (kill count, room count, NPC health percentages) — never user identifiers, device IDs, or location data
- **API keys are user-provided** — Keys are stored locally on device, never embedded in builds or transmitted to third parties
- **No analytics or telemetry** — All metrics (`SessionMetrics`) are stored locally on-device only
- **Content safety** — All NPC dialogue and reward messages are constrained by structured JSON output format; the LLM cannot generate free-form text displayed to the user

---

## Cost & Latency

### Cost Per Operation

| Agent | Model | Avg Tokens | Cost (Gemini Free) | Cost (Groq Free) |
|---|---|---|---|---|
| Director | Flash | ~300 in + ~200 out | $0.00 | $0.00 |
| Level Gen (Hybrid) | Flash | ~400 in + ~150 out | $0.00 | $0.00 |
| QC Validator | Flash | ~500 in + ~100 out | $0.00 | $0.00 |
| NPC Brain (×8) | Flash | ~200 in + ~100 out | $0.00 | $0.00 |
| Reward | Flash | ~400 in + ~200 out | $0.00 | $0.00 |
| **Total per run** | | ~4,500 tokens | **$0.00** | **$0.00** |

All models operate within free-tier quotas. At paid rates (Gemini Flash: $0.075/1M input, $0.30/1M output), total cost would be approximately **$0.002 per run**.

### Latency

| Agent | Provider | Avg Latency | Timeout |
|---|---|---|---|
| Director | Groq Flash | ~1.2s | 15s |
| Level Gen (Hybrid) | Local + Groq Flash | ~2.5s | 15s |
| Level Gen (AI_Full) | NVIDIA Pro 49B | ~40-90s | 120s |
| QC | Groq Flash | ~1.0s | 15s |
| NPC Brain | Groq Flash | ~0.8s | 3s |
| Reward | Groq Flash | ~1.5s | 15s |
| **Total (Hybrid)** | | **~7s** | |
| **Total (AI_Full)** | | **~45-100s** | |

The **Hybrid** pipeline delivers sub-10-second level generation while maintaining full agentic reasoning for game design decisions.

---

## Scalability

### 10x Scale (10 concurrent players)
- **Groq free tier:** 30 RPM handles ~10 concurrent runs with staggered starts (~12 calls per run, one run every ~30s)
- **NVIDIA free tier:** 40 RPM is sufficient for 10 concurrent Hybrid runs
- **No server needed** — Each client makes its own API calls. Scaling is inherently horizontal
- **Bottleneck:** Rate limits per API key. Solution: Use per-user API keys or upgrade to paid tier

### 100x Scale (100 concurrent players)
- **Paid tier required** — Free-tier RPM limits would throttle at ~30-40 concurrent runs
- **Gemini Flash paid:** 2000 RPM easily handles 100 concurrent players
- **Estimated cost:** ~$0.20/hour for 100 active players (4,500 tokens/run × 2 runs/hour/player)
- **Architecture holds** — Client-side API calls mean no backend server to scale. Each device is independent

### 1000x Scale
- Move NPC Brain calls to client-side inference (TensorFlow Lite / ONNX Runtime) to eliminate ~80% of API calls
- Keep Director and Reward as server-side calls (low frequency, high value)
- Estimated cost: ~$0.50/hour for 1000 players

---

## Baseline Comparison

THRESHOLD includes a built-in **A/B comparison system** (`FixedRuleDirector.cs` + `SessionMetrics.cs`) to prove the agentic pipeline outperforms static rules.

### Baseline (Fixed Rules) — What It Does

| Aspect | Baseline Behavior |
|---|---|
| **Difficulty** | `if deaths > 3: difficulty -= 1; if won: difficulty += 1` |
| **Level Gen** | Random pick from 5 static JSON templates. No novelty checking |
| **NPC Behavior** | Always ATTACK state. No flanking, suppression, or defection |
| **Rewards** | Flat formula: `XP = kills × 10`. No effort evaluation |

### Agentic System — What It Does Better

| Aspect | Agentic Behavior | Advantage |
|---|---|---|
| **Difficulty** | 5+ signal analysis (kills, deaths, accuracy, rooms, time, streak) | Granular, contextual adaptation vs binary threshold |
| **Level Gen** | AI assigns room roles based on difficulty profile + layout history novelty | Varied, strategic layouts vs repetitive templates |
| **NPC Behavior** | 7-state FSM with AI reasoning (PATROL, ATTACK, FLANK, SUPPRESS, RETREAT, DEFECT, DEAD) | Emergent tactical behavior vs mindless aggression |
| **NPC Defection** | NPCs evaluate loyalty and may join the player mid-combat | Creates memorable moments; impossible in baseline |
| **Rewards** | AI evaluates effort, retention risk, fair play; assigns contextual bonuses | Motivating, personalized feedback vs flat numbers |

### Measurable Metrics (SessionMetrics.cs)

Both systems track identical metrics for fair comparison:
- **Win Rate** — % of runs completed
- **Retry Rate** — Deaths per run
- **Average Run Duration** — Engagement time
- **Rooms Per Run** — Depth of exploration
- **Defection Events** — Unique agentic-only mechanic (always 0 in baseline)

The `SessionMetrics.LogComparison()` method produces a formatted comparison table at runtime for immediate evaluation.

---

## Limitations

1. **Free-tier rate limits** — At peak usage (rapid restarts), the NPC Brain can exhaust RPM limits, causing fallback to local state machine logic
2. **LLM latency variance** — Cold starts on NVIDIA NIM (49B model) can take 30-90s. Mitigated by the Hybrid pipeline using Flash models only
3. **AI_Full mode reliability** — Full spatial generation by LLM occasionally produces invalid graphs. The QC retry loop catches these, but wastes API calls
4. **No multiplayer** — The architecture is single-player only. Multiplayer would require server-side agent calls and synchronization
5. **NPC Brain frequency** — Real-time brain calls are throttled to avoid rate limits, meaning NPCs may not re-evaluate for several seconds between decisions
6. **Mobile performance** — HTTP API calls on cellular networks add 200-500ms latency. Offline fallback ensures playability but without adaptation
7. **Progression scope** — The unlock tree is functional but limited (5 levels, basic rewards). A production version would need deeper content
