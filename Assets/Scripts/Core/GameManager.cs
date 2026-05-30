// ============================================================================
// GameManager.cs — Core game loop orchestrator
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Manages the full run lifecycle:
//   1. Generate floor → 2. Bake NavMesh → 3. Spawn player + NPCs
//   4. Track NPC kills → 5. On all killed OR player died → restart
//
// Replaces EnemySystemTestRunner for production gameplay.
// ============================================================================

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Threshold.Agents;
using Threshold.Generation;
using Threshold.NPC;
using Threshold.Player;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;

namespace Threshold.Core
{
    /// <summary>
    /// Singleton game manager that orchestrates the full gameplay loop:
    /// generate → spawn → play → win/die → restart.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("Required References")]
        [Tooltip("FloorGenerator component that builds the dungeon.")]
        [SerializeField] private FloorGenerator floorGenerator;

        [Tooltip("NPCBrainController for NPC AI (optional — NPCs auto-engage without it).")]
        [SerializeField] private NPCBrainController brainController;

        [Tooltip("NavMeshSurface for runtime baking. Auto-created if null.")]
        [SerializeField] private NavMeshSurface navMeshSurface;

        [Tooltip("LevelGenerationPipeline for AI-driven level gen (auto-found if null).")]
        [SerializeField] private LevelGenerationPipeline levelPipeline;

        [Header("Generation")]
        [Tooltip("Number of rooms to generate per run.")]
        [SerializeField] private int targetRoomCount = 7;

        [Tooltip("Base enemies per combat room.")]
        [SerializeField] private int baseEnemiesPerRoom = 3;

        [Tooltip("Number of ELITE NPCs per floor.")]
        [SerializeField] private int eliteCount = 1;

        [Tooltip("Difficulty multiplier. Scales with completed runs.")]
        [SerializeField] private float baseDifficulty = 1.0f;

        [Tooltip("Difficulty increase per completed run.")]
        [SerializeField] private float difficultyPerRun = 0.15f;

        [Header("Restart")]
        [Tooltip("Delay before restarting after win or death (seconds).")]
        [SerializeField] private float restartDelay = 2.5f;

        [Tooltip("Brief pause before starting generation on first load.")]
        [SerializeField] private float initialDelay = 0.5f;

        [Header("Debug")]
        [SerializeField] private bool logEvents = true;

        [Header("Background Music")]
        [Tooltip("Background music clip. Fades in on run start, fades out on death.")]
        [SerializeField] private AudioClip backgroundMusic;

        [Tooltip("Max volume for background music.")]
        [Range(0f, 1f)]
        [SerializeField] private float musicVolume = 0.3f;

        [Tooltip("Fade duration for music transitions (seconds).")]
        [Range(0.5f, 5f)]
        [SerializeField] private float musicFadeDuration = 2f;

        // ====================================================================
        // Singleton
        // ====================================================================

        public static GameManager Instance { get; private set; }

        // ====================================================================
        // State
        // ====================================================================

        /// <summary>Current game phase.</summary>
        public GamePhase Phase { get; private set; } = GamePhase.Loading;

        /// <summary>Total completed runs this session.</summary>
        public int CompletedRuns { get; private set; }

        /// <summary>Current run number (1-based).</summary>
        public int CurrentRun { get; private set; }

        /// <summary>True if in the middle of a restart transition.</summary>
        public bool IsRestarting { get; private set; }

        // ====================================================================
        // Internal
        // ====================================================================

        private List<NPCStateMachine> _allNPCs = new();
        private Dictionary<string, List<NPCStateMachine>> _roomNPCs = new();
        private Transform _playerTransform;
        private PlayerHealth _playerHealth;
        private PlayerWeapon _playerWeapon;
        private int _totalNPCCount;
        private int _killedNPCCount;
        private RoomGraphConfig _currentConfig;
        private bool _subscribedToPlayerDeath;
        private Canvas _overlayCanvas;
        private CanvasGroup _overlayGroup;
        private Text _overlayTitle;
        private Text _overlaySub;

        // Room detection for brain controller
        private string _currentPlayerRoomId;
        private float _moduleWidth = 10f;
        private int _killStreak;

        // Director Agent profile (set between runs)
        private DifficultyProfile _directorProfile;

        // Background music
        private AudioSource _bgmSource;
        private Coroutine _bgmFadeCoroutine;

        // ====================================================================
        // Enums
        // ====================================================================

        public enum GamePhase
        {
            Loading,      // Generating floor
            Playing,      // Active gameplay
            PlayerDied,   // Player death → waiting to restart
            LevelCleared, // All NPCs dead → waiting to restart
            Restarting    // Transition in progress
        }

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // Set up background music AudioSource
            _bgmSource = gameObject.AddComponent<AudioSource>();
            _bgmSource.playOnAwake = false;
            _bgmSource.loop = true;
            _bgmSource.spatialBlend = 0f; // 2D
            _bgmSource.volume = 0f;
        }

        private void Start()
        {
            // Disables VSync (required for targetFrameRate to work)
            QualitySettings.vSyncCount = 0;

            // Sets the target frame rate to 60
            Application.targetFrameRate = 60;

            CreateOverlayUI();

            // Validate references
            if (floorGenerator == null)
            {
                floorGenerator = FindAnyObjectByType<FloorGenerator>();
                if (floorGenerator == null)
                {
                    Log("ERROR: No FloorGenerator found! Cannot start.", "ERROR");
                    return;
                }
            }

            if (brainController == null)
                brainController = FindAnyObjectByType<NPCBrainController>();

            if (levelPipeline == null)
                levelPipeline = FindAnyObjectByType<LevelGenerationPipeline>();

            StartCoroutine(StartRunSequence());
        }

        private void Update()
        {
            if (Phase != GamePhase.Playing) return;

            // Track NPC deaths each frame
            CheckNPCStatus();

            // Detect room transitions for brain controller
            DetectPlayerRoom();

            // Sync player state to brain controller periodically
            SyncPlayerStateToBrain();
        }

        private void OnDestroy()
        {
            UnsubscribeFromPlayer();
            if (Instance == this) Instance = null;
        }

        // ====================================================================
        // Run Lifecycle
        // ====================================================================

        private IEnumerator StartRunSequence()
        {
            Phase = GamePhase.Loading;
            IsRestarting = false;
            CurrentRun++;

            Log($"═══ RUN {CurrentRun} STARTING ═══", "HEADER");

            // Brief delay to let Unity settle
            yield return new WaitForSeconds(initialDelay);

            // Step 1: Generate floor (pipeline or fallback)
            Log("Generating floor...");
            bool floorReady = false;
            yield return GenerateFloorCoroutine(result => floorReady = result);
            if (!floorReady)
            {
                Log("Floor generation failed!", "ERROR");
                yield break;
            }

            // Step 2: Bake NavMesh
            Log("Baking NavMesh...");
            BakeNavMesh();

            // Wait one frame for NavMesh to settle
            yield return null;

            // Step 3: Spawn player (FloorGenerator handles this if prefab is assigned)
            SetupPlayerReference();
            if (_playerTransform == null)
            {
                Log("ERROR: No player found after floor generation!", "ERROR");
                yield break;
            }

            // Step 4: Spawn NPCs
            Log("Spawning NPCs...");
            SpawnNPCs();

            // Step 5: Subscribe to player death
            SubscribeToPlayer();

            // Step 6: Set up camera
            var camera = UI.TopDownCamera.Instance;
            if (camera != null)
            {
                camera.SetTarget(_playerTransform);
                // Frame the entry room
                var entryRoom = _currentConfig?.rooms?.Find(r => r.role == RoomRole.ENTRY);
                if (entryRoom != null)
                    camera.OnRoomEnterByGrid(entryRoom.gridCol, entryRoom.gridRow);
            }

            // Step 7: Start brain controller
            if (brainController != null)
                brainController.OnRunStart();

            // Step 8: Start metrics tracking for this run
            PlayerMetricsTracker.Instance?.OnRunStart();

            // Start playing
            Phase = GamePhase.Playing;
            _killedNPCCount = 0;
            _killStreak = 0;
            _currentPlayerRoomId = null;

            // Start HUD run tracking with total generated rooms
            UI.ThresholdUIManager.Instance?.StartRun(_currentConfig?.rooms?.Count ?? targetRoomCount);

            // Step 9: Enter the first room in metrics tracking + brain
            var entryRoomConfig = _currentConfig?.rooms?.Find(r => r.role == RoomRole.ENTRY);
            if (entryRoomConfig != null)
            {
                PlayerMetricsTracker.Instance?.OnRoomEnter(
                    entryRoomConfig.roomId,
                    entryRoomConfig.role,
                    _playerHealth != null ? _playerHealth.HealthPercent : 1f);

                // CRITICAL FIX: Enter the first room in brain controller too
                EnterBrainRoom(entryRoomConfig.roomId);
                _currentPlayerRoomId = entryRoomConfig.roomId; // Prevent double-entry from Update
            }

            // Subscribe to weapon kills for brain state updates
            if (_playerWeapon != null)
                _playerWeapon.OnKill += HandleNPCKilledByPlayer;

            float difficulty = baseDifficulty + (CompletedRuns * difficultyPerRun);
            Log($"═══ RUN {CurrentRun} ACTIVE — {_totalNPCCount} NPCs, " +
                $"Difficulty: {difficulty:F1}x ═══", "HEADER");

            // Step 10: Fade in background music
            FadeBGM(true);
        }

        // ====================================================================
        // Floor Generation
        // ====================================================================

        /// <summary>
        /// Coroutine that tries the full AI pipeline (Director→LevelGen→QC→Build),
        /// then falls back to local procedural generation if pipeline is unavailable.
        /// </summary>
        private IEnumerator GenerateFloorCoroutine(System.Action<bool> callback)
        {
            // ── PATH A: Full AI Pipeline (all 5 agents) ──
            if (levelPipeline != null && GeminiAgentBridge.Instance != null &&
                GeminiAgentBridge.Instance.IsConfigured)
            {
                Log("Using AI Pipeline (Director→LevelGen→QC→Build)...");

                var task = levelPipeline.StartNewRun();
                while (!task.IsCompleted)
                    yield return null;

                if (!task.IsFaulted && task.Result != null && task.Result.success)
                {
                    var pResult = task.Result;
                    _currentConfig = pResult.config;
                    _directorProfile = pResult.difficulty;

                    Log($"AI Pipeline complete in {pResult.totalPipelineTimeMs:F0}ms — " +
                        $"{pResult.config.rooms.Count} rooms, source={pResult.generationSource}, " +
                        $"QC attempts={pResult.qcAttempts}");

                    if (!string.IsNullOrEmpty(pResult.directorDecisionText))
                        Log($"Director says: \"{pResult.directorDecisionText}\"");

                    callback?.Invoke(true);
                    yield break;
                }

                // Pipeline failed — fall through to local fallback
                string error = task.IsFaulted
                    ? task.Exception?.InnerException?.Message ?? "Unknown"
                    : "Pipeline returned null or unsuccessful result";
                Log($"AI Pipeline failed: {error}. Falling back to local generation.");
            }

            // ── PATH B: Local Procedural Fallback ──
            callback?.Invoke(GenerateFloorLocal());
        }

        /// <summary>
        /// Synchronous local floor generation using ProceduralRoomGenerator.
        /// Used as fallback when the AI pipeline is unavailable.
        /// </summary>
        private bool GenerateFloorLocal()
        {
            int seed = System.Environment.TickCount;
            float difficulty = baseDifficulty + (CompletedRuns * difficultyPerRun);

            // Use Director Agent profile if available, otherwise fall back to defaults
            DifficultyProfile diffProfile;
            if (_directorProfile != null)
            {
                diffProfile = _directorProfile;
                Log($"Using Director profile: {diffProfile.difficultyMultiplier:F1}x, " +
                    $"{diffProfile.targetRoomCount} rooms, {diffProfile.baseEnemiesPerRoom} enemies/room");
            }
            else
            {
                diffProfile = new DifficultyProfile
                {
                    difficultyMultiplier = difficulty,
                    targetRoomCount = targetRoomCount,
                    baseEnemiesPerRoom = baseEnemiesPerRoom,
                    eliteCount = eliteCount,
                    eventProbability = 0.3f,
                    preferredTactic = "ATTACK"
                };
            }

            _currentConfig = ProceduralRoomGenerator.GenerateFallback(diffProfile, seed);
            if (_currentConfig == null || _currentConfig.rooms.Count == 0)
            {
                Log("ProceduralRoomGenerator returned null!", "ERROR");
                return false;
            }

            Log($"Local gen: {_currentConfig.rooms.Count} rooms, " +
                $"{_currentConfig.edges.Count} edges, seed={seed}");

            bool success = floorGenerator.BuildFloor(_currentConfig);
            if (!success)
            {
                Log("FloorGenerator.BuildFloor() failed!", "ERROR");
                return false;
            }

            Log($"Floor built. Entry={floorGenerator.EntryWorldPosition}");
            return true;
        }

        // ====================================================================
        // NavMesh
        // ====================================================================

        private void BakeNavMesh()
        {
            if (navMeshSurface == null)
                navMeshSurface = FindAnyObjectByType<NavMeshSurface>();

            if (navMeshSurface == null && floorGenerator != null)
            {
                navMeshSurface = floorGenerator.gameObject.AddComponent<NavMeshSurface>();
                navMeshSurface.collectObjects = CollectObjects.Children;
                navMeshSurface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                Log("Auto-created NavMeshSurface on FloorGenerator.");
            }

            if (navMeshSurface != null)
            {
                navMeshSurface.BuildNavMesh();
                Log("NavMesh baked — NPCs can pathfind.");
            }
        }

        // ====================================================================
        // Player Setup
        // ====================================================================

        private void SetupPlayerReference()
        {
            // FloorGenerator.BuildFloor() already spawns the player if prefab is set
            _playerTransform = floorGenerator.PlayerTransform;

            // Fallback: find by tag
            if (_playerTransform == null)
            {
                var playerObj = GameObject.FindGameObjectWithTag("Player");
                if (playerObj != null)
                    _playerTransform = playerObj.transform;
            }

            if (_playerTransform != null)
            {
                _playerHealth = _playerTransform.GetComponent<PlayerHealth>();
                _playerWeapon = _playerTransform.GetComponent<PlayerWeapon>();

                // Reset player state for new run
                var controller = _playerTransform.GetComponent<PlayerController>();
                if (controller != null)
                {
                    controller.ResetForNewRun(floorGenerator.EntryWorldPosition + Vector3.up * 0.5f);
                }

                Log($"Player ready at {_playerTransform.position}");
            }
        }

        private void SubscribeToPlayer()
        {
            if (_subscribedToPlayerDeath || _playerHealth == null) return;
            _playerHealth.OnDied += HandlePlayerDeath;
            _subscribedToPlayerDeath = true;
        }

        private void UnsubscribeFromPlayer()
        {
            if (_playerHealth != null && _subscribedToPlayerDeath)
            {
                _playerHealth.OnDied -= HandlePlayerDeath;
                _subscribedToPlayerDeath = false;
            }
        }

        // ====================================================================
        // NPC Spawning + Tracking
        // ====================================================================

        private void SpawnNPCs()
        {
            _allNPCs.Clear();
            _roomNPCs.Clear();

            if (_playerTransform == null) return;

            _roomNPCs = floorGenerator.SpawnAllNPCs(_playerTransform);

            foreach (var kvp in _roomNPCs)
            {
                foreach (var npc in kvp.Value)
                    _allNPCs.Add(npc);
            }

            _totalNPCCount = _allNPCs.Count;
            _killedNPCCount = 0;

            Log($"Spawned {_totalNPCCount} NPCs across {_roomNPCs.Count} rooms.");
        }

        private void CheckNPCStatus()
        {
            int deadCount = 0;
            foreach (var npc in _allNPCs)
            {
                // null = destroyed (already died and was cleaned up)
                if (npc == null || npc.IsDead)
                    deadCount++;
            }

            if (deadCount != _killedNPCCount)
            {
                _killedNPCCount = deadCount;

                // Update HUD with kill count
                int alive = _totalNPCCount - _killedNPCCount;
                Log($"NPCs remaining: {alive}/{_totalNPCCount}");

                // Check win condition
                if (_killedNPCCount >= _totalNPCCount && _totalNPCCount > 0)
                {
                    HandleLevelCleared();
                }
            }
        }

        // ====================================================================
        // Room Detection (drives NPC Brain Controller)
        // ====================================================================

        private void DetectPlayerRoom()
        {
            if (_currentConfig == null || _playerTransform == null) return;
            if (brainController == null) return;

            int col = Mathf.RoundToInt(_playerTransform.position.x / _moduleWidth);
            int row = Mathf.RoundToInt(_playerTransform.position.z / _moduleWidth);

            var room = _currentConfig.rooms.Find(r => r.gridCol == col && r.gridRow == row);
            if (room == null) return;

            if (room.roomId != _currentPlayerRoomId)
            {
                string oldRoom = _currentPlayerRoomId;
                _currentPlayerRoomId = room.roomId;

                Log($"Player entered room: {room.roomId} ({room.role})");

                // Exit previous room in brain
                if (!string.IsNullOrEmpty(oldRoom))
                    brainController.OnRoomExit();

                // Enter new room with its NPCs
                EnterBrainRoom(room.roomId);

                // Update camera room framing
                var camera = UI.TopDownCamera.Instance;
                if (camera != null)
                    camera.OnRoomEnterByGrid(room.gridCol, room.gridRow);

                // Update metrics tracker
                PlayerMetricsTracker.Instance?.OnRoomEnter(
                    room.roomId, room.role,
                    _playerHealth != null ? _playerHealth.HealthPercent : 1f);

                // Update HUD Room Progress
                int roomIndex = (_currentConfig?.rooms?.IndexOf(room) ?? 0) + 1;
                UI.ThresholdUIManager.Instance?.UpdateRoomProgress(roomIndex, _currentConfig?.rooms?.Count ?? targetRoomCount);
            }
        }

        private void EnterBrainRoom(string roomId)
        {
            if (brainController == null || _playerTransform == null) return;

            if (_roomNPCs.TryGetValue(roomId, out var npcsInRoom))
            {
                var living = npcsInRoom.Where(n => n != null && !n.IsDead).ToList();
                brainController.OnRoomEnter(roomId, living, _playerTransform);
                brainController.UpdatePlayerState(
                    _playerHealth != null ? _playerHealth.HealthPercent : 1f,
                    50f, // Default accuracy — updated by SyncPlayerStateToBrain
                    _killStreak
                );
                Log($"Brain Controller: {living.Count} NPCs registered for room {roomId}.");
            }
        }

        private void SyncPlayerStateToBrain()
        {
            if (brainController == null || _playerHealth == null) return;

            brainController.UpdatePlayerState(
                _playerHealth.HealthPercent,
                50f, // PlayerMetricsTracker provides real accuracy when available
                _killStreak
            );
        }

        private void HandleNPCKilledByPlayer(Transform npcTransform)
        {
            _killStreak++;

            // Notify brain controller of the kill
            if (npcTransform != null)
            {
                var npc = npcTransform.GetComponent<NPCStateMachine>();
                if (npc != null)
                    brainController?.OnNPCDeath(npc);
            }
        }

        // ====================================================================
        // Win / Death Handlers
        // ====================================================================

        private void HandleLevelCleared()
        {
            if (Phase != GamePhase.Playing) return;
            Phase = GamePhase.LevelCleared;
            CompletedRuns++;

            // Fade out music for transition
            FadeBGM(false);

            Log($"═══ LEVEL CLEARED! Run {CurrentRun} complete. ═══", "WIN");

            // Report to metrics
            PlayerMetricsTracker.Instance?.OnRunEnd(true);

            // Evaluate rewards via Reward Agent
            EvaluateRunReward();

            // Show overlay
            ShowOverlay("LEVEL CLEARED", "Generating next floor...",
                new Color(0.2f, 0.9f, 0.3f, 1f));

            StartCoroutine(RestartAfterDelay());
        }

        private void HandlePlayerDeath()
        {
            if (Phase != GamePhase.Playing) return;
            Phase = GamePhase.PlayerDied;

            // Fade out music
            FadeBGM(false);

            Log($"═══ PLAYER ELIMINATED — Run {CurrentRun} ═══", "DEATH");

            // Report to metrics
            PlayerMetricsTracker.Instance?.OnRunEnd(false);

            // Evaluate rewards via Reward Agent
            EvaluateRunReward();

            // Show overlay
            ShowOverlay("ELIMINATED", "Restarting...",
                new Color(0.9f, 0.15f, 0.15f, 1f));

            StartCoroutine(RestartAfterDelay());
        }

        // ====================================================================
        // Restart
        // ====================================================================

        private IEnumerator RestartAfterDelay()
        {
            IsRestarting = true;

            // Fade in overlay
            float fadeDuration = Mathf.Min(restartDelay * 0.5f, 1f);
            float elapsed = 0f;
            if (_overlayGroup != null)
            {
                _overlayCanvas.gameObject.SetActive(true);
                while (elapsed < fadeDuration)
                {
                    elapsed += Time.unscaledDeltaTime;
                    _overlayGroup.alpha = Mathf.Clamp01(elapsed / fadeDuration);
                    yield return null;
                }
                _overlayGroup.alpha = 1f;
            }

            // Wait
            yield return new WaitForSecondsRealtime(restartDelay * 0.5f);

            // Cleanup
            Log("Cleaning up previous run...");
            UnsubscribeFromPlayer();

            // Unsubscribe from weapon kills
            if (_playerWeapon != null)
                _playerWeapon.OnKill -= HandleNPCKilledByPlayer;

            if (brainController != null)
                brainController.OnRoomExit();

            // Destroy NavMesh before floor cleanup
            if (navMeshSurface != null)
                navMeshSurface.RemoveData();

            floorGenerator.CleanUp();
            _allNPCs.Clear();
            _roomNPCs.Clear();
            _subscribedToPlayerDeath = false;
            _currentPlayerRoomId = null;
            _killStreak = 0;
            _playerTransform = null;
            _playerHealth = null;
            _playerWeapon = null;

            // Fade out overlay
            yield return new WaitForSecondsRealtime(0.3f);

            // Start new run (pipeline handles Director→LevelGen→QC internally)
            StartCoroutine(StartRunSequence());

            // Fade out overlay after new run starts
            yield return new WaitForSeconds(0.5f);
            if (_overlayGroup != null)
            {
                elapsed = 0f;
                while (elapsed < 0.5f)
                {
                    elapsed += Time.unscaledDeltaTime;
                    _overlayGroup.alpha = 1f - Mathf.Clamp01(elapsed / 0.5f);
                    yield return null;
                }
                _overlayCanvas.gameObject.SetActive(false);
            }
        }
        // ====================================================================
        // Agent Integration — Reward
        // ====================================================================

        /// <summary>
        /// Fire-and-forget Reward Agent evaluation. Called at run end
        /// (both win and death) to grant XP and check unlocks.
        /// </summary>
        private async void EvaluateRunReward()
        {
            if (RewardManager.Instance == null)
            {
                Log("Reward Agent skipped — RewardManager not found.");
                return;
            }

            try
            {
                float difficulty = _directorProfile?.difficultyMultiplier
                                ?? (baseDifficulty + (CompletedRuns * difficultyPerRun));
                var result = await RewardManager.Instance.EvaluateRunReward(difficulty);

                if (result != null)
                {
                    Log($"Reward: +{result.totalXP} XP (source: {result.source}). " +
                        $"Total: {RewardManager.Instance.Progression.totalXP} XP, " +
                        $"Level {RewardManager.Instance.GetLevel()}");
                }
            }
            catch (System.Exception ex)
            {
                Log($"Reward Agent error: {ex.Message}. XP not awarded.");
            }
        }

        private AudioSource GetActiveBgmSource()
        {
            if (BackgroundMusic.Instance != null && BackgroundMusic.Instance.AudioSource != null)
            {
                return BackgroundMusic.Instance.AudioSource;
            }
            return _bgmSource;
        }

        private void FadeBGM(bool fadeIn)
        {
            AudioSource activeSource = GetActiveBgmSource();
            if (activeSource == null) return;

            // If falling back to local GameManager source, require clip to be assigned
            if (activeSource == _bgmSource && backgroundMusic == null) return;

            if (_bgmFadeCoroutine != null)
                StopCoroutine(_bgmFadeCoroutine);

            _bgmFadeCoroutine = StartCoroutine(FadeBGMCoroutine(fadeIn));
        }

        private IEnumerator FadeBGMCoroutine(bool fadeIn)
        {
            AudioSource activeSource = GetActiveBgmSource();
            if (activeSource == null) yield break;

            if (fadeIn)
            {
                // Start playing if not already
                if (!activeSource.isPlaying)
                {
                    if (activeSource == _bgmSource)
                    {
                        activeSource.clip = backgroundMusic;
                    }
                    activeSource.volume = 0f;
                    activeSource.Play();
                }

                // Fade in
                float elapsed = 0f;
                float startVol = activeSource.volume;
                while (elapsed < musicFadeDuration)
                {
                    elapsed += Time.unscaledDeltaTime;
                    activeSource.volume = Mathf.Lerp(startVol, musicVolume, elapsed / musicFadeDuration);
                    yield return null;
                }
                activeSource.volume = musicVolume;
            }
            else
            {
                // Fade out
                float elapsed = 0f;
                float startVol = activeSource.volume;
                while (elapsed < musicFadeDuration)
                {
                    elapsed += Time.unscaledDeltaTime;
                    activeSource.volume = Mathf.Lerp(startVol, 0f, elapsed / musicFadeDuration);
                    yield return null;
                }
                activeSource.volume = 0f;
                activeSource.Stop();
            }

            _bgmFadeCoroutine = null;
        }

        // ====================================================================
        // Overlay UI (Runtime — no prefab)
        // ====================================================================

        private void CreateOverlayUI()
        {
            var canvasObj = new GameObject("GameOverlay");
            canvasObj.transform.SetParent(transform);

            _overlayCanvas = canvasObj.AddComponent<Canvas>();
            _overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _overlayCanvas.sortingOrder = 999;

            var scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;

            canvasObj.AddComponent<GraphicRaycaster>();

            _overlayGroup = canvasObj.AddComponent<CanvasGroup>();
            _overlayGroup.alpha = 0f;
            _overlayGroup.interactable = false;
            _overlayGroup.blocksRaycasts = false;

            // Dark background
            var bgObj = new GameObject("BG");
            bgObj.transform.SetParent(canvasObj.transform, false);
            var bgRect = bgObj.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            bgObj.AddComponent<Image>().color = new Color(0.03f, 0.03f, 0.05f, 0.88f);

            // Title text
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(canvasObj.transform, false);
            var titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 0.55f);
            titleRect.anchorMax = new Vector2(0.5f, 0.55f);
            titleRect.sizeDelta = new Vector2(800f, 120f);
            _overlayTitle = titleObj.AddComponent<Text>();
            _overlayTitle.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _overlayTitle.fontSize = 64;
            _overlayTitle.fontStyle = FontStyle.Bold;
            _overlayTitle.alignment = TextAnchor.MiddleCenter;
            _overlayTitle.color = Color.white;

            // Sub text
            var subObj = new GameObject("Sub");
            subObj.transform.SetParent(canvasObj.transform, false);
            var subRect = subObj.AddComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0.5f, 0.45f);
            subRect.anchorMax = new Vector2(0.5f, 0.45f);
            subRect.sizeDelta = new Vector2(600f, 60f);
            _overlaySub = subObj.AddComponent<Text>();
            _overlaySub.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _overlaySub.fontSize = 28;
            _overlaySub.alignment = TextAnchor.MiddleCenter;
            _overlaySub.color = new Color(0.7f, 0.7f, 0.7f, 0.9f);

            canvasObj.SetActive(false);
        }

        private void ShowOverlay(string title, string sub, Color titleColor)
        {
            if (_overlayTitle != null)
            {
                _overlayTitle.text = title;
                _overlayTitle.color = titleColor;
            }
            if (_overlaySub != null)
                _overlaySub.text = sub;
        }

        // ====================================================================
        // Public API
        // ====================================================================

        /// <summary>
        /// Manually trigger a restart (e.g. from pause menu).
        /// </summary>
        public void ForceRestart()
        {
            if (IsRestarting) return;
            Phase = GamePhase.Restarting;
            ShowOverlay("RESTARTING", "Generating new floor...",
                new Color(0.8f, 0.8f, 0.2f, 1f));
            StartCoroutine(RestartAfterDelay());
        }

        /// <summary>
        /// Returns the count of remaining alive NPCs.
        /// </summary>
        public int AliveNPCCount => _totalNPCCount - _killedNPCCount;

        /// <summary>
        /// Returns the total NPC count for this run.
        /// </summary>
        public int TotalNPCCount => _totalNPCCount;

        // ====================================================================
        // Logging
        // ====================================================================

        private void Log(string msg, string cat = "INFO")
        {
            if (!logEvents && cat == "INFO") return;
            string prefix = cat switch
            {
                "HEADER" => "  ",
                "ERROR"  => "  ❌ ",
                "WIN"    => "  ✅ ",
                "DEATH"  => "  💀 ",
                _        => "  "
            };
            Debug.Log($"[GameManager]{prefix}{msg}");
        }
    }
}
