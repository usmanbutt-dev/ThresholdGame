// ============================================================================
// PlayerController.cs — Core player movement & rotation controller
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Reads VirtualJoystick input via ThresholdUIManager, drives Rigidbody
// movement on the XZ plane, and rotates the player to face the joystick
// direction (which also controls weapon aim direction).
// ============================================================================

using UnityEngine;

namespace Threshold.Player
{
    /// <summary>
    /// Core player controller. Handles Rigidbody-based movement and
    /// facing rotation driven by the virtual joystick.
    /// Attach to the Player GameObject along with PlayerHealth and PlayerWeapon.
    /// Requires: Rigidbody (non-kinematic), Collider, Tag="Player"
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(PlayerHealth))]
    [RequireComponent(typeof(PlayerWeapon))]
    public class PlayerController : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("Movement")]
        [Tooltip("Base movement speed in units per second.")]
        [SerializeField] private float moveSpeed = 7f;

        [Tooltip("Smoothing factor for velocity changes (lower = snappier).")]
        [SerializeField] private float accelerationSmoothing = 0.1f;

        [Header("Rotation")]
        [Tooltip("How fast the player rotates to face joystick direction (degrees/sec).")]
        [SerializeField] private float rotationSpeed = 720f;

        [Tooltip("If true, player keeps facing last joystick direction when released.")]
        [SerializeField] private bool retainFacingDirection = true;

        [Header("Death")]
        [Tooltip("If true, movement is disabled when dead.")]
        [SerializeField] private bool disableOnDeath = true;

        // ====================================================================
        // State
        // ====================================================================

        /// <summary>True if the player is currently moving (joystick active).</summary>
        public bool IsMoving { get; private set; }

        /// <summary>Current movement velocity magnitude (0–moveSpeed).</summary>
        public float CurrentSpeed => _rb.linearVelocity.magnitude;

        /// <summary>Last non-zero joystick direction (for aim retention).</summary>
        public Vector3 FacingDirection { get; private set; } = Vector3.forward;

        // ====================================================================
        // Singleton
        // ====================================================================

        public static PlayerController Instance { get; private set; }

        // ====================================================================
        // Internal
        // ====================================================================

        private Rigidbody _rb;
        private PlayerHealth _health;
        private Vector3 _smoothVelocity;
        private Vector3 _inputVelocity;
        private NPC.NPCBrainController _cachedBrain;

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _rb = GetComponent<Rigidbody>();
            _health = GetComponent<PlayerHealth>();

            // Configure Rigidbody for top-down
            _rb.useGravity = true;
            _rb.constraints = RigidbodyConstraints.FreezeRotation; // No physics rotation
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

            // Ensure Player tag
            if (!gameObject.CompareTag("Player"))
            {
                Debug.LogWarning("[PlayerController] GameObject is not tagged 'Player'. " +
                                 "TopDownCamera and NPCBrainController expect the 'Player' tag.");
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void FixedUpdate()
        {
            // Skip if dead
            if (disableOnDeath && _health != null && _health.IsDead)
            {
                _rb.linearVelocity = new Vector3(0f, _rb.linearVelocity.y, 0f);
                IsMoving = false;
                return;
            }

            HandleMovement();
            HandleRotation();
        }

        // ====================================================================
        // Movement
        // ====================================================================

        private void HandleMovement()
        {
            // Get joystick input from UI manager
            Vector3 moveInput = Vector3.zero;
            var uiManager = UI.ThresholdUIManager.Instance;
            if (uiManager != null)
            {
                moveInput = uiManager.GetMoveInput(); // Returns (x, 0, z) normalized
            }

            IsMoving = moveInput.sqrMagnitude > 0.01f;

            // Calculate target velocity on XZ plane
            Vector3 targetVelocity = moveInput * moveSpeed;

            // Smooth acceleration/deceleration
            _inputVelocity = Vector3.SmoothDamp(
                _inputVelocity, targetVelocity, ref _smoothVelocity, accelerationSmoothing);

            // Apply to rigidbody (preserve Y velocity for gravity)
            _rb.linearVelocity = new Vector3(
                _inputVelocity.x,
                _rb.linearVelocity.y,
                _inputVelocity.z);
        }

        // ====================================================================
        // Rotation (faces joystick direction = aim direction)
        // ====================================================================

        private void HandleRotation()
        {
            Vector3 moveInput = Vector3.zero;
            var uiManager = UI.ThresholdUIManager.Instance;
            if (uiManager != null)
            {
                moveInput = uiManager.GetMoveInput();
            }

            if (moveInput.sqrMagnitude > 0.01f)
            {
                // Update facing direction
                FacingDirection = moveInput.normalized;

                // Rotate toward joystick direction
                Quaternion targetRotation = Quaternion.LookRotation(FacingDirection, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, targetRotation, rotationSpeed * Time.fixedDeltaTime);
            }
            else if (!retainFacingDirection)
            {
                // If not retaining, could snap back — but typically we retain
            }
            // If retainFacingDirection is true (default), player keeps facing
            // the last joystick direction when released
        }

        // ====================================================================
        // Public API
        // ====================================================================

        /// <summary>
        /// Teleport the player to a position (e.g. room entry point).
        /// Snaps camera to follow.
        /// </summary>
        public void TeleportTo(Vector3 position)
        {
            _rb.position = position;
            transform.position = position;
            _inputVelocity = Vector3.zero;
            _smoothVelocity = Vector3.zero;
            _rb.linearVelocity = Vector3.zero;

            // Snap camera
            UI.TopDownCamera.Instance?.SnapToTarget();

            Debug.Log($"[PlayerController] Teleported to {position}");
        }

        /// <summary>
        /// Full reset for a new run — health, ammo, velocity.
        /// </summary>
        public void ResetForNewRun(Vector3 spawnPosition)
        {
            TeleportTo(spawnPosition);
            _health?.ResetHealth();
            GetComponent<PlayerWeapon>()?.ResetAmmo();
            GetComponent<PlayerAnimator>()?.ResetAnimator();

            Debug.Log("[PlayerController] Reset for new run.");
        }

        /// <summary>
        /// Returns the current player state data for NPCBrainController.
        /// </summary>
        public void BroadcastStateToNPCBrain()
        {
            if (_cachedBrain == null)
                _cachedBrain = FindAnyObjectByType<NPC.NPCBrainController>();
            if (_cachedBrain == null || _health == null) return;

            var weapon = GetComponent<PlayerWeapon>();
            var metrics = PlayerMetricsTracker.Instance;

            float accuracy = 0f;
            int killStreak = 0;
            if (metrics != null)
            {
                var roomMetrics = metrics.GetCurrentRoomMetrics();
                if (roomMetrics != null)
                {
                    accuracy = roomMetrics.Accuracy;
                    killStreak = roomMetrics.enemiesKilled;
                }
            }

            _cachedBrain.UpdatePlayerState(
                _health.HealthPercent,
                accuracy,
                killStreak,
                "rifle" // Default weapon name
            );
        }

        private float _nextBroadcastTime;

        private void Update()
        {
            // Broadcast player state to NPC Brain controller every 0.5s
            if (Time.time >= _nextBroadcastTime)
            {
                _nextBroadcastTime = Time.time + 0.5f;
                BroadcastStateToNPCBrain();
            }
        }
    }
}
