// ============================================================================
// PlayerWeapon.cs — Hitscan weapon system with ammo and auto-reload
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Fires in the direction the player is currently facing (joystick-driven).
// Uses hitscan (raycast) matching the NPC TryFire() pattern.
// Integrates with GameplayHUD for ammo display, PlayerMetricsTracker for
// accuracy tracking, and NPCStateMachine.TakeDamage() for kills.
// ============================================================================

using System;
using UnityEngine;

namespace Threshold.Player
{
    /// <summary>
    /// Hitscan weapon attached to the Player. Fires in the player's forward
    /// direction (controlled by joystick). Handles magazine, reload, damage,
    /// and all metrics reporting.
    /// </summary>
    public class PlayerWeapon : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("Weapon Stats")]
        [Tooltip("Damage per shot.")]
        [SerializeField] private float damagePerShot = 25f;

        [Tooltip("Shots per second when holding fire.")]
        [SerializeField] private float fireRate = 5f;

        [Tooltip("Maximum range of hitscan raycast.")]
        [SerializeField] private float range = 30f;

        [Header("Ammo")]
        [Tooltip("Magazine size.")]
        [SerializeField] private int magazineSize = 30;

        [Tooltip("Reload time in seconds.")]
        [SerializeField] private float reloadTime = 1.5f;

        [Tooltip("Auto-reload when magazine is empty.")]
        [SerializeField] private bool autoReload = true;

        [Header("Visual")]
        [Tooltip("Muzzle point transform. If null, uses player position + up.")]
        [SerializeField] private Transform muzzlePoint;

        [Tooltip("Optional muzzle flash prefab (particle system).")]
        [SerializeField] private GameObject muzzleFlashPrefab;

        [Header("Laser Tracer")]
        [Tooltip("If true, spawns a ProjectileTracer on each shot.")]
        [SerializeField] private bool useTracers = true;

        [Tooltip("Full laser beam appearance config — color, size, fade, glow.")]
        [SerializeField] private TracerConfig tracerConfig = TracerConfig.Default;

        [Header("Layers")]
        [Tooltip("Layers the weapon can hit.")]
        [SerializeField] private LayerMask hitLayers = ~0; // Everything by default

        // ====================================================================
        // Events
        // ====================================================================

        /// <summary>Fired when a shot is fired. Arg = muzzle world position.</summary>
        public event Action<Vector3> OnShot;

        /// <summary>Fired when an NPC is killed. Arg = NPC transform.</summary>
        public event Action<Transform> OnKill;

        /// <summary>Fired when reload starts.</summary>
        public event Action OnReloadStart;

        /// <summary>Fired when reload completes.</summary>
        public event Action OnReloadComplete;

        // ====================================================================
        // State
        // ====================================================================

        /// <summary>Current ammo in magazine.</summary>
        public int CurrentAmmo { get; private set; }

        /// <summary>True while reloading.</summary>
        public bool IsReloading { get; private set; }

        /// <summary>Ammo as 0–1 fraction.</summary>
        public float AmmoPercent => magazineSize > 0 ? (float)CurrentAmmo / magazineSize : 0f;

        // ====================================================================
        // Singleton
        // ====================================================================

        public static PlayerWeapon Instance { get; private set; }

        // ====================================================================
        // Internal
        // ====================================================================

        private float _lastFireTime;
        private float _reloadEndTime;

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            CurrentAmmo = magazineSize;
            SyncAmmoHUD();
        }

        private void Update()
        {
            // Handle reload timer
            if (IsReloading)
            {
                if (Time.time >= _reloadEndTime)
                    FinishReload();
                return; // Can't fire while reloading
            }

            // Check fire input
            var uiManager = UI.ThresholdUIManager.Instance;
            if (uiManager != null && uiManager.IsFireHeld())
            {
                TryFire();
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ====================================================================
        // Public API
        // ====================================================================

        /// <summary>Start a manual reload.</summary>
        public void StartReload()
        {
            if (IsReloading || CurrentAmmo >= magazineSize) return;

            IsReloading = true;
            _reloadEndTime = Time.time + reloadTime;
            OnReloadStart?.Invoke();

            Debug.Log("[PlayerWeapon] Reloading...");
        }

        /// <summary>Reset ammo to full (e.g. on new run).</summary>
        public void ResetAmmo()
        {
            CurrentAmmo = magazineSize;
            IsReloading = false;
            SyncAmmoHUD();
        }

        // ====================================================================
        // Fire Logic
        // ====================================================================

        private void TryFire()
        {
            // Rate limiting
            float interval = 1f / fireRate;
            if (Time.time - _lastFireTime < interval) return;

            // Check ammo
            if (CurrentAmmo <= 0)
            {
                if (autoReload) StartReload();
                return;
            }

            _lastFireTime = Time.time;
            CurrentAmmo--;

            // Muzzle position and direction
            Vector3 origin = muzzlePoint != null
                ? muzzlePoint.position
                : transform.position + Vector3.up * 0.8f;
            Vector3 direction = transform.forward;

            // Report shot fired to metrics
            PlayerMetricsTracker.Instance?.OnShotFired();
            PlayerMetricsTracker.Instance?.OnAmmoUsed();

            // Perform hitscan raycast
            bool hitSomething = Physics.Raycast(origin, direction, out RaycastHit hitInfo,
                range, hitLayers);

            Vector3 endPoint = hitSomething ? hitInfo.point : origin + direction * range;

            // Spawn tracer visual
            if (useTracers)
            {
                ProjectileTracer.Spawn(origin, endPoint, tracerConfig);
            }

            // Spawn muzzle flash
            if (muzzleFlashPrefab != null)
            {
                var flash = Instantiate(muzzleFlashPrefab, origin,
                    Quaternion.LookRotation(direction));
                Destroy(flash, 0.5f);
            }

            // Process hit
            if (hitSomething)
            {
                ProcessHit(hitInfo);
            }

            // Update HUD
            SyncAmmoHUD();

            // Fire event
            OnShot?.Invoke(origin);

            // Auto-reload on empty
            if (CurrentAmmo <= 0 && autoReload)
            {
                StartReload();
            }
        }

        private void ProcessHit(RaycastHit hitInfo)
        {
            // Check if we hit an NPC
            var npc = hitInfo.collider.GetComponent<NPC.NPCStateMachine>();
            if (npc != null)
            {
                // Don't damage allied NPCs
                if (npc.IsAllied) return;

                // Don't damage already dead NPCs
                if (npc.IsDead) return;

                // Report hit to metrics
                PlayerMetricsTracker.Instance?.OnShotHit();

                // Apply damage
                bool killed = npc.TakeDamage(damagePerShot);

                if (killed)
                {
                    // Report kill
                    PlayerMetricsTracker.Instance?.OnEnemyKilled();
                    UI.ThresholdUIManager.Instance?.RecordKill();

                    OnKill?.Invoke(npc.transform);

                    Debug.Log($"[PlayerWeapon] Killed {npc.npcId} ({npc.archetype})");
                }
            }
            else
            {
                // Hit environment or other — still counts as a "hit" for accuracy
                // if it was aimed at something (not tracking env hits as accuracy)
            }
        }

        // ====================================================================
        // Reload
        // ====================================================================

        private void FinishReload()
        {
            IsReloading = false;
            CurrentAmmo = magazineSize;
            SyncAmmoHUD();
            OnReloadComplete?.Invoke();

            Debug.Log("[PlayerWeapon] Reload complete.");
        }

        // ====================================================================
        // HUD Sync
        // ====================================================================

        private void SyncAmmoHUD()
        {
            UI.ThresholdUIManager.Instance?.UpdateAmmo(CurrentAmmo, magazineSize);

            // Also update live stats for metrics
            PlayerMetricsTracker.Instance?.UpdateLiveStats(
                PlayerHealth.Instance != null ? PlayerHealth.Instance.HealthPercent : 1f,
                AmmoPercent);
        }
    }
}
