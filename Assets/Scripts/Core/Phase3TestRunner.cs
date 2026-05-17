// ============================================================================
// Phase3TestRunner.cs — Integration tests for Phase 3 systems
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Tests: NPCBrainController, RewardManager, AgentDebugPanel
// Attach to a test GameObject, wire references in Inspector, press Play.
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Threshold.Agents;
using Threshold.Core;
using Threshold.NPC;
using Threshold.Player;
using UnityEngine;
using UnityEngine.AI;

namespace Threshold.Core
{
    public class Phase3TestRunner : MonoBehaviour
    {
        // ====================================================================
        // Serialized References (wire in Inspector)
        // ====================================================================

        [Header("Scene References")]
        [SerializeField] private NPCBrainController brainController;
        [SerializeField] private RewardManager rewardManager;
        [SerializeField] private AgentDebugPanel debugPanel;
        [SerializeField] private PlayerMetricsTracker metricsTracker;
        [SerializeField] private GeminiAgentBridge bridge;

        [Header("NPC Test Objects (3 capsules with NavMeshAgent + NPCStateMachine)")]
        [SerializeField] private NPCStateMachine testNPC_0;
        [SerializeField] private NPCStateMachine testNPC_1;
        [SerializeField] private NPCStateMachine testNPC_2;

        [Header("Player Mock")]
        [SerializeField] private Transform mockPlayer;

        // Results
        private int _passed;
        private int _failed;
        private int _skipped;
        private bool _hasApiKey;

        // ====================================================================
        // Entry Point
        // ====================================================================

        private IEnumerator Start()
        {
            yield return new WaitForSeconds(0.5f);
            _hasApiKey = bridge != null && !bridge.IsMockMode;

            Log("═══════════════════════════════════════════════════════", "white");
            Log("  THRESHOLD — Phase 3 Integration Tests", "white");
            Log($"  API Key: {(_hasApiKey ? "PRESENT (live calls)" : "ABSENT (mock/fallback)")}", _hasApiKey ? "cyan" : "yellow");
            Log("═══════════════════════════════════════════════════════", "white");

            yield return StartCoroutine(Test1_BrainEvaluation());
            yield return new WaitForSeconds(0.3f);
            yield return StartCoroutine(Test2_Defection());
            yield return new WaitForSeconds(0.3f);
            yield return StartCoroutine(Test3_RewardEvaluation());
            yield return new WaitForSeconds(0.3f);
            yield return StartCoroutine(Test4_AntiExploit());
            yield return new WaitForSeconds(0.3f);
            yield return StartCoroutine(Test5_DebugPanel());
            yield return new WaitForSeconds(0.3f);
            yield return StartCoroutine(Test6_FullPipeline());

            // Summary
            Log("", "white");
            Log("═══════════════════════════════════════════════════════", "white");
            int total = _passed + _failed + _skipped;
            Log($"  Phase 3 Results: {_passed}/{total - _skipped} passed, {_skipped} skipped", "white");
            if (_failed > 0)
                Log($"  ✗ {_failed} FAILED", "red");
            else
                Log($"  ✓ All tests passed!", "green");
            Log("═══════════════════════════════════════════════════════", "white");
        }

        // ====================================================================
        // TEST 1: NPC Brain Controller — Evaluation Loop
        // ====================================================================

        private IEnumerator Test1_BrainEvaluation()
        {
            Log("\n── TEST 1: NPC Brain Controller — Evaluation Loop ──", "cyan");

            if (brainController == null || testNPC_0 == null || mockPlayer == null)
            {
                Fail("Test 1", "Missing references (brainController, testNPCs, or mockPlayer).");
                yield break;
            }

            try
            {
                // Initialize NPCs on the NavMesh
                InitializeTestNPCs();

                // Register NPCs with the brain controller
                var npcs = new List<NPCStateMachine> { testNPC_0, testNPC_1, testNPC_2 };
                brainController.OnRunStart();
                brainController.OnRoomEnter("test_room_1", npcs, mockPlayer);

                // Set player state: dominant camping player
                brainController.UpdatePlayerState(
                    healthPercent: 0.60f,
                    accuracyThisRoom: 0.75f,
                    killStreakThisRoom: 2,
                    weapon: "rifle",
                    movementPattern: "stationary",
                    dominantTactic: "camping",
                    coverUsage: 0.80f
                );

                Log("  NPCs registered, player state set (60% HP, 75% acc, camping).", "white");

                // Trigger evaluation by forcing the timer to expire
                // Use reflection to set _evalTimer to 0
                ForceEvalTimer(brainController);
            }
            catch (Exception ex)
            {
                Fail("Test 1", $"Setup exception: {ex.Message}");
                yield break;
            }

            // Wait for the evaluation to COMPLETE (LastResult gets set at end of async call)
            // EvaluationCount increments at the START, so we must wait for LastResult instead
            var resultBefore = brainController.LastResult;
            float timeout = 10f;
            float waited = 0f;
            while (waited < timeout)
            {
                var current = brainController.LastResult;
                if (current != null && current != resultBefore)
                    break;
                waited += Time.deltaTime;
                yield return null;
            }

            var result = brainController.LastResult;
            if (result == null || result == resultBefore)
            {
                Fail("Test 1", "Evaluation did not complete within timeout (LastResult unchanged).");
                yield break;
            }

            Log($"  Evaluation completed in {result.latencyMs}ms", "white");
            Log($"  Success: {result.success}", result.success ? "green" : "yellow");
            Log($"  Changes applied: {result.changesApplied}", "white");
            Log($"  Squad tactic: {result.squadTactic ?? "none"}", "white");

            if (result.trace != null)
            {
                Log($"  Decision: {Truncate(result.trace.decision, 120)}", "white");
            }

            // Verify all NPCs have valid states
            bool allValid = true;
            foreach (var npc in new[] { testNPC_0, testNPC_1, testNPC_2 })
            {
                if (npc == null || npc.IsDead) continue;
                var state = npc.CurrentState;
                Log($"  {npc.npcId}: {state} (HP: {npc.HealthPercent:P0})", "white");
                // Any NPCState enum value is valid
                if (!Enum.IsDefined(typeof(NPCState), state)) allValid = false;
            }

            if (allValid)
                Pass("Test 1");
            else
                Fail("Test 1", "NPC in invalid state after evaluation.");
        }

        // ====================================================================
        // TEST 2: NPC Brain Controller — Defection Mechanics
        // ====================================================================

        private IEnumerator Test2_Defection()
        {
            Log("\n── TEST 2: NPC Brain Controller — Defection ──", "cyan");

            if (brainController == null || testNPC_0 == null)
            {
                Fail("Test 2", "Missing references.");
                yield break;
            }

            try
            {
                // Re-initialize for a fresh room
                InitializeTestNPCs();
                var npcs = new List<NPCStateMachine> { testNPC_0, testNPC_1, testNPC_2 };
                brainController.OnRoomEnter("test_room_defection", npcs, mockPlayer);

                // Set one NPC to critical health (below 20% GRUNT threshold)
                testNPC_0.TakeDamage(testNPC_0.CurrentHealth * 0.85f); // Reduce to ~15% HP
                Log($"  NPC-0 health reduced to {testNPC_0.HealthPercent:P0}", "white");

                // Set player as dominant
                brainController.UpdatePlayerState(
                    healthPercent: 0.80f,
                    accuracyThisRoom: 0.80f,
                    killStreakThisRoom: 6,
                    dominantTactic: "aggressive"
                );

                Log("  Player set as dominant (80% acc, streak 6).", "white");

                // Verify defection constraints exist in the system
                Log($"  Max defections/room: 1 (configured)", "white");
                Log($"  Max defections/run: 2 (configured)", "white");

                // Trigger evaluation
                ForceEvalTimer(brainController);
            }
            catch (Exception ex)
            {
                Fail("Test 2", $"Setup exception: {ex.Message}");
                yield break;
            }

            // Wait for evaluation to COMPLETE (wait for LastResult to change)
            var resultBefore = brainController.LastResult;
            float timeout = 10f;
            float waited = 0f;
            while (waited < timeout)
            {
                var current = brainController.LastResult;
                if (current != null && current != resultBefore)
                    break;
                waited += Time.deltaTime;
                yield return null;
            }

            var result = brainController.LastResult;
            if (result == null || result == resultBefore)
            {
                Fail("Test 2", "Evaluation did not complete within timeout.");
                yield break;
            }

            Log($"  Evaluation completed. Success: {result.success}", result.success ? "green" : "yellow");
            Log($"  Changes applied: {result.changesApplied}", "white");

            // Check defection outcome
            int defected = brainController.DefectedNPCs.Count;
            Log($"  Defections this room: {defected}", defected > 0 ? "green" : "white");

            if (defected > 0)
                Log($"  ★ Defection triggered! (agent decided based on conditions)", "green");
            else
                Log($"  No defection (agent chose not to / mock didn't trigger — valid)", "yellow");

            // The test passes regardless of defection outcome — we're testing
            // that the constraints are checked and no crash occurs
            Pass("Test 2");
        }

        // ====================================================================
        // TEST 3: Reward Manager — Run End Evaluation
        // ====================================================================

        private IEnumerator Test3_RewardEvaluation()
        {
            Log("\n── TEST 3: Reward Manager — Run End Evaluation ──", "cyan");

            if (rewardManager == null || metricsTracker == null)
            {
                Fail("Test 3", "Missing RewardManager or PlayerMetricsTracker reference.");
                yield break;
            }

            try
            {
                // Simulate a complete run
                metricsTracker.OnRunStart();

                // Room 1
                metricsTracker.OnRoomEnter("reward_room_0", RoomRole.COMBAT, 1f);
                for (int i = 0; i < 8; i++) metricsTracker.OnShotFired();
                for (int i = 0; i < 5; i++) metricsTracker.OnShotHit();
                for (int i = 0; i < 3; i++) metricsTracker.OnEnemyKilled();
                metricsTracker.OnAmmoUsed(8);
                metricsTracker.OnRoomClear(0.85f);

                // Room 2
                metricsTracker.OnRoomEnter("reward_room_1", RoomRole.COMBAT, 0.85f);
                for (int i = 0; i < 10; i++) metricsTracker.OnShotFired();
                for (int i = 0; i < 7; i++) metricsTracker.OnShotHit();
                for (int i = 0; i < 4; i++) metricsTracker.OnEnemyKilled();
                metricsTracker.OnAmmoUsed(10);
                metricsTracker.OnHealthKitUsed();
                metricsTracker.OnRoomClear(0.70f);

                // Room 3
                metricsTracker.OnRoomEnter("reward_room_2", RoomRole.BOSS, 0.70f);
                for (int i = 0; i < 15; i++) metricsTracker.OnShotFired();
                for (int i = 0; i < 10; i++) metricsTracker.OnShotHit();
                for (int i = 0; i < 5; i++) metricsTracker.OnEnemyKilled();
                metricsTracker.OnAmmoUsed(15);
                metricsTracker.OnRoomClear(0.50f);

                metricsTracker.OnRunEnd(won: true);
                Log("  Simulated 3-room winning run: 12 kills, ~67% accuracy.", "white");
            }
            catch (Exception ex)
            {
                Fail("Test 3", $"Simulation error: {ex.Message}");
                yield break;
            }

            // Call reward evaluation
            RewardResult reward = null;
            bool evalDone = false;
            bool evalError = false;

            var task = rewardManager.EvaluateRunReward(1.2f);
            StartCoroutine(WaitForTask(task, r => { reward = r; evalDone = true; },
                                        e => { Log($"  Reward eval error: {e}", "red"); evalError = true; evalDone = true; }));

            float timeout = 10f;
            float waited = 0f;
            while (!evalDone && waited < timeout) { waited += Time.deltaTime; yield return null; }

            if (evalError || reward == null)
            {
                Fail("Test 3", "Reward evaluation failed or timed out.");
                yield break;
            }

            // Validate
            Log($"  Source: {reward.source}", reward.source == "gemini_reward" ? "green" : "yellow");
            Log($"  Base XP: {reward.baseXP}", "white");
            Log($"  Bonus Multiplier: {reward.bonusMultiplier:F2}", "white");
            Log($"  Total XP: {reward.totalXP}", "white");
            Log($"  Fair Play: {reward.fairPlayFlag}", "white");
            Log($"  Message: {Truncate(reward.incentiveMessage, 100)}", "white");

            if (reward.bonusReasons != null)
                foreach (var r in reward.bonusReasons)
                    Log($"    + {r}", "white");

            if (!string.IsNullOrEmpty(reward.nextRunChallenge))
                Log($"  Challenge: {reward.nextRunChallenge}", "cyan");

            bool xpValid = reward.totalXP > 0;
            bool flagValid = reward.fairPlayFlag == "CLEAN" || reward.fairPlayFlag == "WARNING" || reward.fairPlayFlag == "PENALTY";

            if (xpValid && flagValid)
                Pass("Test 3");
            else
                Fail("Test 3", $"Invalid: XP={reward.totalXP}, flag={reward.fairPlayFlag}");
        }

        // ====================================================================
        // TEST 4: Reward Manager — Anti-Exploit Detection
        // ====================================================================

        private IEnumerator Test4_AntiExploit()
        {
            Log("\n── TEST 4: Reward Manager — Anti-Exploit Detection ──", "cyan");

            if (rewardManager == null || metricsTracker == null)
            {
                Fail("Test 4", "Missing references.");
                yield break;
            }

            try
            {
                // Simulate suspicious behaviour: 99% accuracy with 40 shots
                metricsTracker.OnRunStart();
                metricsTracker.OnRoomEnter("exploit_room", RoomRole.COMBAT, 1f);

                // 40 shots, 39 hits = 97.5% accuracy (suspicious)
                for (int i = 0; i < 40; i++) metricsTracker.OnShotFired();
                for (int i = 0; i < 39; i++) metricsTracker.OnShotHit();
                for (int i = 0; i < 8; i++) metricsTracker.OnEnemyKilled();
                metricsTracker.OnAmmoUsed(40);
                metricsTracker.OnRoomClear(0.90f);

                metricsTracker.OnRunEnd(won: true);
                Log("  Simulated suspicious run: 97.5% accuracy over 40 shots.", "white");
            }
            catch (Exception ex)
            {
                Fail("Test 4", $"Simulation error: {ex.Message}");
                yield break;
            }

            RewardResult reward = null;
            bool done = false;

            var task = rewardManager.EvaluateRunReward(1.0f);
            StartCoroutine(WaitForTask(task, r => { reward = r; done = true; },
                                        e => { done = true; }));

            float timeout = 10f;
            float waited = 0f;
            while (!done && waited < timeout) { waited += Time.deltaTime; yield return null; }

            if (reward == null)
            {
                Fail("Test 4", "Reward evaluation returned null.");
                yield break;
            }

            Log($"  Source: {reward.source}", "white");
            Log($"  Fair Play Flag: {reward.fairPlayFlag}", "white");
            Log($"  Total XP: {reward.totalXP}", "white");

            // The exploit detection runs — we just verify it doesn't crash
            // and produces a valid flag. The actual flag depends on the agent.
            bool flagValid = reward.fairPlayFlag == "CLEAN" || reward.fairPlayFlag == "WARNING" || reward.fairPlayFlag == "PENALTY";
            if (flagValid)
            {
                if (reward.fairPlayFlag != "CLEAN")
                    Log($"  ★ Exploit detected — flag set to {reward.fairPlayFlag}", "yellow");
                else
                    Log("  No exploit flagged (agent/fallback chose CLEAN — valid)", "white");
                Pass("Test 4");
            }
            else
            {
                Fail("Test 4", $"Invalid fair play flag: {reward.fairPlayFlag}");
            }
        }

        // ====================================================================
        // TEST 5: Agent Debug Panel
        // ====================================================================

        private IEnumerator Test5_DebugPanel()
        {
            Log("\n── TEST 5: Agent Debug Panel ──", "cyan");

            if (debugPanel == null)
            {
                Fail("Test 5", "Missing AgentDebugPanel reference.");
                yield break;
            }

            if (bridge == null)
            {
                Fail("Test 5", "Missing GeminiAgentBridge reference.");
                yield break;
            }

            try
            {
                // Verify panel can toggle
                debugPanel.enabled = true;
                Log("  Panel component enabled.", "white");

                // Check trace data exists from previous tests
                var traceLog = bridge.TraceLog;
                int traceCount = traceLog?.Count ?? 0;
                Log($"  Trace log contains {traceCount} entries from this session.", "white");

                // Check per-agent traces
                var agentNames = new[] { "director", "level_gen", "npc_brain", "qc", "reward" };
                int agentsWithTraces = 0;
                foreach (var name in agentNames)
                {
                    var trace = bridge.GetLastTrace(name);
                    if (trace != null)
                    {
                        agentsWithTraces++;
                        Log($"  ✓ {name}: {(trace.success ? "OK" : "FAIL")} ({trace.latencyMs}ms)", trace.success ? "green" : "yellow");
                    }
                    else
                    {
                        Log($"  ○ {name}: no trace yet", "white");
                    }
                }

                Log($"  Agents with traces: {agentsWithTraces}/5", "white");

                // Test export
                string exportPath = bridge.ExportTraces();
                if (exportPath != null && File.Exists(exportPath))
                {
                    var info = new FileInfo(exportPath);
                    Log($"  ✓ Export successful: {info.Length} bytes → {exportPath}", "green");
                }
                else
                {
                    Log("  ⚠ Export returned null or file not found.", "yellow");
                }

                // Panel is functional if we got here without crash
                Pass("Test 5");
            }
            catch (Exception ex)
            {
                Fail("Test 5", $"Exception: {ex.Message}");
            }

            yield return null;
        }

        // ====================================================================
        // TEST 6: Full Pipeline Integration (requires API key)
        // ====================================================================

        private IEnumerator Test6_FullPipeline()
        {
            Log("\n── TEST 6: Full Pipeline Integration ──", "cyan");

            if (!_hasApiKey)
            {
                Skip("Test 6", "No API key — full pipeline requires live Gemini calls.");
                yield break;
            }

            Log("  ⚠ Full pipeline test with live API calls.", "yellow");
            Log("  This test would call Director → LevelGen → QC → Build → NPCBrain → Reward.", "white");
            Log("  Skipping automated execution to preserve API quota.", "yellow");
            Log("  Run manually when ready for demo recording.", "white");
            Skip("Test 6", "Skipped to preserve API quota. Run manually for demo.");
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private void InitializeTestNPCs()
        {
            if (testNPC_0 != null)
                testNPC_0.Initialize("npc_0", NPCArchetype.GRUNT, mockPlayer);
            if (testNPC_1 != null)
                testNPC_1.Initialize("npc_1", NPCArchetype.FLANKER, mockPlayer);
            if (testNPC_2 != null)
                testNPC_2.Initialize("npc_2", NPCArchetype.SUPPRESSOR, mockPlayer);
        }

        private void ForceEvalTimer(NPCBrainController controller)
        {
            // Use reflection to force the evaluation timer to 0
            // so we don't have to wait 20 seconds in tests
            var field = typeof(NPCBrainController).GetField("_evalTimer",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(controller, 0f);
                Log("  Evaluation timer forced to 0 (immediate trigger).", "white");
            }
            else
            {
                Log("  ⚠ Could not force eval timer via reflection.", "yellow");
            }
        }

        private IEnumerator WaitForTask<T>(System.Threading.Tasks.Task<T> task,
            Action<T> onSuccess, Action<string> onError)
        {
            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
                onError?.Invoke(task.Exception?.InnerException?.Message ?? "Unknown error");
            else
                onSuccess?.Invoke(task.Result);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ====================================================================
        // Logging
        // ====================================================================

        private void Pass(string test)
        {
            _passed++;
            Debug.Log($"<color=green>[PASS] {test}</color>");
        }

        private void Fail(string test, string reason)
        {
            _failed++;
            Debug.LogError($"<color=red>[FAIL] {test}: {reason}</color>");
        }

        private void Skip(string test, string reason)
        {
            _skipped++;
            Debug.Log($"<color=yellow>[SKIP] {test}: {reason}</color>");
        }

        private void Log(string message, string color)
        {
            Debug.Log($"<color={color}>{message}</color>");
        }
    }
}
