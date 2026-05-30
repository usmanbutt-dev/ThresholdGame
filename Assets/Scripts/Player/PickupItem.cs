// ============================================================================
// PickupItem.cs — Universal collectable item system
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Attach this to any 3D model to make it a pickup. Configurable for health,
// ammo, or custom effects. Auto-creates trigger collider and optional
// bobbing/rotation animation.
// ============================================================================

using System.Collections;
using UnityEngine;

namespace Threshold.Player
{
    /// <summary>
    /// Universal pickup item. Triggers on player collision, applies effect,
    /// plays SFX/VFX, then self-destructs.
    /// </summary>
    public class PickupItem : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("Pickup Type")]
        [Tooltip("What this pickup does when collected.")]
        [SerializeField] private PickupType pickupType = PickupType.Medkit;

        [Header("Effect Values")]
        [Tooltip("Health restored (for Medkit/Bandage). 0 = full heal for Medkit.")]
        [SerializeField] private float healthAmount = 0f;

        [Tooltip("Ammo added (for Bullets/AmmoBox). Number of rounds.")]
        [SerializeField] private int ammoAmount = 10;

        [Header("Audio")]
        [Tooltip("Sound played on pickup.")]
        [SerializeField] private AudioClip pickupSFX;

        [Tooltip("Pickup sound volume.")]
        [Range(0f, 1f)]
        [SerializeField] private float sfxVolume = 0.8f;

        [Header("Visual Animation")]
        [Tooltip("If true, item bobs up and down.")]
        [SerializeField] private bool enableBobbing = true;

        [Tooltip("Bob amplitude in world units.")]
        [SerializeField] private float bobAmplitude = 0.15f;

        [Tooltip("Bob speed (cycles per second).")]
        [SerializeField] private float bobSpeed = 2f;

        [Tooltip("If true, item rotates slowly.")]
        [SerializeField] private bool enableRotation = true;

        [Tooltip("Rotation speed in degrees per second.")]
        [SerializeField] private float rotationSpeed = 90f;

        [Header("Trigger")]
        [Tooltip("Radius of the trigger collider for auto-pickup.")]
        [SerializeField] private float triggerRadius = 1.5f;

        [Tooltip("Layer mask — only objects on these layers can pick this up.")]
        [SerializeField] private LayerMask pickupLayers = ~0;

        // ====================================================================
        // Internal
        // ====================================================================

        private Vector3 _startPos;
        private bool _collected;
        private Transform _visualChild;

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            // Validate pickupType enum — catches stale serialized values after enum changes
            if (!System.Enum.IsDefined(typeof(PickupType), pickupType))
            {
                Debug.LogWarning($"[Pickup] {gameObject.name}: Invalid pickupType={pickupType}! " +
                    $"Resetting to Medkit. Re-save the prefab in Inspector to fix permanently.");
                pickupType = PickupType.Medkit;
            }

            // Ensure we have a trigger collider for auto-pickup
            var existingCollider = GetComponent<Collider>();
            if (existingCollider == null || !existingCollider.isTrigger)
            {
                var sphere = gameObject.AddComponent<SphereCollider>();
                sphere.isTrigger = true;
                sphere.radius = triggerRadius;
                sphere.center = Vector3.up * 0.5f;
            }

            // Need a Rigidbody for OnTriggerEnter (kinematic, no gravity)
            var rb = GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            // Cache the visual model (first child, or self if no children)
            _visualChild = transform.childCount > 0 ? transform.GetChild(0) : transform;
        }

        private void Start()
        {
            _startPos = transform.position;

            // Apply default values based on pickup type
            ApplyDefaults();
        }

        private void Update()
        {
            if (_collected) return;

            // Bobbing animation
            if (enableBobbing)
            {
                float yOffset = Mathf.Sin(Time.time * bobSpeed * Mathf.PI * 2f) * bobAmplitude;
                transform.position = _startPos + Vector3.up * yOffset;
            }

            // Rotation animation
            if (enableRotation && _visualChild != null)
            {
                _visualChild.Rotate(Vector3.up, rotationSpeed * Time.deltaTime, Space.World);
            }
        }

        // ====================================================================
        // Trigger Detection
        // ====================================================================

        private void OnTriggerEnter(Collider other)
        {
            if (_collected) return;

            // Find the player root — the collider might be on a child object,
            // so check both the collider's object and all parents for the Player tag
            GameObject playerRoot = null;

            if (other.CompareTag("Player"))
            {
                playerRoot = other.gameObject;
            }
            else
            {
                // Walk up the hierarchy to find the Player-tagged root
                Transform parent = other.transform.parent;
                while (parent != null)
                {
                    if (parent.CompareTag("Player"))
                    {
                        playerRoot = parent.gameObject;
                        break;
                    }
                    parent = parent.parent;
                }
            }

            // Last resort: check if any player component exists on this hierarchy
            if (playerRoot == null)
            {
                var ph = other.GetComponentInParent<PlayerHealth>();
                if (ph != null) playerRoot = ph.gameObject;
            }

            if (playerRoot == null)
            {
                Debug.Log($"[Pickup] {gameObject.name}: Trigger hit by '{other.name}' but no Player found in hierarchy.");
                return;
            }

            Debug.Log($"[Pickup] {gameObject.name}: Player detected ({playerRoot.name}), attempting {pickupType}...");

            // Try to apply effect
            bool success = TryApplyEffect(playerRoot);

            if (success)
            {
                _collected = true;

                // Play pickup SFX
                if (pickupSFX != null)
                {
                    AudioSource.PlayClipAtPoint(pickupSFX, transform.position, sfxVolume);
                }

                Debug.Log($"[Pickup] ✓ {pickupType} collected by {playerRoot.name}");

                // Destroy the pickup
                Destroy(gameObject);
            }
            else
            {
                Debug.Log($"[Pickup] ✗ {pickupType} NOT collected — player may be full or type invalid.");
            }
        }

        // ====================================================================
        // Effect Application
        // ====================================================================

        private bool TryApplyEffect(GameObject player)
        {
            switch (pickupType)
            {
                case PickupType.Medkit:
                    return TryHeal(player, true);

                case PickupType.Bandage:
                    return TryHeal(player, false);

                case PickupType.AmmoBox:
                    return TryAddAmmo(player);

                default:
                    Debug.LogError($"[Pickup] Unknown pickupType={(int)pickupType} on {gameObject.name}! " +
                        $"Re-assign the Pickup Type in the Inspector.");
                    return false;
            }
        }

        private bool TryHeal(GameObject player, bool fullHeal)
        {
            var health = player.GetComponent<PlayerHealth>();
            if (health == null) health = player.GetComponentInParent<PlayerHealth>();
            if (health == null || health.IsDead) return false;

            // Don't pick up if already full health
            if (health.HealthPercent >= 1f) return false;

            if (fullHeal)
            {
                health.Heal(health.MaxHealth); // Full heal
            }
            else
            {
                float amount = healthAmount > 0 ? healthAmount : 25f; // Default bandage: 25 HP
                health.Heal(amount);
            }

            return true;
        }

        private bool TryAddAmmo(GameObject player)
        {
            var weapon = player.GetComponent<PlayerWeapon>();
            if (weapon == null) weapon = player.GetComponentInParent<PlayerWeapon>();
            if (weapon == null) return false;

            // Don't pick up if reserve ammo is already full
            if (weapon.ReserveAmmo >= weapon.MaxReserveAmmo) return false;

            int amount = ammoAmount > 0 ? ammoAmount : 10; // Default
            weapon.AddAmmo(amount);

            return true;
        }

        // ====================================================================
        // Defaults
        // ====================================================================

        private void ApplyDefaults()
        {
            // Set sensible defaults if values weren't configured
            switch (pickupType)
            {
                case PickupType.Medkit:
                    // healthAmount doesn't matter — full heal
                    break;
                case PickupType.Bandage:
                    if (healthAmount <= 0) healthAmount = 25f;
                    break;
                case PickupType.AmmoBox:
                    if (ammoAmount <= 0) ammoAmount = 30;
                    break;
            }
        }
    }

    // ========================================================================
    // Enum
    // ========================================================================

    public enum PickupType
    {
        Medkit,     // Full heal
        Bandage,    // Partial heal (25 HP default)
        AmmoBox     // Ammo box (30 rounds default)
    }
}
