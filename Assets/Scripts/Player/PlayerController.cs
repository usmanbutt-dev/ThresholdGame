// ============================================================================
// PlayerController.cs — Twin-stick player movement & rotation controller
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Left stick (VirtualJoystick): movement on XZ plane
// Right stick (AimJoystick): aim direction + auto-fire
// When aiming, player faces aim direction independently of movement.
// When not aiming, player faces movement direction.
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

        [Header("Shooting Movement")]
        [Tooltip("Movement speed multiplier when aiming/shooting (0.4 = 40% of run speed). " +
                 "Walk+Shoot animations are slower than run, so movement must slow down to match.")]
        [Range(0.2f, 1f)]
        [SerializeField] private float shootingSpeedMultiplier = 0.5f;

        [Tooltip("If true, player keeps facing last joystick direction when released.")]
        [SerializeField] private bool retainFacingDirection = true;

        [Header("Death")]
        [Tooltip("If true, movement is disabled when dead.")]
        [SerializeField] private bool disableOnDeath = true;

        // ====================================================================
        // State
        // ====================================================================

        /// <summary>True if the player is currently moving (left stick active).</summary>
        public bool IsMoving { get; private set; }

        /// <summary>True if the player is currently aiming (right stick active).</summary>
        public bool IsAiming { get; private set; }

        /// <summary>Current movement velocity magnitude (0–moveSpeed).</summary>
        public float CurrentSpeed => _rb.linearVelocity.magnitude;

        /// <summary>Direction the player is facing (aim or movement).</summary>
        public Vector3 FacingDirection { get; private set; } = Vector3.forward;

        /// <summary>Current movement direction from left stick (world XZ).</summary>
        public Vector3 MoveDirection { get; private set; }

        /// <summary>
        /// Angle between movement direction and facing direction in degrees.
        /// 0 = forward, 180 = backward, +90 = right strafe, -90 = left strafe.
        /// Used by PlayerAnimator for directional walk+shoot blending.
        /// </summary>
        public float MoveAngleRelativeToFacing { get; private set; }

        // ====================================================================
        // Singleton
        // ====================================================================

        public static PlayerController Instance { get; private set; }

        // ====================================================================
        // Internal
        // ====================================================================

        private Rigidbody _rb;
        private PlayerHealth _health;
        private PlayerWeapon _weapon;
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
            _weapon = GetComponent<PlayerWeapon>();

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
            // Get MOVEMENT input from LEFT stick
            Vector3 moveInput = Vector3.zero;
            var uiManager = UI.ThresholdUIManager.Instance;
            if (uiManager != null)
            {
                moveInput = uiManager.GetMoveInput(); // Returns (x, 0, z) normalized
            }

            // PC fallback: WASD / Arrow keys (when joystick has no input)
            if (moveInput.sqrMagnitude < 0.01f)
            {
                float h = 0f, v = 0f;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    v += 1f;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  v -= 1f;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  h -= 1f;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h += 1f;
                moveInput = new Vector3(h, 0f, v).normalized;
            }

            IsMoving = moveInput.sqrMagnitude > 0.01f;
            MoveDirection = moveInput.normalized;

            // Calculate target velocity on XZ plane
            // Slow down when aiming — walk+shoot anims are slower than run
            float speed = IsAiming ? moveSpeed * shootingSpeedMultiplier : moveSpeed;
            Vector3 targetVelocity = moveInput * speed;

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
        // Rotation — Twin-stick: aim stick overrides facing direction
        // ====================================================================

        private void HandleRotation()
        {
            var uiManager = UI.ThresholdUIManager.Instance;

            // Get AIM input from RIGHT stick
            Vector3 aimInput = Vector3.zero;
            if (uiManager != null)
                aimInput = uiManager.GetAimInput(); // Returns (x, 0, z)

            // PC fallback: hold Right Mouse Button to aim toward mouse cursor
            if (aimInput.sqrMagnitude < 0.01f && Input.GetMouseButton(1))
            {
                var ray = Camera.main != null ? Camera.main.ScreenPointToRay(Input.mousePosition) : default;
                var plane = new Plane(Vector3.up, transform.position);
                if (plane.Raycast(ray, out float dist))
                {
                    Vector3 worldPoint = ray.GetPoint(dist);
                    Vector3 dir = worldPoint - transform.position;
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.25f)
                        aimInput = dir.normalized;
                }
            }

            // Ignore aim stick rotation during reload so that the player faces their movement direction
            // and plays a clean forward walking animation instead of sliding sideways/backwards.
            if (_weapon != null && _weapon.IsReloading)
            {
                aimInput = Vector3.zero;
            }

            IsAiming = aimInput.sqrMagnitude > 0.01f;

            Vector3 desiredFacing;

            if (IsAiming)
            {
                // RIGHT STICK / MOUSE ACTIVE: face aim direction (independent of movement)
                desiredFacing = aimInput.normalized;
            }
            else if (IsMoving)
            {
                // ONLY LEFT STICK: face movement direction (classic single-stick)
                desiredFacing = MoveDirection;
            }
            else
            {
                // No input — retain current facing
                desiredFacing = retainFacingDirection ? FacingDirection : transform.forward;
            }

            if (desiredFacing.sqrMagnitude > 0.01f)
            {
                FacingDirection = desiredFacing;
                Quaternion targetRotation = Quaternion.LookRotation(FacingDirection, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, targetRotation, rotationSpeed * Time.fixedDeltaTime);
            }

            // Calculate relative move angle for directional animations
            if (IsMoving && FacingDirection.sqrMagnitude > 0.01f)
            {
                // Signed angle from facing to movement direction
                // 0 = walking forward, 180 = backward, 90 = right, -90 = left
                MoveAngleRelativeToFacing = Vector3.SignedAngle(FacingDirection, MoveDirection, Vector3.up);
            }
            else
            {
                MoveAngleRelativeToFacing = 0f;
            }
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
