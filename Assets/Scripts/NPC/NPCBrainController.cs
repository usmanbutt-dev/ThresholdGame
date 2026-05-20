// ============================================================================
// NPCBrainController.cs — Gemini-driven NPC tactical evaluation loop
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Evaluates ALL living NPCs in the current room every 20 seconds using
// the Gemini NPC Brain Agent. This is the agentic decision-maker that
// the hackathon requires — NPCs don't use hardcoded behaviour trees.
//
// Handles defection mechanics: max 1 per room, 2 per run, ELITE resist.
// On API failure: NPCs continue in current state (safe degradation).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Threshold.Agents;
using Threshold.Core;
using Threshold.Player;
using UnityEngine;

namespace Threshold.NPC
{
    /// <summary>
    /// Room-level controller that drives NPC tactical decisions via Gemini.
    /// Attach to a persistent manager GameObject. Call OnRoomEnter/OnRoomExit
    /// when the player transitions between rooms.
    /// </summary>
    public class NPCBrainController : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("Evaluation")]
        [Tooltip("Seconds between Brain Agent evaluations.")]
        [SerializeField] private float evaluationInterval = 30f;

        [Tooltip("Seconds to wait after room entry before first evaluation.")]
        [SerializeField] private float firstEvalDelay = 3f;

        [Header("Defection Limits")]
        [Tooltip("Max defections allowed in a single room.")]
        [SerializeField] private int maxDefectionsPerRoom = 1;

        [Tooltip("Max defections allowed in a single run.")]
        [SerializeField] private int maxDefectionsPerRun = 2;

        [Header("Player Reference")]
        [SerializeField] private Transform playerTransform;

        [Header("Debug")]
        [SerializeField] private bool logEvaluations = true;

        // ====================================================================
        // Runtime State
        // ====================================================================

        /// <summary>All living NPCs in the current room.</summary>
        public List<NPCStateMachine> ActiveNPCs { get; private set; } = new();

        /// <summary>NPCs that have defected this room.</summary>
        public List<NPCStateMachine> DefectedNPCs { get; private set; } = new();

        /// <summary>True while a Brain Agent call is in-flight.</summary>
        public bool IsEvaluating { get; private set; }

        /// <summary>Number of evaluations completed this room.</summary>
        public int EvaluationCount { get; private set; }

        /// <summary>Last evaluation result for debug display.</summary>
        public BrainEvalResult LastResult { get; private set; }

        // Internal
        private float _evalTimer;
        private bool _roomActive;
        private int _defectionsThisRun;
        private int _defectionsThisRoom;
        private string _currentRoomId;

        // Player combat data for the brain agent
        private float _playerHealthPercent = 1f;
        private float _playerAccuracyThisRoom;
        private int _playerKillStreakThisRoom;
        private string _playerWeapon = "rifle";
        private string _playerMovementPattern = "mobile";
        private string _playerDominantTactic = "aggressive";
        private float _playerCoverUsage;

        // ====================================================================
        // System Prompt
        // ====================================================================

        private static readonly string SystemPrompt = @"
You are the NPC BRAIN AGENT for THRESHOLD, a top-down corridor shooter.
Every 20s, decide tactical state changes for all living enemy NPCs. Think like a squad commander.

6 STATES: PATROL(default,no combat), ATTACK(direct fire), FLANK(90° repositioning),
SUPPRESS(rapid fire from cover,pin player), RETREAT(fall back,<20%HP), ALLIED(defection,permanent).

TACTICS: Cover-camper→FLANK+SUPPRESS. Rusher→multi-angle ATTACK. Reloading/low-HP player→SUPPRESS+FLANK.
Dominant player(high acc,streak)→RETREAT wounded. Squad 3+→keep 1 ATTACK anchor. Minimize state changes.

DEFECTION(ALLIED): RARE. ALL required: HP<15%(GRUNT/FLANKER) or <10%(others), player accuracy>60%,
kill_streak≥2, no exploit, NOT ELITE(unless last alive), strategic value. Only if defection_allowed=true.

OUTPUT action field as JSON:
{""state_changes"":[{""npc_id"":""id"",""new_state"":""STATE"",""reason"":""brief""}],
""squad_tactic"":""name"",""tactic_reasoning"":""brief""}
Only include NPCs that CHANGE state. Empty array if no changes needed.
Valid: PATROL,ATTACK,FLANK,SUPPRESS,RETREAT,ALLIED
".Trim();

        // ====================================================================
        // Public API — Called by game systems
        // ====================================================================

        /// <summary>
        /// Called when the player enters a new room. Registers all NPCs
        /// in the room and starts the evaluation timer.
        /// </summary>
        public void OnRoomEnter(string roomId, List<NPCStateMachine> npcsInRoom, Transform player)
        {
            _currentRoomId = roomId;
            playerTransform = player;
            _defectionsThisRoom = 0;
            EvaluationCount = 0;
            _playerKillStreakThisRoom = 0;

            ActiveNPCs.Clear();
            DefectedNPCs.Clear();

            if (npcsInRoom != null)
            {
                foreach (var npc in npcsInRoom)
                {
                    if (npc != null && !npc.IsDead)
                    {
                        ActiveNPCs.Add(npc);
                        npc.SetState(NPCState.PATROL); // Reset to patrol on room entry
                    }
                }
            }

            _evalTimer = firstEvalDelay; // Delay before first evaluation
            _roomActive = true;

            if (logEvaluations)
                Debug.Log($"[NPCBrain] Room {roomId}: {ActiveNPCs.Count} NPCs registered. " +
                          $"First eval in {firstEvalDelay}s.");
        }

        /// <summary>
        /// Called when the player exits the room or the room is cleared.
        /// Stops the evaluation loop.
        /// </summary>
        public void OnRoomExit()
        {
            _roomActive = false;
            _currentRoomId = null;

            if (logEvaluations)
                Debug.Log("[NPCBrain] Room exited. Evaluation paused.");
        }

        /// <summary>
        /// Called when an NPC dies. Removes from active list.
        /// </summary>
        public void OnNPCDeath(NPCStateMachine npc)
        {
            ActiveNPCs.Remove(npc);

            if (logEvaluations)
                Debug.Log($"[NPCBrain] NPC {npc.npcId} died. {ActiveNPCs.Count} remaining.");

            // If all dead, stop evaluating
            if (ActiveNPCs.Count == 0 || ActiveNPCs.All(n => n.IsDead || n.IsAllied))
            {
                _roomActive = false;
                if (logEvaluations) Debug.Log("[NPCBrain] All enemies eliminated. Evaluation stopped.");
            }
        }

        /// <summary>
        /// Called when a new run begins. Resets run-scoped defection counter.
        /// </summary>
        public void OnRunStart()
        {
            _defectionsThisRun = 0;
        }

        /// <summary>
        /// Update player combat state for the next evaluation cycle.
        /// Called by PlayerMetricsTracker or game UI.
        /// </summary>
        public void UpdatePlayerState(float healthPercent, float accuracyThisRoom,
            int killStreakThisRoom, string weapon = null, string movementPattern = null,
            string dominantTactic = null, float coverUsage = 0f)
        {
            _playerHealthPercent = healthPercent;
            _playerAccuracyThisRoom = accuracyThisRoom;
            _playerKillStreakThisRoom = killStreakThisRoom;
            if (weapon != null) _playerWeapon = weapon;
            if (movementPattern != null) _playerMovementPattern = movementPattern;
            if (dominantTactic != null) _playerDominantTactic = dominantTactic;
            _playerCoverUsage = coverUsage;
        }

        /// <summary>
        /// Force an immediate evaluation. Useful for testing and debugging.
        /// Resets the evaluation timer so the next scheduled eval happens
        /// a full interval later.
        /// </summary>
        public void ForceEvaluation()
        {
            if (IsEvaluating)
            {
                Debug.Log("[NPCBrain] Evaluation already in progress, cannot force.");
                return;
            }
            if (!_roomActive)
            {
                Debug.Log("[NPCBrain] No active room, cannot force evaluation.");
                return;
            }
            _evalTimer = evaluationInterval;
            _ = RunEvaluation();
        }

        // ====================================================================
        // Update Loop
        // ====================================================================

        private void Update()
        {
            if (!_roomActive || IsEvaluating) return;

            // Count down to next evaluation
            _evalTimer -= Time.deltaTime;
            if (_evalTimer <= 0f)
            {
                _evalTimer = evaluationInterval;
                _ = RunEvaluation();
            }
        }

        // ====================================================================
        // Core Evaluation
        // ====================================================================

        private async Task RunEvaluation()
        {
            // C5 FIX: Set flag synchronously BEFORE any await to prevent race
            if (IsEvaluating) return;
            IsEvaluating = true;

            if (ActiveNPCs.Count == 0)
            {
                IsEvaluating = false;
                return;
            }

            // Remove dead NPCs before evaluation
            ActiveNPCs.RemoveAll(n => n == null || n.IsDead);
            var hostileNpcs = ActiveNPCs.Where(n => !n.IsAllied).ToList();

            if (hostileNpcs.Count == 0)
            {
                _roomActive = false;
                return;
            }

            try
            {
                EvaluationCount++;
                string gameState = BuildGameStateJson(hostileNpcs);

                var request = new AgentRequest(
                    agentName: "npc_brain",
                    systemPrompt: SystemPrompt,
                    gameStateJson: gameState,
                    model: GeminiModel.Flash,
                    timeoutSeconds: 3 // Tight timeout for combat responsiveness
                );

                if (logEvaluations)
                    Debug.Log($"[NPCBrain] Eval #{EvaluationCount}: {hostileNpcs.Count} hostile NPCs...");

                var response = await GeminiAgentBridge.Instance.SendAgentRequest(request);

                if (response.success && response.trace != null)
                {
                    var result = ParseAndApply(response.trace.action, hostileNpcs);
                    result.trace = response.trace;
                    result.latencyMs = response.latencyMs;
                    LastResult = result;

                    if (logEvaluations)
                        LogResult(result);

                    // M5 FIX: Auto-refresh allied NPC targets after each eval
                    SetAlliedTargets();
                }
                else
                {
                    // Safe degradation: NPCs continue in current state
                    if (logEvaluations)
                        Debug.LogWarning($"[NPCBrain] Eval #{EvaluationCount} failed: {response.error}. " +
                                         "NPCs continuing in current state.");

                    LastResult = new BrainEvalResult
                    {
                        success = false,
                        error = response.error,
                        latencyMs = response.latencyMs
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPCBrain] Evaluation exception: {ex.Message}");
                LastResult = new BrainEvalResult { success = false, error = ex.Message };
            }
            finally
            {
                IsEvaluating = false;
            }
        }

        // ====================================================================
        // Game State Builder
        // ====================================================================

        private string BuildGameStateJson(List<NPCStateMachine> hostileNpcs)
        {
            // Player data
            Vector3 pPos = playerTransform != null ? playerTransform.position : Vector3.zero;

            var sb = new System.Text.StringBuilder(1024);
            sb.Append("{");

            // Player
            sb.Append("\"player\":{");
            sb.Append($"\"position\":{{\"x\":{pPos.x:F1},\"y\":{pPos.y:F1},\"z\":{pPos.z:F1}}},");
            sb.Append($"\"health_percent\":{_playerHealthPercent:F2},");
            sb.Append($"\"weapon\":\"{_playerWeapon}\",");
            sb.Append($"\"accuracy_this_room\":{_playerAccuracyThisRoom:F2},");
            sb.Append($"\"kill_streak\":{_playerKillStreakThisRoom},");
            sb.Append($"\"movement_pattern\":\"{_playerMovementPattern}\",");
            sb.Append($"\"dominant_tactic\":\"{_playerDominantTactic}\",");
            sb.Append($"\"cover_usage\":{_playerCoverUsage:F2}");
            sb.Append("},");

            // NPCs
            sb.Append("\"npcs\":[");
            for (int i = 0; i < hostileNpcs.Count; i++)
            {
                var snapshot = hostileNpcs[i].GetSnapshot();
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"id\":\"{snapshot.npcId}\",");
                sb.Append($"\"type\":\"{snapshot.archetypeType}\",");
                sb.Append($"\"state\":\"{snapshot.currentState}\",");
                sb.Append($"\"health_percent\":{snapshot.healthPercent:F2},");
                sb.Append($"\"position\":{{\"x\":{snapshot.posX:F1},\"y\":{snapshot.posY:F1},\"z\":{snapshot.posZ:F1}}},");
                sb.Append($"\"has_line_of_sight\":{(snapshot.hasLineOfSight ? "true" : "false")},");
                sb.Append($"\"distance_to_player\":{snapshot.distanceToPlayer:F1},");
                sb.Append($"\"in_cover\":{(snapshot.isInCover ? "true" : "false")},");
                sb.Append($"\"time_in_state\":{snapshot.timeSinceLastStateChange:F1}");
                sb.Append("}");
            }
            sb.Append("],");

            // Context
            sb.Append($"\"room_id\":\"{_currentRoomId}\",");
            sb.Append($"\"evaluation_number\":{EvaluationCount},");
            sb.Append($"\"hostile_count\":{hostileNpcs.Count},");
            sb.Append($"\"allied_count\":{DefectedNPCs.Count},");

            // Defection eligibility
            bool defectionAllowed = _defectionsThisRoom < maxDefectionsPerRoom
                                    && _defectionsThisRun < maxDefectionsPerRun;
            sb.Append($"\"defection_allowed\":{(defectionAllowed ? "true" : "false")},");
            sb.Append($"\"defections_this_room\":{_defectionsThisRoom},");
            sb.Append($"\"defections_this_run\":{_defectionsThisRun}");

            sb.Append("}");
            return sb.ToString();
        }

        // ====================================================================
        // Response Parser
        // ====================================================================

        private BrainEvalResult ParseAndApply(string actionJson, List<NPCStateMachine> hostileNpcs)
        {
            var result = new BrainEvalResult { success = true };

            if (string.IsNullOrWhiteSpace(actionJson))
            {
                result.changesApplied = 0;
                return result;
            }

            try
            {
                string clean = StripCodeFences(actionJson);
                var output = JsonUtility.FromJson<BrainOutput>(clean);

                if (output?.state_changes == null || output.state_changes.Length == 0)
                {
                    result.changesApplied = 0;
                    result.squadTactic = output?.squad_tactic ?? "hold";
                    return result;
                }

                result.squadTactic = output.squad_tactic;
                result.tacticReasoning = output.tactic_reasoning;
                int applied = 0;

                foreach (var change in output.state_changes)
                {
                    var npc = hostileNpcs.FirstOrDefault(n => n.npcId == change.npc_id);
                    if (npc == null)
                    {
                        if (logEvaluations)
                            Debug.LogWarning($"[NPCBrain] Unknown NPC ID: {change.npc_id}");
                        continue;
                    }

                    if (!TryParseState(change.new_state, out NPCState newState))
                    {
                        if (logEvaluations)
                            Debug.LogWarning($"[NPCBrain] Invalid state '{change.new_state}' for {change.npc_id}");
                        continue;
                    }

                    // Defection validation
                    if (newState == NPCState.ALLIED)
                    {
                        if (!ValidateDefection(npc))
                        {
                            if (logEvaluations)
                                Debug.Log($"[NPCBrain] Defection denied for {npc.npcId}: failed validation.");
                            continue;
                        }
                    }

                    // Skip no-op transitions
                    if (npc.CurrentState == newState) continue;

                    // Apply the state change
                    npc.SetState(newState);
                    applied++;

                    if (logEvaluations)
                        Debug.Log($"[NPCBrain]   → {npc.npcId}: {newState} ({change.reason})");

                    // Track defection
                    if (newState == NPCState.ALLIED)
                    {
                        _defectionsThisRoom++;
                        _defectionsThisRun++;
                        DefectedNPCs.Add(npc);
                        ActiveNPCs.Remove(npc);

                        // Notify metrics tracker
                        if (PlayerMetricsTracker.Instance != null)
                            PlayerMetricsTracker.Instance.OnDefectionWitnessed();
                    }
                }

                result.changesApplied = applied;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPCBrain] Parse error: {ex.Message}");
                result.success = false;
                result.error = ex.Message;
            }

            return result;
        }

        // ====================================================================
        // Defection Validation
        // ====================================================================

        /// <summary>
        /// Server-side validation of defection eligibility, independent of
        /// what the agent decides. This is the safety net.
        /// </summary>
        private bool ValidateDefection(NPCStateMachine npc)
        {
            // Room limit
            if (_defectionsThisRoom >= maxDefectionsPerRoom)
            {
                if (logEvaluations) Debug.Log($"[NPCBrain] Defection denied: room limit ({maxDefectionsPerRoom}) reached.");
                return false;
            }

            // Run limit
            if (_defectionsThisRun >= maxDefectionsPerRun)
            {
                if (logEvaluations) Debug.Log($"[NPCBrain] Defection denied: run limit ({maxDefectionsPerRun}) reached.");
                return false;
            }

            // Already allied
            if (npc.IsAllied)
            {
                return false;
            }

            // Health threshold: NPCs must be wounded to defect
            float healthThreshold = npc.archetype switch
            {
                NPCArchetype.GRUNT => 0.20f,
                NPCArchetype.FLANKER => 0.20f,
                NPCArchetype.SUPPRESSOR => 0.15f,
                NPCArchetype.ELITE => 0.10f,
                _ => 0.15f
            };

            if (npc.HealthPercent > healthThreshold)
            {
                if (logEvaluations)
                    Debug.Log($"[NPCBrain] Defection denied for {npc.npcId}: HP {npc.HealthPercent:P0} > {healthThreshold:P0} threshold.");
                return false;
            }

            // ELITE resistance: only defect if last hostile standing
            if (npc.archetype == NPCArchetype.ELITE)
            {
                int otherHostiles = ActiveNPCs.Count(n => n != npc && !n.IsDead && !n.IsAllied);
                if (otherHostiles > 0)
                {
                    if (logEvaluations)
                        Debug.Log($"[NPCBrain] Defection denied for ELITE {npc.npcId}: {otherHostiles} other hostiles alive.");
                    return false;
                }
            }

            // Player must be dominant
            if (_playerAccuracyThisRoom < 0.5f || _playerKillStreakThisRoom < 1)
            {
                if (logEvaluations)
                    Debug.Log($"[NPCBrain] Defection denied: player not dominant enough " +
                              $"(acc={_playerAccuracyThisRoom:P0}, streak={_playerKillStreakThisRoom}).");
                return false;
            }

            return true;
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static bool TryParseState(string stateName, out NPCState state)
        {
            state = NPCState.PATROL;
            if (string.IsNullOrWhiteSpace(stateName)) return false;

            return stateName.Trim().ToUpperInvariant() switch
            {
                "PATROL" => Assign(NPCState.PATROL, out state),
                "ATTACK" => Assign(NPCState.ATTACK, out state),
                "FLANK" => Assign(NPCState.FLANK, out state),
                "SUPPRESS" => Assign(NPCState.SUPPRESS, out state),
                "RETREAT" => Assign(NPCState.RETREAT, out state),
                "ALLIED" => Assign(NPCState.ALLIED, out state),
                _ => false
            };
        }

        private static bool Assign(NPCState value, out NPCState state)
        {
            state = value;
            return true;
        }

        private static string StripCodeFences(string text)
        {
            string clean = text.Trim();
            if (clean.StartsWith("```"))
            {
                int nl = clean.IndexOf('\n');
                int lf = clean.LastIndexOf("```");
                if (nl > 0 && lf > nl)
                    clean = clean.Substring(nl + 1, lf - nl - 1).Trim();
            }
            return clean;
        }

        private void LogResult(BrainEvalResult result)
        {
            Debug.Log($"[NPCBrain] Eval #{EvaluationCount} complete in {result.latencyMs}ms — " +
                      $"{result.changesApplied} changes, tactic: {result.squadTactic ?? "none"}");

            if (!string.IsNullOrEmpty(result.tacticReasoning))
                Debug.Log($"[NPCBrain]   Reasoning: {result.tacticReasoning}");
        }

        // ====================================================================
        // Data Types
        // ====================================================================

        /// <summary>
        /// Parsed output from the Brain Agent's action field.
        /// </summary>
        [Serializable]
        private class BrainOutput
        {
            public BrainStateChange[] state_changes;
            public string squad_tactic;
            public string tactic_reasoning;
        }

        [Serializable]
        private class BrainStateChange
        {
            public string npc_id;
            public string new_state;
            public string reason;
        }

        /// <summary>
        /// Set the player transform for the allied NPCs to follow.
        /// </summary>
        public void SetAlliedTargets()
        {
            if (playerTransform == null) return;

            // Tell allied NPCs to attack nearest hostile
            foreach (var allied in DefectedNPCs)
            {
                if (allied == null || allied.IsDead) continue;

                var nearestHostile = ActiveNPCs
                    .Where(n => n != null && !n.IsDead && !n.IsAllied)
                    .OrderBy(n => Vector3.Distance(allied.transform.position, n.transform.position))
                    .FirstOrDefault();

                allied.SetAlliedTarget(nearestHostile?.transform);
            }
        }
    }

    /// <summary>
    /// Result of a single Brain Agent evaluation cycle.
    /// </summary>
    public class BrainEvalResult
    {
        public bool success;
        public int changesApplied;
        public string squadTactic;
        public string tacticReasoning;
        public string error;
        public long latencyMs;
        public AgentTrace trace;
    }
}
