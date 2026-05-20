// ============================================================================
// ProceduralRoomGenerator.cs — Graph builder with local fallback algorithm
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Threshold.Core;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Threshold.Generation
{
    /// <summary>
    /// Builds a RoomGraphConfig either from Gemini output or via a local
    /// fallback algorithm. Includes full validation suite.
    /// </summary>
    public static class ProceduralRoomGenerator
    {
        // ====================================================================
        // Fallback Generation (no Gemini required)
        // ====================================================================

        /// <summary>
        /// Generates a complete, validated RoomGraphConfig using a local algorithm.
        /// Always produces playable layouts — this is the safety net.
        /// </summary>
        public static RoomGraphConfig GenerateFallback(DifficultyProfile difficulty, int seed = -1)
        {
            if (seed < 0) seed = Environment.TickCount;
            Random.InitState(seed);

            int roomCount = Mathf.Clamp(difficulty.targetRoomCount, 5, 12);
            // Tighter grid reduces L-route corridor inflation (was 2.5f)
            int gridSize = Mathf.CeilToInt(Mathf.Sqrt(roomCount * 1.5f));
            gridSize = Mathf.Max(gridSize, 4);
            // Cap total rooms (anchors + corridors) to prevent routing bloat
            int maxTotalRooms = Mathf.CeilToInt(roomCount * 1.5f);

            var config = new RoomGraphConfig
            {
                difficulty = difficulty,
                rooms = new List<RoomConfig>(),
                edges = new List<EdgeConfig>(),
                metadata = new LayoutMetadata
                {
                    seed = seed,
                    generationMethod = "local_fallback",
                    timestamp = DateTime.UtcNow.ToString("o"),
                    gridWidth = gridSize,
                    gridHeight = gridSize,
                    qcAttempts = 0
                }
            };

            // Grid tracking: which cells are occupied
            var occupied = new Dictionary<Vector2Int, string>();

            // Step 1: Place ENTRY on bottom border, EXIT on top border
            Vector2Int entryPos = new(Random.Range(0, gridSize), 0);
            Vector2Int exitPos = new(Random.Range(0, gridSize), gridSize - 1);

            // C1 FIX: Ensure entry and exit are never on the same cell
            int exitRetries = 0;
            while (exitPos == entryPos && exitRetries < 20)
            {
                exitPos = new(Random.Range(0, gridSize), gridSize - 1);
                exitRetries++;
            }
            // If gridSize == 1 and they're stuck on the same cell, force separation
            if (exitPos == entryPos)
            {
                exitPos = new(Mathf.Min(entryPos.x + 1, gridSize - 1), gridSize - 1);
                if (exitPos == entryPos) exitPos = new(entryPos.x, Mathf.Min(1, gridSize - 1));
            }

            string entryId = AddRoom(config, occupied, entryPos, RoomRole.ENTRY);
            string exitId = AddRoom(config, occupied, exitPos, RoomRole.EXIT);

            // Step 2: Generate anchor points between entry and exit
            var anchors = new List<Vector2Int> { entryPos };
            int interiorCount = roomCount - 2;
            int attempts = 0;

            while (interiorCount > 0 && attempts < 200)
            {
                attempts++;
                Vector2Int candidate = new(Random.Range(0, gridSize), Random.Range(0, gridSize));
                if (!occupied.ContainsKey(candidate))
                {
                    string id = AddRoom(config, occupied, candidate, RoomRole.COMBAT);
                    anchors.Add(candidate);
                    interiorCount--;
                }
            }

            // C2 FIX: Warn if grid was too full to place all requested rooms
            if (interiorCount > 0)
            {
                Debug.LogWarning($"[ProceduralRoomGenerator] Could only place {roomCount - 2 - interiorCount}" +
                                 $" of {roomCount - 2} interior rooms. Grid may be too small.");
            }
            anchors.Add(exitPos);

            // Step 3: Connect rooms via L-shaped corridor routing
            // Sort anchors roughly from entry to exit
            anchors.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

            for (int i = 0; i < anchors.Count - 1; i++)
            {
                // C3 FIX: Skip connection when source and target are the same cell
                if (anchors[i] == anchors[i + 1]) continue;
                ConnectRoomsLShaped(config, occupied, anchors[i], anchors[i + 1], gridSize, maxTotalRooms);
            }

            int corridorCount = config.rooms.Count - roomCount;
            if (corridorCount > 0)
                Debug.Log($"[ProceduralRoomGenerator] Corridors: +{corridorCount} rooms (total: {config.rooms.Count}, target: {roomCount}, cap: {maxTotalRooms})");

            // Step 4: Determine shapes from actual doorway connections
            AssignShapesFromConnections(config);

            // Step 5: Assign gameplay roles
            AssignRoles(config, difficulty);

            // Step 6: Populate spawn zones based on difficulty
            PopulateSpawnZones(config, difficulty);

            // Step 7: Add items to PACING and LOOT rooms
            PopulateItems(config);

            // Validate and fix if needed
            EnsureConnectivity(config, occupied, gridSize);

            return config;
        }

        // ====================================================================
        // Grid Helpers
        // ====================================================================

        private static string AddRoom(RoomGraphConfig config, Dictionary<Vector2Int, string> occupied,
                                       Vector2Int pos, RoomRole role)
        {
            string id = $"room_{config.rooms.Count}";
            config.rooms.Add(new RoomConfig
            {
                roomId = id,
                gridCol = pos.x,
                gridRow = pos.y,
                role = role,
                doorways = new List<DoorwayConfig>(),
                spawnZones = new List<SpawnZoneConfig>(),
                items = new List<ItemConfig>(),
                events = new List<EventConfig>()
            });
            occupied[pos] = id;
            return id;
        }

        private static void ConnectRoomsLShaped(RoomGraphConfig config,
            Dictionary<Vector2Int, string> occupied, Vector2Int from, Vector2Int to,
            int gridSize, int maxTotalRooms)
        {
            // L-shaped routing: move horizontally first, then vertically
            Vector2Int current = from;

            // Horizontal leg
            int dirX = to.x > from.x ? 1 : -1;
            while (current.x != to.x)
            {
                Vector2Int next = new(current.x + dirX, current.y);
                EnsureRoomAndEdge(config, occupied, current, next, gridSize, maxTotalRooms);
                if (!occupied.ContainsKey(next)) break; // corridor cap hit
                current = next;
            }

            // Vertical leg
            int dirY = to.y > from.y ? 1 : -1;
            while (current.y != to.y)
            {
                Vector2Int next = new(current.x, current.y + dirY);
                EnsureRoomAndEdge(config, occupied, current, next, gridSize, maxTotalRooms);
                if (!occupied.ContainsKey(next)) break; // corridor cap hit
                current = next;
            }
        }

        private static void EnsureRoomAndEdge(RoomGraphConfig config,
            Dictionary<Vector2Int, string> occupied, Vector2Int from, Vector2Int to,
            int gridSize, int maxTotalRooms)
        {
            if (to.x < 0 || to.x >= gridSize || to.y < 0 || to.y >= gridSize) return;

            // Ensure target room exists — respect corridor cap
            if (!occupied.ContainsKey(to))
            {
                if (config.rooms.Count >= maxTotalRooms)
                    return; // corridor cap reached, stop inflating
                AddRoom(config, occupied, to, RoomRole.COMBAT);
            }

            // Safety: source must exist to create edge
            if (!occupied.ContainsKey(from)) return;

            string fromId = occupied[from];
            string toId = occupied[to];

            // Determine direction
            Direction dir;
            if (to.y > from.y) dir = Direction.NORTH;
            else if (to.y < from.y) dir = Direction.SOUTH;
            else if (to.x > from.x) dir = Direction.EAST;
            else dir = Direction.WEST;

            // Check if edge already exists
            bool exists = config.edges.Exists(e =>
                (e.roomIdA == fromId && e.roomIdB == toId) ||
                (e.roomIdA == toId && e.roomIdB == fromId));

            if (!exists)
            {
                config.edges.Add(new EdgeConfig
                {
                    roomIdA = fromId,
                    roomIdB = toId,
                    directionFromA = dir
                });

                // Add doorway configs to both rooms
                var fromRoom = config.GetRoom(fromId);
                var toRoom = config.GetRoom(toId);
                Direction oppositeDir = (Direction)(((int)dir + 2) % 4);

                if (!fromRoom.HasDoorway(dir))
                {
                    fromRoom.doorways.Add(new DoorwayConfig
                    {
                        direction = dir, isOpen = true, connectedRoomId = toId
                    });
                }

                if (!toRoom.HasDoorway(oppositeDir))
                {
                    toRoom.doorways.Add(new DoorwayConfig
                    {
                        direction = oppositeDir, isOpen = true, connectedRoomId = fromId
                    });
                }
            }
        }

        // ====================================================================
        // Shape Assignment
        // ====================================================================

        private static void AssignShapesFromConnections(RoomGraphConfig config)
        {
            // C4 FIX: Track and remove rooms with 0 doorways (orphans)
            var orphans = new List<RoomConfig>();

            foreach (var room in config.rooms)
            {
                int doorCount = room.doorways.Count;
                bool n = room.HasDoorway(Direction.NORTH);
                bool e = room.HasDoorway(Direction.EAST);
                bool s = room.HasDoorway(Direction.SOUTH);
                bool w = room.HasDoorway(Direction.WEST);

                if (doorCount == 0)
                {
                    // Room has no connections — mark for removal
                    if (room.role != RoomRole.ENTRY && room.role != RoomRole.EXIT)
                    {
                        orphans.Add(room);
                        Debug.LogWarning($"[ProceduralRoomGenerator] Room {room.roomId} has 0 doorways. Removing orphan.");
                    }
                    else
                    {
                        // ENTRY/EXIT with 0 doors is a critical error
                        room.shape = RoomShape.DEAD_END;
                        Debug.LogError($"[ProceduralRoomGenerator] {room.role} room {room.roomId} has 0 doorways!");
                    }
                    continue;
                }

                room.shape = doorCount switch
                {
                    4 => RoomShape.CROSSROADS,
                    3 => RoomShape.T_JUNCTION,
                    2 => AreOpposite(n, e, s, w) ? RoomShape.STRAIGHT : RoomShape.CORNER,
                    1 => RoomShape.DEAD_END,
                    _ => RoomShape.DEAD_END
                };
            }

            // Remove orphan rooms and their edges
            foreach (var orphan in orphans)
            {
                config.rooms.Remove(orphan);
                config.edges.RemoveAll(e => e.roomIdA == orphan.roomId || e.roomIdB == orphan.roomId);
            }
        }

        private static bool AreOpposite(bool n, bool e, bool s, bool w)
        {
            return (n && s && !e && !w) || (e && w && !n && !s);
        }

        // ====================================================================
        // Role Assignment
        // ====================================================================

        private static void AssignRoles(RoomGraphConfig config, DifficultyProfile difficulty)
        {
            // ENTRY and EXIT are already set. Assign roles to the rest.
            var interiorRooms = config.rooms.Where(r =>
                r.role != RoomRole.ENTRY && r.role != RoomRole.EXIT).ToList();

            if (interiorRooms.Count == 0) return;

            // Find room closest to EXIT for BOSS
            var exitRoom = config.GetExitRoom();
            if (exitRoom != null)
            {
                var connected = config.GetConnectedRoomIds(exitRoom.roomId);
                foreach (var cid in connected)
                {
                    var candidate = interiorRooms.Find(r => r.roomId == cid);
                    if (candidate != null && candidate.shape == RoomShape.CROSSROADS)
                    {
                        candidate.role = RoomRole.BOSS;
                        interiorRooms.Remove(candidate);
                        break;
                    }
                }

                // M2 FIX: If no CROSSROADS, prefer rooms with ≥2 doorways for BOSS.
                // DEAD_END should never be BOSS (only 1 exit = death trap).
                if (!config.rooms.Exists(r => r.role == RoomRole.BOSS) && interiorRooms.Count > 0)
                {
                    // Priority: T_JUNCTION > CORNER/STRAIGHT > DEAD_END (last resort)
                    var boss = interiorRooms.Find(r => r.shape == RoomShape.T_JUNCTION)
                           ?? interiorRooms.Find(r => r.shape == RoomShape.STRAIGHT || r.shape == RoomShape.CORNER)
                           ?? interiorRooms[interiorRooms.Count - 1];
                    boss.role = RoomRole.BOSS;
                    interiorRooms.Remove(boss);

                    if (boss.shape == RoomShape.DEAD_END)
                        Debug.LogWarning($"[ProceduralRoomGenerator] BOSS assigned to DEAD_END {boss.roomId} — no better room available.");
                }
            }

            // Assign DEAD_ENDs as LOOT
            foreach (var room in interiorRooms.ToList())
            {
                if (room.shape == RoomShape.DEAD_END && room.role == RoomRole.COMBAT)
                {
                    room.role = RoomRole.LOOT;
                }
            }

            // Place PACING rooms every 2–3 combat rooms, capped to prevent over-assignment
            int combatStreak = 0;
            int pacingInterval = difficulty.difficultyMultiplier > 1.5f ? 3 : 2;
            int maxPacing = Mathf.Max(1, interiorRooms.Count / 3);
            int pacingCount = 0;
            foreach (var room in interiorRooms)
            {
                if (room.role != RoomRole.COMBAT) continue;
                combatStreak++;
                if (combatStreak >= pacingInterval && pacingCount < maxPacing)
                {
                    room.role = RoomRole.PACING;
                    combatStreak = 0;
                    pacingCount++;
                }
            }

            // Assign one AMBUSH if difficulty is high enough
            if (difficulty.difficultyMultiplier >= 1.2f)
            {
                var combatRooms = interiorRooms.Where(r => r.role == RoomRole.COMBAT).ToList();
                if (combatRooms.Count > 0)
                {
                    combatRooms[Random.Range(0, combatRooms.Count)].role = RoomRole.AMBUSH;
                }
            }

            // Assign CHOKE to CORNER rooms in combat
            foreach (var room in interiorRooms)
            {
                if (room.role == RoomRole.COMBAT && room.shape == RoomShape.CORNER)
                {
                    room.role = RoomRole.CHOKE;
                    break; // Only one choke per floor
                }
            }
        }

        // ====================================================================
        // Spawn Zone Population
        // ====================================================================

        public static void PopulateSpawnZones(RoomGraphConfig config, DifficultyProfile difficulty)
        {
            int elitesRemaining = difficulty.eliteCount;

            foreach (var room in config.rooms)
            {
                if (room.role == RoomRole.ENTRY || room.role == RoomRole.EXIT ||
                    room.role == RoomRole.PACING || room.role == RoomRole.LOOT)
                    continue;

                // Skip rooms already populated (idempotency guard)
                if (room.spawnZones != null && room.spawnZones.Count > 0)
                    continue;

                int baseCount = difficulty.baseEnemiesPerRoom;
                float mult = difficulty.difficultyMultiplier;

                int enemyCount = room.role switch
                {
                    RoomRole.BOSS => Mathf.CeilToInt(baseCount * mult * 1.5f),
                    RoomRole.AMBUSH => Mathf.CeilToInt(baseCount * mult * 1.2f),
                    RoomRole.CHOKE => Mathf.Max(2, Mathf.CeilToInt(baseCount * mult * 0.8f)),
                    _ => Mathf.CeilToInt(baseCount * mult)
                };

                // Place ELITE in BOSS room first
                if (room.role == RoomRole.BOSS && elitesRemaining > 0)
                {
                    room.spawnZones.Add(new SpawnZoneConfig
                    {
                        localPosition = new Vector3(0, 0, 2f),
                        archetype = NPCArchetype.ELITE,
                        count = 1,
                        spawnDelay = 0
                    });
                    elitesRemaining--;
                    enemyCount--;
                    // C5 FIX: Clamp to prevent negative enemy count
                    enemyCount = Mathf.Max(0, enemyCount);
                }

                // Fill remaining with mixed archetypes
                if (enemyCount > 0)
                {
                    // Distribute across 2–3 spawn positions
                    int zones = Mathf.Min(enemyCount, 3);
                    int perZone = enemyCount / zones;
                    int remainder = enemyCount % zones;

                    for (int i = 0; i < zones; i++)
                    {
                        float angle = (360f / zones) * i * Mathf.Deg2Rad;
                        Vector3 pos = new(Mathf.Cos(angle) * 4f, 0, Mathf.Sin(angle) * 4f);

                        NPCArchetype archetype = PickArchetype(difficulty, room.role);

                        room.spawnZones.Add(new SpawnZoneConfig
                        {
                            localPosition = pos,
                            archetype = archetype,
                            count = perZone + (i < remainder ? 1 : 0),
                            spawnDelay = room.role == RoomRole.AMBUSH ? 1.5f : 0f
                        });
                    }
                }
            }
        }

        private static NPCArchetype PickArchetype(DifficultyProfile difficulty, RoomRole role)
        {
            float roll = Random.value;
            float mult = difficulty.difficultyMultiplier;

            if (role == RoomRole.CHOKE) return NPCArchetype.SUPPRESSOR;

            if (mult > 1.5f && roll < 0.25f) return NPCArchetype.FLANKER;
            if (mult > 1.2f && roll < 0.15f) return NPCArchetype.SUPPRESSOR;

            return NPCArchetype.GRUNT;
        }

        // ====================================================================
        // Item Population
        // ====================================================================

        private static void PopulateItems(RoomGraphConfig config)
        {
            foreach (var room in config.rooms)
            {
                switch (room.role)
                {
                    case RoomRole.PACING:
                        room.items.Add(new ItemConfig
                        {
                            itemType = ItemType.HEALTH_KIT,
                            localPosition = new Vector3(-2f, 0, 0)
                        });
                        room.items.Add(new ItemConfig
                        {
                            itemType = ItemType.AMMO_CACHE,
                            localPosition = new Vector3(2f, 0, 0)
                        });
                        break;

                    case RoomRole.LOOT:
                        room.items.Add(new ItemConfig
                        {
                            itemType = Random.value > 0.5f ? ItemType.WEAPON_PICKUP : ItemType.SHIELD_BOOST,
                            localPosition = Vector3.zero
                        });
                        // 30% chance of trap in loot room
                        if (Random.value < 0.3f)
                        {
                            room.items.Add(new ItemConfig
                            {
                                itemType = ItemType.TRAP,
                                localPosition = new Vector3(0, 0, -1f)
                            });
                        }
                        break;

                    case RoomRole.BOSS:
                        // Reward after boss
                        room.events.Add(new EventConfig
                        {
                            triggerType = EventTriggerType.ON_CLEAR,
                            description = "Boss defeated — drop reward cache",
                            parameter = 0
                        });
                        break;

                    // L6 FIX: AMBUSH and COMBAT rooms get ON_ENTER trigger events
                    case RoomRole.AMBUSH:
                        room.events.Add(new EventConfig
                        {
                            triggerType = EventTriggerType.ON_ENTER,
                            description = "Ambush triggered — delayed spawn wave",
                            parameter = 1  // 1 = delayed spawn flag
                        });
                        break;

                    case RoomRole.COMBAT:
                        room.events.Add(new EventConfig
                        {
                            triggerType = EventTriggerType.ON_ENTER,
                            description = "Combat engagement — spawn enemies",
                            parameter = 0
                        });
                        break;
                }
            }
        }

        // ====================================================================
        // Doorway Auto-Repair (compensates for LLM spatial reasoning gaps)
        // ====================================================================

        /// <summary>
        /// Automatically repairs doorway consistency issues in AI-generated configs.
        /// For each edge, ensures both rooms have the required doorway entries.
        /// Also upgrades room shapes if they don't have enough doorways.
        /// Returns the number of repairs made.
        /// </summary>
        public static int RepairDoorways(RoomGraphConfig config)
        {
            if (config == null || config.edges == null || config.rooms == null) return 0;

            int repairs = 0;

            foreach (var edge in config.edges)
            {
                var roomA = config.GetRoom(edge.roomIdA);
                var roomB = config.GetRoom(edge.roomIdB);
                if (roomA == null || roomB == null) continue;

                Direction dirFromA = edge.directionFromA;
                Direction dirFromB = (Direction)(((int)dirFromA + 2) % 4);

                // Repair room A's doorway
                if (!roomA.HasDoorway(dirFromA))
                {
                    if (roomA.doorways == null)
                        roomA.doorways = new List<DoorwayConfig>();

                    // Check if doorway exists but is closed
                    var existing = roomA.GetDoorway(dirFromA);
                    if (existing != null)
                    {
                        existing.isOpen = true;
                        existing.connectedRoomId = edge.roomIdB;
                    }
                    else
                    {
                        roomA.doorways.Add(new DoorwayConfig
                        {
                            direction = dirFromA,
                            isOpen = true,
                            connectedRoomId = edge.roomIdB
                        });
                    }
                    repairs++;
                }

                // Repair room B's doorway
                if (!roomB.HasDoorway(dirFromB))
                {
                    if (roomB.doorways == null)
                        roomB.doorways = new List<DoorwayConfig>();

                    var existing = roomB.GetDoorway(dirFromB);
                    if (existing != null)
                    {
                        existing.isOpen = true;
                        existing.connectedRoomId = edge.roomIdA;
                    }
                    else
                    {
                        roomB.doorways.Add(new DoorwayConfig
                        {
                            direction = dirFromB,
                            isOpen = true,
                            connectedRoomId = edge.roomIdA
                        });
                    }
                    repairs++;
                }
            }

            // Upgrade room shapes if they now have more doorways than their shape allows
            if (repairs > 0)
            {
                foreach (var room in config.rooms)
                {
                    int openCount = 0;
                    if (room.doorways != null)
                    {
                        foreach (var d in room.doorways)
                            if (d.isOpen) openCount++;
                    }

                    // Upgrade shape to match actual doorway count
                    if (openCount >= 4 && room.shape != RoomShape.CROSSROADS)
                        room.shape = RoomShape.CROSSROADS;
                    else if (openCount == 3 && room.shape != RoomShape.T_JUNCTION && room.shape != RoomShape.CROSSROADS)
                        room.shape = RoomShape.T_JUNCTION;
                    else if (openCount == 2 && room.shape == RoomShape.DEAD_END)
                        room.shape = RoomShape.CORNER; // Could be STRAIGHT too, but CORNER is safer
                }

                Debug.Log($"[ProceduralRoomGenerator] Auto-repaired {repairs} doorway(s) in AI-generated layout.");
            }

            // Repair spawn safety: ENTRY and EXIT rooms must have no enemies
            foreach (var room in config.rooms)
            {
                if ((room.role == RoomRole.ENTRY || room.role == RoomRole.EXIT) &&
                    room.spawnZones != null && room.spawnZones.Count > 0)
                {
                    int cleared = room.TotalEnemyCount();
                    room.spawnZones.Clear();
                    repairs++;
                    Debug.Log($"[ProceduralRoomGenerator] Cleared {cleared} enemy spawn(s) from {room.role} room {room.roomId}.");
                }
            }

            return repairs;
        }

        // ====================================================================
        // Validation
        // ====================================================================

        /// <summary>
        /// Validates a RoomGraphConfig for playability. Returns a list of
        /// issues found. Empty list = valid layout.
        /// </summary>
        public static List<string> Validate(RoomGraphConfig config)
        {
            var issues = new List<string>();
            if (config == null || config.rooms == null || config.rooms.Count == 0)
            {
                issues.Add("Config is null or has no rooms.");
                return issues;
            }

            ValidateConnectivity(config, issues);
            ValidateSpawnSafety(config, issues);
            ValidateDoorwayConsistency(config, issues);
            ValidateRoleConstraints(config, issues);
            ValidateUniqueRoles(config, issues);

            return issues;
        }

        /// <summary>BFS connectivity check — all rooms must be reachable from ENTRY.</summary>
        private static void ValidateConnectivity(RoomGraphConfig config, List<string> issues)
        {
            var entry = config.GetEntryRoom();
            if (entry == null)
            {
                issues.Add("No ENTRY room found.");
                return;
            }

            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(entry.roomId);
            visited.Add(entry.roomId);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                foreach (string neighbor in config.GetConnectedRoomIds(current))
                {
                    if (visited.Add(neighbor))
                        queue.Enqueue(neighbor);
                }
            }

            foreach (var room in config.rooms)
            {
                if (!visited.Contains(room.roomId))
                    issues.Add($"Room {room.roomId} is unreachable from ENTRY.");
            }
        }

        /// <summary>No enemies should spawn in the ENTRY room.</summary>
        private static void ValidateSpawnSafety(RoomGraphConfig config, List<string> issues)
        {
            var entry = config.GetEntryRoom();
            if (entry != null && entry.TotalEnemyCount() > 0)
                issues.Add("ENTRY room has enemy spawns — must be safe.");

            var exit = config.GetExitRoom();
            if (exit != null && exit.TotalEnemyCount() > 0)
                issues.Add("EXIT room has enemy spawns — should be safe.");
        }

        /// <summary>Connected rooms must have matching doorways facing each other.</summary>
        private static void ValidateDoorwayConsistency(RoomGraphConfig config, List<string> issues)
        {
            foreach (var edge in config.edges)
            {
                var roomA = config.GetRoom(edge.roomIdA);
                var roomB = config.GetRoom(edge.roomIdB);
                if (roomA == null || roomB == null) continue;

                Direction dirFromA = edge.directionFromA;
                Direction dirFromB = (Direction)(((int)dirFromA + 2) % 4);

                if (!roomA.HasDoorway(dirFromA))
                    issues.Add($"Room {edge.roomIdA} missing {dirFromA} doorway to {edge.roomIdB}.");
                if (!roomB.HasDoorway(dirFromB))
                    issues.Add($"Room {edge.roomIdB} missing {dirFromB} doorway to {edge.roomIdA}.");
            }
        }

        /// <summary>Enforces role-to-shape constraints from GDD.</summary>
        private static void ValidateRoleConstraints(RoomGraphConfig config, List<string> issues)
        {
            foreach (var room in config.rooms)
            {
                // BOSS should prefer CROSSROADS (warning, not error)
                if (room.role == RoomRole.BOSS && room.shape != RoomShape.CROSSROADS)
                    issues.Add($"Warning: BOSS room {room.roomId} is {room.shape}, CROSSROADS preferred.");
            }
        }

        /// <summary>Exactly one ENTRY and one EXIT per floor.</summary>
        private static void ValidateUniqueRoles(RoomGraphConfig config, List<string> issues)
        {
            int entries = config.rooms.Count(r => r.role == RoomRole.ENTRY);
            int exits = config.rooms.Count(r => r.role == RoomRole.EXIT);

            if (entries != 1) issues.Add($"Expected exactly 1 ENTRY, found {entries}.");
            if (exits != 1) issues.Add($"Expected exactly 1 EXIT, found {exits}.");
        }

        /// <summary>Emergency fix: ensure graph is connected by adding corridor rooms.</summary>
        private static void EnsureConnectivity(RoomGraphConfig config,
            Dictionary<Vector2Int, string> occupied, int gridSize)
        {
            var entry = config.GetEntryRoom();
            if (entry == null) return;

            // M6 FIX: Cap total rooms to prevent runaway corridor generation
            int maxTotalRooms = gridSize * gridSize;

            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(entry.roomId);
            visited.Add(entry.roomId);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                foreach (string neighbor in config.GetConnectedRoomIds(current))
                {
                    if (visited.Add(neighbor))
                        queue.Enqueue(neighbor);
                }
            }

            // Connect any orphaned rooms to the nearest visited room
            foreach (var room in config.rooms.ToList())
            {
                if (visited.Contains(room.roomId)) continue;
                if (config.rooms.Count >= maxTotalRooms)
                {
                    Debug.LogWarning($"[ProceduralRoomGenerator] Room cap ({maxTotalRooms}) reached. " +
                                     $"Removing orphan {room.roomId} instead of connecting.");
                    config.rooms.Remove(room);
                    config.edges.RemoveAll(e => e.roomIdA == room.roomId || e.roomIdB == room.roomId);
                    continue;
                }

                Vector2Int orphanPos = new(room.gridCol, room.gridRow);
                Vector2Int nearest = FindNearestVisitedCell(orphanPos, visited, config);

                if (nearest.x >= 0)
                {
                    ConnectRoomsLShaped(config, occupied, nearest, orphanPos, gridSize, int.MaxValue);
                    AssignShapesFromConnections(config);
                    visited.Add(room.roomId);
                }
            }
        }

        private static Vector2Int FindNearestVisitedCell(Vector2Int from,
            HashSet<string> visited, RoomGraphConfig config)
        {
            float bestDist = float.MaxValue;
            Vector2Int best = new(-1, -1);

            foreach (var room in config.rooms)
            {
                if (!visited.Contains(room.roomId)) continue;
                Vector2Int pos = new(room.gridCol, room.gridRow);
                float dist = Vector2Int.Distance(from, pos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = pos;
                }
            }
            return best;
        }
    }
}
