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

        [Tooltip("Max reserve ammo pool the player can carry.")]
        [SerializeField] private int maxReserveAmmo = 120;

        [Tooltip("Starting reserve ammo.")]
        [SerializeField] private int startingReserveAmmo = 90;

        [Tooltip("Reload time in seconds.")]
        [SerializeField] private float reloadTime = 1.5f;

        [Tooltip("Auto-reload when magazine is empty.")]
        [SerializeField] private bool autoReload = true;

        [Header("Visual")]
        [Tooltip("Muzzle point transform. If null, uses player position + up.")]
        [SerializeField] private Transform muzzlePoint;

        [Tooltip("Optional muzzle flash prefab (particle system).")]
        [SerializeField] private GameObject muzzleFlashPrefab;

        [Header("Bullet Tracer")]
        [Tooltip("If true, spawns a ProjectileTracer on each shot.")]
        [SerializeField] private bool useTracers = true;

        [Tooltip("Bullet tracer appearance — color, size, length, speed, glow.")]
        [SerializeField] private TracerConfig tracerConfig = TracerConfig.Default;

        [Header("Layers")]
        [Tooltip("Layers the weapon can hit.")]
        [SerializeField] private LayerMask hitLayers = ~0; // Everything by default

        [Header("Audio SFX")]
        [Tooltip("Sound effect played on each shot.")]
        [SerializeField] private AudioClip shootSFX;

        [Tooltip("Sound effect played when reload starts.")]
        [SerializeField] private AudioClip reloadSFX;

        [Tooltip("Volume for weapon sound effects.")]
        [Range(0f, 1f)]
        [SerializeField] private float sfxVolume = 0.6f;

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

        /// <summary>Current spare ammo in reserve.</summary>
        public int ReserveAmmo { get; private set; }

        /// <summary>Max reserve ammo limit.</summary>
        public int MaxReserveAmmo => maxReserveAmmo;

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
        private AudioSource _audioSource;

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
            ReserveAmmo = startingReserveAmmo;
            SyncAmmoHUD();

            // Cache or add an AudioSource for weapon SFX
            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
                _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f; // 2D sound for the player's own weapon
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

            // Check fire input (touch aim stick OR PC right-click)
            bool wantsFire = false;
            var uiManager = UI.ThresholdUIManager.Instance;
            if (uiManager != null && uiManager.IsFireHeld())
                wantsFire = true;

            // PC fallback: PlayerController.IsAiming covers mouse right-click
            if (!wantsFire && PlayerController.Instance != null && PlayerController.Instance.IsAiming)
                wantsFire = true;

            if (wantsFire)
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

            // Play reload sound
            if (reloadSFX != null && _audioSource != null)
                _audioSource.PlayOneShot(reloadSFX, sfxVolume);

            Debug.Log("[PlayerWeapon] Reloading...");
        }

        /// <summary>Reset ammo to full (e.g. on new run).</summary>
        public void ResetAmmo()
        {
            CurrentAmmo = magazineSize;
            ReserveAmmo = startingReserveAmmo;
            IsReloading = false;
            SyncAmmoHUD();
        }

        /// <summary>Add ammo rounds (from pickups) directly to the reserve pool.</summary>
        public void AddAmmo(int amount)
        {
            if (amount <= 0) return;
            ReserveAmmo = Mathf.Min(ReserveAmmo + amount, maxReserveAmmo);
            SyncAmmoHUD();
            Debug.Log($"[PlayerWeapon] Added {amount} to reserve. Now: {ReserveAmmo}/{maxReserveAmmo}");
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
                if (autoReload && ReserveAmmo > 0) StartReload();
                return;
            }

            _lastFireTime = Time.time;
            CurrentAmmo--;

            // Play shoot sound
            if (shootSFX != null && _audioSource != null)
                _audioSource.PlayOneShot(shootSFX, sfxVolume);

            // Physics origin: Start raycast from player's core (safely behind gun tip to prevent point-blank clipping)
            Vector3 physicsOrigin = transform.position + Vector3.up * 0.8f;
            Vector3 direction = transform.forward;

            // Visual origin: Spawn tracers, flashes, and fire events from the actual gun tip (muzzlePoint)
            Vector3 visualOrigin = muzzlePoint != null
                ? muzzlePoint.position
                : transform.position + Vector3.up * 0.8f;

            // Report shot fired to metrics
            PlayerMetricsTracker.Instance?.OnShotFired();
            PlayerMetricsTracker.Instance?.OnAmmoUsed();

            // Perform hitscan raycast starting from the player's core
            bool hitSomething = Physics.Raycast(physicsOrigin, direction, out RaycastHit hitInfo,
                range, hitLayers);

            Vector3 endPoint = hitSomething ? hitInfo.point : visualOrigin + direction * range;

            // Spawn tracer visual starting from muzzle tip
            if (useTracers)
            {
                ProjectileTracer.Spawn(visualOrigin, endPoint, tracerConfig);
            }

            // Spawn muzzle flash at muzzle tip
            if (muzzleFlashPrefab != null)
            {
                var flash = Instantiate(muzzleFlashPrefab, visualOrigin,
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
            OnShot?.Invoke(visualOrigin);

            // Auto-reload on empty
            if (CurrentAmmo <= 0 && autoReload && ReserveAmmo > 0)
            {
                StartReload();
            }
        }

        private void ProcessHit(RaycastHit hitInfo)
        {
            // Check if we hit an NPC (check self and parents for compound collider setups)
            var npc = hitInfo.collider.GetComponentInParent<NPC.NPCStateMachine>();
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

            int needed = magazineSize - CurrentAmmo;
            int transfer = Mathf.Min(needed, ReserveAmmo);

            CurrentAmmo += transfer;
            ReserveAmmo -= transfer;

            SyncAmmoHUD();
            OnReloadComplete?.Invoke();

            Debug.Log($"[PlayerWeapon] Reload complete. Magazine: {CurrentAmmo}/{magazineSize}, Reserve: {ReserveAmmo}");
        }

        // ====================================================================
        // HUD Sync
        // ====================================================================

        private void SyncAmmoHUD()
        {
            UI.ThresholdUIManager.Instance?.UpdateAmmo(CurrentAmmo, ReserveAmmo);

            // Also update live stats for metrics
            PlayerMetricsTracker.Instance?.UpdateLiveStats(
                PlayerHealth.Instance != null ? PlayerHealth.Instance.HealthPercent : 1f,
                AmmoPercent);
        }
    }
}
