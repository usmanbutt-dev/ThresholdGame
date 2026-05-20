// ============================================================================
// PlayerAnimator.cs — Bridges player scripts to Animator parameters
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Drives the MechWarrior Animator Controller based on PlayerController,
// PlayerWeapon, and PlayerHealth state. Listens to events for one-shot
// triggers (fire, reload, death) and updates blend parameters each frame.
//
// Validates that the assigned AnimatorController contains the expected
// parameters at startup, and gracefully skips updates if they are missing.
// ============================================================================

using UnityEngine;

namespace Threshold.Player
{
    /// <summary>
    /// Bridges PlayerController, PlayerWeapon, and PlayerHealth to the
    /// Animator on the MechWarrior model. Attach to the root Player
    /// GameObject (same level as PlayerController).
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PlayerAnimator : MonoBehaviour
    {
        // ====================================================================
        // Animator Parameter Names (must match the AnimatorController)
        // ====================================================================

        private static readonly int Speed           = Animator.StringToHash("Speed");
        private static readonly int IsFiring        = Animator.StringToHash("IsFiring");
        private static readonly int IsReloading     = Animator.StringToHash("IsReloading");
        private static readonly int DieTrigger      = Animator.StringToHash("Die");
        private static readonly int SpeedMultiplier = Animator.StringToHash("SpeedMultiplier");

        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("References")]
        [Tooltip("If null, auto-found on this GameObject.")]
        [SerializeField] private Animator animator;

        [Header("Tuning")]
        [Tooltip("Smoothing for speed parameter transitions.")]
        [SerializeField] private float speedDampTime = 0.1f;

        [Tooltip("Player's max move speed (must match PlayerController.moveSpeed). " +
                 "Used to normalize animation speed.")]
        [SerializeField] private float referenceSpeed = 7f;

        [Tooltip("Minimum animation speed multiplier when barely moving. " +
                 "Prevents the run cycle from freezing at tiny stick inputs.")]
        [Range(0.1f, 0.8f)]
        [SerializeField] private float minAnimSpeed = 0.35f;

        // ====================================================================
        // Internal
        // ====================================================================

        private PlayerController _controller;
        private PlayerWeapon _weapon;
        private PlayerHealth _health;
        private bool _deathTriggered;

        // Parameter existence flags — validated once in Awake/Start
        private bool _hasSpeed;
        private bool _hasIsFiring;
        private bool _hasIsReloading;
        private bool _hasDie;
        private bool _hasSpeedMultiplier;
        private bool _parametersValidated;

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            if (animator == null) animator = GetComponent<Animator>();
            _controller = GetComponent<PlayerController>();
            _weapon = GetComponent<PlayerWeapon>();
            _health = GetComponent<PlayerHealth>();
        }

        private void Start()
        {
            ValidateParameters();
        }

        private void OnEnable()
        {
            // Subscribe to events for one-shot animations
            if (_weapon != null)
            {
                _weapon.OnShot += HandleShot;
                _weapon.OnReloadStart += HandleReloadStart;
                _weapon.OnReloadComplete += HandleReloadComplete;
            }
            if (_health != null)
            {
                _health.OnDied += HandleDeath;
            }
        }

        private void OnDisable()
        {
            if (_weapon != null)
            {
                _weapon.OnShot -= HandleShot;
                _weapon.OnReloadStart -= HandleReloadStart;
                _weapon.OnReloadComplete -= HandleReloadComplete;
            }
            if (_health != null)
            {
                _health.OnDied -= HandleDeath;
            }
        }

        private void Update()
        {
            if (animator == null) return;
            if (_deathTriggered) return; // Stop updating after death
            if (!_parametersValidated) return; // Don't touch animator until validated

            // Drive blend tree speed from controller velocity
            float normalizedSpeed = 0f;
            if (_controller != null && referenceSpeed > 0f)
            {
                normalizedSpeed = _controller.CurrentSpeed / referenceSpeed;
                normalizedSpeed = Mathf.Clamp01(normalizedSpeed);
            }

            if (_hasSpeed)
            {
                animator.SetFloat(Speed, normalizedSpeed, speedDampTime, Time.deltaTime);
            }

            // Drive animation playback speed proportional to stick deflection.
            // When the player barely tilts the stick, the run/walk animation
            // plays slower so feet don't slide. At full stick → 1x speed.
            if (_hasSpeedMultiplier)
            {
                float multiplier = 1f;
                if (normalizedSpeed > 0.01f)
                {
                    // Map [0..1] velocity → [minAnimSpeed..1] playback rate
                    multiplier = Mathf.Lerp(minAnimSpeed, 1f, normalizedSpeed);
                }
                animator.SetFloat(SpeedMultiplier, multiplier);
            }

            // IsFiring is true while fire button is held AND weapon is actively shooting
            if (_hasIsFiring)
            {
                bool firing = false;
                if (_weapon != null && !_weapon.IsReloading)
                {
                    var uiManager = UI.ThresholdUIManager.Instance;
                    firing = uiManager != null && uiManager.IsFireHeld();
                }
                animator.SetBool(IsFiring, firing);
            }
        }

        // ====================================================================
        // Parameter Validation
        // ====================================================================

        /// <summary>
        /// Checks if the current AnimatorController has the expected parameters.
        /// Logs a single warning if any are missing, instead of spamming per-frame.
        /// </summary>
        private void ValidateParameters()
        {
            _parametersValidated = true;

            if (animator == null || animator.runtimeAnimatorController == null)
            {
                Debug.LogWarning("[PlayerAnimator] No AnimatorController assigned. " +
                    "Run Tools > THRESHOLD > Setup MechWarrior Player to configure.", this);
                _hasSpeed = false;
                _hasIsFiring = false;
                _hasIsReloading = false;
                _hasDie = false;
                return;
            }

            // Build a set of existing parameter hashes for fast lookup
            _hasSpeed = false;
            _hasIsFiring = false;
            _hasIsReloading = false;
            _hasDie = false;
            _hasSpeedMultiplier = false;

            foreach (var param in animator.parameters)
            {
                int hash = param.nameHash;
                if (hash == Speed)            _hasSpeed = true;
                else if (hash == IsFiring)         _hasIsFiring = true;
                else if (hash == IsReloading)      _hasIsReloading = true;
                else if (hash == DieTrigger)        _hasDie = true;
                else if (hash == SpeedMultiplier)   _hasSpeedMultiplier = true;
            }

            bool allPresent = _hasSpeed && _hasIsFiring && _hasIsReloading && _hasDie && _hasSpeedMultiplier;

            if (!allPresent)
            {
                string missing = "";
                if (!_hasSpeed) missing += "Speed, ";
                if (!_hasIsFiring) missing += "IsFiring, ";
                if (!_hasIsReloading) missing += "IsReloading, ";
                if (!_hasDie) missing += "Die, ";
                if (!_hasSpeedMultiplier) missing += "SpeedMultiplier, ";
                missing = missing.TrimEnd(',', ' ');

                Debug.LogWarning($"[PlayerAnimator] AnimatorController is missing parameters: [{missing}]. " +
                    $"Run Tools > THRESHOLD > Setup MechWarrior Player to rebuild the controller. " +
                    $"Animation for missing parameters will be skipped.", this);
            }
            else
            {
                Debug.Log("[PlayerAnimator] All animator parameters validated.", this);
            }
        }

        // ====================================================================
        // Event Handlers
        // ====================================================================

        private void HandleShot(Vector3 _)
        {
            // Shot event is already tracked via IsFiring bool
        }

        private void HandleReloadStart()
        {
            if (animator != null && _hasIsReloading)
                animator.SetBool(IsReloading, true);
        }

        private void HandleReloadComplete()
        {
            if (animator != null && _hasIsReloading)
                animator.SetBool(IsReloading, false);
        }

        private void HandleDeath()
        {
            if (animator != null && _hasDie && !_deathTriggered)
            {
                _deathTriggered = true;
                animator.SetTrigger(DieTrigger);
            }
        }

        // ====================================================================
        // Public API
        // ====================================================================

        /// <summary>
        /// Reset animation state for a new run.
        /// </summary>
        public void ResetAnimator()
        {
            _deathTriggered = false;

            // Re-validate in case the controller was swapped at runtime
            if (!_parametersValidated) ValidateParameters();

            if (animator != null)
            {
                if (_hasSpeed)       animator.SetFloat(Speed, 0f);
                if (_hasIsFiring)    animator.SetBool(IsFiring, false);
                if (_hasIsReloading) animator.SetBool(IsReloading, false);
                animator.Rebind();
                animator.Update(0f);
            }
        }
    }
}
