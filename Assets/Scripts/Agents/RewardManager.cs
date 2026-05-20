// ============================================================================
// RewardManager.cs — Reward Agent (A5) + XP progression system
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Runs once per run-end. Agent evaluates effort, progress, retention risk,
// and fairness to determine contextual rewards. NOT random/fixed loot.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Threshold.Agents;
using Threshold.Player;
using UnityEngine;

namespace Threshold.Agents
{
    // ========================================================================
    // Data Types
    // ========================================================================

    [Serializable]
    public class RewardResult
    {
        public bool success;
        public string source; // "gemini_reward" or "local_fallback"
        public int baseXP;
        public float bonusMultiplier;
        public int totalXP;
        public string[] bonusReasons;
        public string unlockSuggestion;
        public string incentiveMessage;
        public string nextRunChallenge;
        public string fairPlayFlag; // CLEAN, WARNING, PENALTY
        public AgentTrace trace;
    }

    [Serializable]
    public class PlayerProgression
    {
        public int totalXP;
        public int currentLevel;
        public List<string> unlockedItems = new();
        public string activeChallenge;
        public string activeChallengeType;
        public bool challengeCompleted;
        public int challengesCompleted;
        public int challengesAccepted;
    }

    [Serializable]
    public class UnlockThreshold
    {
        public int xpRequired;
        public string itemId;
        public string displayName;
        public string category; // weapon, ability, cosmetic
    }

    [Serializable]
    public class FairPlayReport
    {
        public bool isClean;
        public List<string> flags = new();
        public float suspicionScore; // 0-1
    }

    // ========================================================================
    // Main Manager
    // ========================================================================

    public class RewardManager : MonoBehaviour
    {
        private static RewardManager _instance;
        public static RewardManager Instance => _instance;

        [Header("XP Settings")]
        [SerializeField] private int xpPerKill = 10;
        [SerializeField] private float winBonusPercent = 0.5f;

        [Header("Debug")]
        [SerializeField] private bool logRewards = true;

        public PlayerProgression Progression { get; private set; } = new();
        public RewardResult LastReward { get; private set; }

        private string _savePath;

        // Unlock table
        private static readonly UnlockThreshold[] Unlocks =
        {
            new() { xpRequired = 100,  itemId = "shotgun",        displayName = "Shotgun",           category = "weapon" },
            new() { xpRequired = 250,  itemId = "dash_ability",   displayName = "Tactical Dash",     category = "ability" },
            new() { xpRequired = 500,  itemId = "smg",            displayName = "SMG",               category = "weapon" },
            new() { xpRequired = 800,  itemId = "shield_burst",   displayName = "Shield Burst",      category = "ability" },
            new() { xpRequired = 1200, itemId = "sniper",         displayName = "Sniper Rifle",      category = "weapon" },
            new() { xpRequired = 1800, itemId = "slow_field",     displayName = "Temporal Slow",     category = "ability" },
            new() { xpRequired = 2500, itemId = "railgun",        displayName = "Railgun",           category = "weapon" },
            new() { xpRequired = 3500, itemId = "defector_charm", displayName = "Defector's Charm",  category = "cosmetic" },
            new() { xpRequired = 5000, itemId = "plasma_cannon",  displayName = "Plasma Cannon",     category = "weapon" },
        };

        // ====================================================================
        // System Prompt
        // ====================================================================

        private static readonly string SystemPrompt = @"
You are the REWARD AGENT for THRESHOLD, a top-down roguelite corridor shooter.
After each run, evaluate performance contextually and determine fair, motivating rewards.
No random rewards or fixed loot tables — every decision must be reasoned.

PRINCIPLE: Reward EFFORT over outcome. Lost but improved massively = big reward. Won by exploiting = reduced.

6 SIGNALS: run_outcome(win/loss,rooms,kills), difficulty_context(multiplier), improvement_delta(acc/time change),
retention_risk(streak,churn_risk,quick_retries), challenge_acceptance(explored?defections?), fair_play(exploit flags).

OUTPUT (strict JSON in action field):
{""base_xp"":<20-500>,""bonus_multiplier"":<1.0-3.0>,""bonus_reasons"":[""reasons""],
""unlock_suggestion"":""item_id or empty"",""incentive_message"":""2-3 sentences to player"",
""next_run_challenge"":""achievable challenge or empty"",""fair_play_flag"":""CLEAN|WARNING|PENALTY""}

RULES: base=kills×10 min, scale for difficulty/rooms. Improvement>5% acc=+50%. High difficulty=×multiplier.
Challenge done=+100%. churn_risk>0.6=+50%+encouraging msg. WARNING=-25%, PENALTY=-50%+no unlock.
Be specific in incentive_message, not generic. Reference their play style in challenge.
".Trim();

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            _savePath = Path.Combine(Application.persistentDataPath, "player_progression.json");
            LoadProgression();
        }

        private void OnDestroy() { if (_instance == this) _instance = null; }

        // ====================================================================
        // Public API
        // ====================================================================

        /// <summary>
        /// Evaluate rewards for the completed run. Call after OnRunEnd().
        /// </summary>
        public async Task<RewardResult> EvaluateRunReward(float difficultyMultiplier)
        {
            var tracker = PlayerMetricsTracker.Instance;
            var lastRun = tracker?.GetLastRunMetrics();
            if (lastRun == null)
            {
                Debug.LogWarning("[Reward] No run data available.");
                return BuildFallback(0, false, 1f);
            }

            // Check challenge completion before evaluation
            bool challengeDone = CheckChallengeCompletion(lastRun);
            if (challengeDone)
            {
                Progression.challengeCompleted = true;
                Progression.challengesCompleted++;
                tracker.OnChallengeCompleted();
            }

            // Fair play analysis
            var fairPlay = AnalyzeFairPlay(lastRun);

            // Build game state for agent
            string gameState = BuildRewardGameState(lastRun, difficultyMultiplier, fairPlay);

            var request = new AgentRequest(
                agentName: "reward",
                systemPrompt: SystemPrompt,
                gameStateJson: gameState,
                model: GeminiModel.Flash
            );

            var response = await GeminiAgentBridge.Instance.SendAgentRequest(request);

            RewardResult result;
            if (response.success && response.trace != null)
            {
                result = ParseRewardResponse(response.trace.action, lastRun, difficultyMultiplier);
                result.source = "gemini_reward";
                result.trace = response.trace;
            }
            else
            {
                result = BuildFallback(lastRun.totalKills, lastRun.won, difficultyMultiplier);
            }

            // Apply XP and check unlocks
            ApplyReward(result);
            LastReward = result;

            if (logRewards) LogReward(result);
            return result;
        }

        /// <summary>Current player level based on total XP.</summary>
        public int GetLevel() => Progression.currentLevel;

        /// <summary>XP needed for next unlock.</summary>
        public int GetXPToNextUnlock()
        {
            foreach (var u in Unlocks)
                if (!Progression.unlockedItems.Contains(u.itemId))
                    return u.xpRequired - Progression.totalXP;
            return 0;
        }

        /// <summary>Set the active challenge for the next run.</summary>
        public void SetActiveChallenge(string challenge, string type = "agent")
        {
            Progression.activeChallenge = challenge;
            Progression.activeChallengeType = type;
            Progression.challengeCompleted = false;
            Progression.challengesAccepted++;
            PlayerMetricsTracker.Instance?.OnChallengeAccepted();
            SaveProgression();
        }

        // ====================================================================
        // Fair Play / Anti-Exploit
        // ====================================================================

        private FairPlayReport AnalyzeFairPlay(RunMetrics run)
        {
            var report = new FairPlayReport { isClean = true };

            // AFK detection: very long session with few kills
            if (run.sessionLengthSeconds > 600 && run.totalKills < 3)
            {
                report.flags.Add("AFK_SUSPECTED: Long session, minimal kills");
                report.suspicionScore += 0.4f;
            }

            // Suspicious accuracy: > 95% with many shots is improbable
            if (run.overallAccuracy > 0.95f && run.totalShotsFired > 30)
            {
                report.flags.Add("ACCURACY_ANOMALY: >95% accuracy over 30+ shots");
                report.suspicionScore += 0.5f;
            }

            // Spawn camping: very fast room clears with high kills
            if (run.roomsCompleted > 0)
            {
                float avgTime = run.avgRoomClearTime;
                float avgKills = run.totalKills / (float)run.roomsCompleted;
                if (avgTime < 3f && avgKills > 4)
                {
                    report.flags.Add("SPAWN_CAMP: Extremely fast clears with high kills");
                    report.suspicionScore += 0.3f;
                }
            }

            // Session too short to be meaningful
            if (run.sessionLengthSeconds < 10f && run.totalKills > 5)
            {
                report.flags.Add("TIME_ANOMALY: Too many kills in very short session");
                report.suspicionScore += 0.4f;
            }

            report.suspicionScore = Mathf.Clamp01(report.suspicionScore);
            report.isClean = report.suspicionScore < 0.3f;
            return report;
        }

        // ====================================================================
        // Challenge Checking
        // ====================================================================

        private bool CheckChallengeCompletion(RunMetrics run)
        {
            if (string.IsNullOrEmpty(Progression.activeChallenge)) return false;
            if (Progression.challengeCompleted) return false;

            string challenge = Progression.activeChallenge.ToLowerInvariant();

            // Pattern-based challenge detection
            if (challenge.Contains("without healing") || challenge.Contains("no healing"))
                return run.totalHealthKitsUsed == 0 && run.roomsCompleted >= 3;

            if (challenge.Contains("accuracy") && challenge.Contains("70"))
                return run.overallAccuracy >= 0.70f;

            if (challenge.Contains("accuracy") && challenge.Contains("80"))
                return run.overallAccuracy >= 0.80f;

            if (challenge.Contains("defection") || challenge.Contains("defect"))
                return run.totalDefections >= 1;

            if (challenge.Contains("no deaths") || challenge.Contains("without dying"))
                return run.totalDeaths == 0 && run.won;

            if (challenge.Contains("speed") || challenge.Contains("under"))
                return run.avgRoomClearTime < 15f && run.roomsCompleted >= 3;

            // Generic: if they won, count it
            return run.won;
        }

        // ====================================================================
        // Game State Builder
        // ====================================================================

        private string BuildRewardGameState(RunMetrics run, float difficulty, FairPlayReport fairPlay)
        {
            var tracker = PlayerMetricsTracker.Instance;
            var history = tracker?.GetHistory();
            var prevRun = tracker?.GetRecentRuns(2)?.FirstOrDefault();

            var sb = new System.Text.StringBuilder(1024);
            sb.Append("{");
            sb.Append($"\"run_outcome\":{{\"won\":{(run.won ? "true" : "false")},\"rooms\":{run.roomsCompleted},\"kills\":{run.totalKills},\"deaths\":{run.totalDeaths}}},");
            sb.Append($"\"difficulty_multiplier\":{difficulty:F2},");
            sb.Append($"\"accuracy\":{run.overallAccuracy:F3},");
            sb.Append($"\"avg_room_time\":{run.avgRoomClearTime:F1},");
            sb.Append($"\"session_length\":{run.sessionLengthSeconds:F1},");
            sb.Append($"\"defections_witnessed\":{run.totalDefections},");

            // Improvement delta
            float accDelta = prevRun != null ? run.overallAccuracy - prevRun.overallAccuracy : 0f;
            float timeDelta = prevRun != null ? prevRun.avgRoomClearTime - run.avgRoomClearTime : 0f;
            sb.Append($"\"improvement\":{{\"accuracy_delta\":{accDelta:F3},\"time_delta\":{timeDelta:F1}}},");

            // Retention
            sb.Append($"\"retention\":{{\"streak\":{history?.winLossStreak ?? 0},\"churn_risk\":{run.churnRiskScore:F2},\"quick_retry\":{(run.wasQuickRetry ? "true" : "false")}}},");

            // Challenge
            sb.Append($"\"challenge\":{{\"active\":\"{Progression.activeChallenge ?? ""}\",\"completed\":{(Progression.challengeCompleted ? "true" : "false")}}},");

            // Fair play
            string flags = fairPlay.flags.Count > 0 ? string.Join(", ", fairPlay.flags) : "none";
            sb.Append($"\"fair_play\":{{\"suspicion_score\":{fairPlay.suspicionScore:F2},\"flags\":\"{flags}\",\"is_clean\":{(fairPlay.isClean ? "true" : "false")}}}");

            sb.Append("}");
            return sb.ToString();
        }

        // ====================================================================
        // Response Parser
        // ====================================================================

        private RewardResult ParseRewardResponse(string actionJson, RunMetrics run, float difficulty)
        {
            if (string.IsNullOrWhiteSpace(actionJson))
                return BuildFallback(run.totalKills, run.won, difficulty);

            try
            {
                string clean = StripCodeFences(actionJson);
                var raw = JsonUtility.FromJson<RewardOutputRaw>(clean);
                if (raw == null) return BuildFallback(run.totalKills, run.won, difficulty);

                int baseXP = Mathf.Clamp(raw.base_xp, 20, 500);
                float mult = Mathf.Clamp(raw.bonus_multiplier, 1f, 3f);

                // Apply challenge completion bonus
                if (Progression.challengeCompleted) mult += 1f;

                // Apply fair play penalty
                if (raw.fair_play_flag == "WARNING") mult *= 0.75f;
                else if (raw.fair_play_flag == "PENALTY") mult *= 0.5f;

                int total = Mathf.RoundToInt(baseXP * mult);

                return new RewardResult
                {
                    success = true,
                    baseXP = baseXP,
                    bonusMultiplier = mult,
                    totalXP = total,
                    bonusReasons = raw.bonus_reasons ?? Array.Empty<string>(),
                    unlockSuggestion = raw.unlock_suggestion ?? "",
                    incentiveMessage = raw.incentive_message ?? "Great effort! Keep pushing.",
                    nextRunChallenge = raw.next_run_challenge ?? "",
                    fairPlayFlag = raw.fair_play_flag ?? "CLEAN"
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Reward] Parse error: {ex.Message}");
                return BuildFallback(run.totalKills, run.won, difficulty);
            }
        }

        // ====================================================================
        // Fallback
        // ====================================================================

        private RewardResult BuildFallback(int kills, bool won, float difficulty)
        {
            int baseXP = kills * xpPerKill;
            float mult = won ? 1f + winBonusPercent : 1f;
            mult *= Mathf.Max(difficulty, 1f);

            if (Progression.challengeCompleted) mult += 1f;

            int total = Mathf.Max(Mathf.RoundToInt(baseXP * mult), 20); // Min 20 XP

            var reasons = new List<string>();
            reasons.Add($"{kills} kills × {xpPerKill} XP");
            if (won) reasons.Add($"Win bonus: +{winBonusPercent:P0}");
            if (difficulty > 1f) reasons.Add($"Difficulty {difficulty:F1}x bonus");
            if (Progression.challengeCompleted) reasons.Add("Challenge completed: +100%");

            return new RewardResult
            {
                success = true,
                source = "local_fallback",
                baseXP = baseXP,
                bonusMultiplier = mult,
                totalXP = total,
                bonusReasons = reasons.ToArray(),
                unlockSuggestion = "",
                incentiveMessage = won
                    ? "Solid run! Your skills are growing."
                    : "Tough fight, but every loss teaches something. Try again!",
                nextRunChallenge = "",
                fairPlayFlag = "CLEAN"
            };
        }

        // ====================================================================
        // XP & Progression
        // ====================================================================

        private void ApplyReward(RewardResult result)
        {
            Progression.totalXP += result.totalXP;
            Progression.currentLevel = CalculateLevel(Progression.totalXP);

            // Check new unlocks
            foreach (var unlock in Unlocks)
            {
                if (Progression.totalXP >= unlock.xpRequired &&
                    !Progression.unlockedItems.Contains(unlock.itemId))
                {
                    Progression.unlockedItems.Add(unlock.itemId);
                    if (logRewards)
                        Debug.Log($"[Reward] ★ UNLOCKED: {unlock.displayName} ({unlock.category})");
                }
            }

            // Set next challenge if agent suggested one
            if (!string.IsNullOrEmpty(result.nextRunChallenge))
                SetActiveChallenge(result.nextRunChallenge);

            SaveProgression();
        }

        private int CalculateLevel(int xp)
        {
            // Each level requires progressively more XP
            // Level 1: 0, Level 2: 100, Level 3: 300, Level 4: 600...
            int level = 1;
            int threshold = 100;
            int remaining = xp;
            while (remaining >= threshold)
            {
                remaining -= threshold;
                level++;
                threshold = level * 100;
            }
            return level;
        }

        /// <summary>XP progress toward next level as 0-1.</summary>
        public float GetLevelProgress()
        {
            int level = Progression.currentLevel;
            int thisLevelStart = 0;
            int threshold = 100;
            for (int i = 1; i < level; i++) { thisLevelStart += threshold; threshold = (i + 1) * 100; }
            int nextThreshold = level * 100;
            int xpInLevel = Progression.totalXP - thisLevelStart;
            return nextThreshold > 0 ? Mathf.Clamp01((float)xpInLevel / nextThreshold) : 0f;
        }

        // ====================================================================
        // Reward Screen Data
        // ====================================================================

        /// <summary>
        /// Returns structured data for the reward UI screen.
        /// </summary>
        public RewardScreenData GetRewardScreenData()
        {
            var result = LastReward;
            if (result == null) return null;

            var newUnlocks = new List<UnlockThreshold>();
            foreach (var u in Unlocks)
            {
                if (Progression.totalXP >= u.xpRequired &&
                    Progression.totalXP - result.totalXP < u.xpRequired)
                    newUnlocks.Add(u);
            }

            return new RewardScreenData
            {
                totalXP = result.totalXP,
                baseXP = result.baseXP,
                bonusMultiplier = result.bonusMultiplier,
                bonusReasons = result.bonusReasons,
                incentiveMessage = result.incentiveMessage,
                nextChallenge = result.nextRunChallenge,
                fairPlayFlag = result.fairPlayFlag,
                playerLevel = Progression.currentLevel,
                levelProgress = GetLevelProgress(),
                totalLifetimeXP = Progression.totalXP,
                newUnlocks = newUnlocks.Select(u => u.displayName).ToArray(),
                source = result.source
            };
        }

        // ====================================================================
        // Persistence
        // ====================================================================

        private void SaveProgression()
        {
            try
            {
                File.WriteAllText(_savePath, JsonUtility.ToJson(Progression, true));
            }
            catch (Exception ex) { Debug.LogWarning($"[Reward] Save failed: {ex.Message}"); }
        }

        private void LoadProgression()
        {
            try
            {
                if (File.Exists(_savePath))
                {
                    Progression = JsonUtility.FromJson<PlayerProgression>(File.ReadAllText(_savePath));
                    Progression.unlockedItems ??= new List<string>();
                    if (logRewards)
                        Debug.Log($"[Reward] Loaded: Level {Progression.currentLevel}, " +
                                  $"{Progression.totalXP} XP, {Progression.unlockedItems.Count} unlocks");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Reward] Load failed: {ex.Message}");
                Progression = new PlayerProgression();
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static string StripCodeFences(string text)
        {
            string c = text.Trim();
            if (c.StartsWith("```"))
            {
                int nl = c.IndexOf('\n'); int lf = c.LastIndexOf("```");
                if (nl > 0 && lf > nl) c = c.Substring(nl + 1, lf - nl - 1).Trim();
            }
            return c;
        }

        private void LogReward(RewardResult r)
        {
            Debug.Log($"[Reward] ── Run Reward [{r.source}] ──");
            Debug.Log($"[Reward]   Base: {r.baseXP} × {r.bonusMultiplier:F2} = {r.totalXP} XP");
            Debug.Log($"[Reward]   Fair Play: {r.fairPlayFlag}");
            if (r.bonusReasons != null)
                foreach (var reason in r.bonusReasons)
                    Debug.Log($"[Reward]   + {reason}");
            Debug.Log($"[Reward]   Message: {r.incentiveMessage}");
            if (!string.IsNullOrEmpty(r.nextRunChallenge))
                Debug.Log($"[Reward]   Challenge: {r.nextRunChallenge}");
            Debug.Log($"[Reward]   Total XP: {Progression.totalXP} (Level {Progression.currentLevel})");
        }

        // ====================================================================
        // Serialization Types
        // ====================================================================

        [Serializable] private class RewardOutputRaw
        {
            public int base_xp;
            public float bonus_multiplier;
            public string[] bonus_reasons;
            public string unlock_suggestion;
            public string incentive_message;
            public string next_run_challenge;
            public string fair_play_flag;
        }
    }

    /// <summary>Structured data for the reward UI screen.</summary>
    [Serializable]
    public class RewardScreenData
    {
        public int totalXP;
        public int baseXP;
        public float bonusMultiplier;
        public string[] bonusReasons;
        public string incentiveMessage;
        public string nextChallenge;
        public string fairPlayFlag;
        public int playerLevel;
        public float levelProgress;
        public int totalLifetimeXP;
        public string[] newUnlocks;
        public string source;
    }
}
