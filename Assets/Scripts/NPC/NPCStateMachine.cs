// ============================================================================
// NPCStateMachine.cs — 6-state NPC behaviour system driven by Gemini
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// States are transitioned by the NPC Brain Agent (agentic, not rule-based).
// Each state has distinct movement, combat, and animation behaviour.
// ALLIED state is one-way and is the game's signature defection feature.
// ============================================================================

using System;
using System.Collections;
using Threshold.Core;
using UnityEngine;
using UnityEngine.AI;

namespace Threshold.NPC
{
    // ========================================================================
    // Enums & Data
    // ========================================================================

    /// <summary>
    /// The 6 NPC behaviour states from the GDD. Transitions are decided
    /// by the NPC Brain Agent — NOT hard-coded if/else rules.
    /// </summary>
    public enum NPCState
    {
        PATROL,
        ATTACK,
        FLANK,
        SUPPRESS,
        RETREAT,
        ALLIED,
        DEAD
    }

    /// <summary>
    /// Snapshot of NPC state sent to the Brain Agent for reasoning.
    /// </summary>
    [Serializable]
    public class NPCSnapshot
    {
        public string npcId;
        public string archetypeType;
        public string currentState;
        public float healthPercent;
        public float posX;
        public float posY;
        public float posZ;
        public bool hasLineOfSight;
        public float distanceToPlayer;
        public bool isInCover;
        public float timeSinceLastStateChange;
    }

    /// <summary>
    /// Base stats per NPC archetype from the GDD.
    /// </summary>
    [Serializable]
    public struct NPCArchetypeStats
    {
        public float maxHealth;
        public float fireRate;       // shots per second
        public float accuracy;       // 0–1
        public float moveSpeed;      // NavMesh speed
        public float retreatHealth;  // health % to suggest retreat
        public float damage;         // per-shot damage to player

        public static NPCArchetypeStats Grunt => new()
        {
            maxHealth = 100f, fireRate = 1.0f, accuracy = 0.40f,
            moveSpeed = 3.5f, retreatHealth = 0.15f, damage = 8f
        };

        public static NPCArchetypeStats Flanker => new()
        {
            maxHealth = 80f, fireRate = 1.2f, accuracy = 0.50f,
            moveSpeed = 5.0f, retreatHealth = 0.20f, damage = 10f
        };

        public static NPCArchetypeStats Suppressor => new()
        {
            maxHealth = 120f, fireRate = 2.0f, accuracy = 0.25f,
            moveSpeed = 2.5f, retreatHealth = 0.15f, damage = 5f
        };

        public static NPCArchetypeStats Elite => new()
        {
            maxHealth = 250f, fireRate = 1.5f, accuracy = 0.60f,
            moveSpeed = 3.5f, retreatHealth = 0.10f, damage = 15f
        };

        public static NPCArchetypeStats Get(NPCArchetype type) => type switch
        {
            NPCArchetype.GRUNT => Grunt,
            NPCArchetype.FLANKER => Flanker,
            NPCArchetype.SUPPRESSOR => Suppressor,
            NPCArchetype.ELITE => Elite,
            _ => Grunt
        };
    }

    // ========================================================================
    // Main Component
    // ========================================================================

    /// <summary>
    /// MonoBehaviour attached to each NPC. Manages the 6-state machine,
    /// per-frame behaviour, combat, and movement via NavMeshAgent.
    /// State transitions are driven externally by the NPC Brain controller.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NPCStateMachine : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("Identity")]
        [Tooltip("Unique NPC identifier (set at spawn time).")]
        public string npcId;

        [Tooltip("NPC archetype — determines base stats.")]
        public NPCArchetype archetype = NPCArchetype.GRUNT;

        [Header("Patrol")]
        [Tooltip("Waypoints for PATROL state. Cycles through in order.")]
        public Transform[] patrolWaypoints;

        [Header("Combat")]
        [Tooltip("Muzzle transform for raycasting shots.")]
        public Transform muzzlePoint;

        [Tooltip("Max engagement range in meters.")]
        public float engagementRange = 25f;

        [Tooltip("Cover points available to this NPC (assigned by room).")]
        public Transform[] coverPoints;

        [Header("Auto-Engage")]
        [Tooltip("If true, NPCs in PATROL auto-switch to ATTACK when player is within range and visible.")]
        [SerializeField] private bool autoEngageOnSight = true;

        [Tooltip("Max distance to auto-detect and engage the player.")]
        [SerializeField] private float autoEngageRange = 15f;

        [Header("Visuals")]
        [Tooltip("Animator component on this NPC (or child). Auto-found if null.")]
        [SerializeField] private Animator animator;

        [Tooltip("Tracer color for NPC shots.")]
        [SerializeField] private Color npcTracerColor = new Color(1f, 0.15f, 0.1f, 0.9f);

        [Header("Defection VFX")]
        [Tooltip("Visual effect to play when NPC defects to ALLIED.")]
        public GameObject defectionVFX;

        [Header("Debug")]
        [SerializeField] private bool logStateChanges = true;

        // ====================================================================
        // Runtime State
        // ====================================================================

        public NPCState CurrentState { get; private set; } = NPCState.PATROL;
        public NPCArchetypeStats Stats { get; private set; }
        public float CurrentHealth { get; private set; }
        public float HealthPercent => Stats.maxHealth > 0 ? CurrentHealth / Stats.maxHealth : 0f;
        public bool IsDead => CurrentHealth <= 0f;
        public bool IsAllied => CurrentState == NPCState.ALLIED;
        public float TimeSinceStateChange => Time.time - _lastStateChangeTime;

        // Internal
        private NavMeshAgent _agent;
        private Transform _playerTarget;
        private Transform _alliedTarget; // Former allies to attack when ALLIED
        private float _lastFireTime;
        private float _lastStateChangeTime;
        private int _patrolIndex;
        private bool _hasLineOfSight;
        private float _distanceToPlayer;
        private Transform _currentCover;

        // State-specific modifiers
        private float _currentFireRate;
        private float _currentAccuracy;
        private float _currentMoveSpeed;

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            if (animator == null)
                animator = GetComponentInChildren<Animator>();
        }

        /// <summary>
        /// Initialize the NPC with its archetype and spawn configuration.
        /// Call once after instantiation.
        /// </summary>
        public void Initialize(string id, NPCArchetype type, Transform player,
                                Transform[] waypoints = null, Transform[] covers = null)
        {
            npcId = id;
            archetype = type;
            Stats = NPCArchetypeStats.Get(type);
            CurrentHealth = Stats.maxHealth;
            _playerTarget = player;
            if (waypoints != null) patrolWaypoints = waypoints;
            if (covers != null) coverPoints = covers;

            _agent.speed = Stats.moveSpeed;
            ApplyStateModifiers(NPCState.PATROL);
            _lastStateChangeTime = Time.time;
        }

        private void Update()
        {
            if (IsDead) return;

            UpdateSensors();
            UpdateAnimator();

            switch (CurrentState)
            {
                case NPCState.PATROL:   UpdatePatrol();   break;
                case NPCState.ATTACK:   UpdateAttack();   break;
                case NPCState.FLANK:    UpdateFlank();    break;
                case NPCState.SUPPRESS: UpdateSuppress(); break;
                case NPCState.RETREAT:  UpdateRetreat();  break;
                case NPCState.ALLIED:   UpdateAllied();   break;
            }
        }

        // ====================================================================
        // State Transitions (called by NPC Brain controller)
        // ====================================================================

        /// <summary>
        /// Transition to a new state. Called externally by the NPC Brain
        /// controller when the Gemini agent decides a state change.
        /// Handles OnExit → OnEnter lifecycle.
        /// </summary>
        public void SetState(NPCState newState)
        {
            if (IsDead) return;

            // C1 FIX: No-op if already in the requested state
            if (CurrentState == newState) return;

            // ALLIED is one-way — cannot leave once entered
            if (CurrentState == NPCState.ALLIED && newState != NPCState.ALLIED)
            {
                Debug.LogWarning($"[NPC {npcId}] Cannot leave ALLIED state. Ignoring transition to {newState}.");
                return;
            }

            NPCState oldState = CurrentState;
            OnStateExit(oldState);
            CurrentState = newState;
            _lastStateChangeTime = Time.time;
            ApplyStateModifiers(newState);
            OnStateEnter(newState);

            if (logStateChanges)
                Debug.Log($"[NPC {npcId}] {oldState} → {newState}");
        }

        private void OnStateExit(NPCState state)
        {
            switch (state)
            {
                case NPCState.SUPPRESS:
                    // Reset fire rate after suppression ends
                    break;
                case NPCState.RETREAT:
                    _currentCover = null;
                    break;
            }
        }

        private void OnStateEnter(NPCState state)
        {
            switch (state)
            {
                case NPCState.PATROL:
                    _agent.isStopped = false;
                    NavigateToNextWaypoint();
                    break;

                case NPCState.ATTACK:
                    _agent.isStopped = false;
                    break;

                case NPCState.FLANK:
                    _agent.isStopped = false;
                    NavigateToFlankPosition();
                    break;

                case NPCState.SUPPRESS:
                    // M4 FIX: Navigate to cover at base speed; stop on arrival in Update
                    if (_agent != null && _agent.isOnNavMesh)
                        _agent.speed = Stats.moveSpeed; // Temporarily use base speed to reach cover
                    NavigateToCover();
                    break;

                case NPCState.RETREAT:
                    _agent.isStopped = false;
                    NavigateToFurthestCover();
                    break;

                case NPCState.ALLIED:
                    TriggerDefection();
                    break;
            }
        }

        /// <summary>
        /// Apply stat modifiers based on the active state.
        /// Each state changes fire rate, accuracy, and movement speed.
        /// </summary>
        private void ApplyStateModifiers(NPCState state)
        {
            switch (state)
            {
                case NPCState.PATROL:
                    _currentFireRate = 0f; // Don't fire
                    _currentAccuracy = 0f;
                    _currentMoveSpeed = Stats.moveSpeed * 0.6f;
                    break;

                case NPCState.ATTACK:
                    _currentFireRate = Stats.fireRate;
                    _currentAccuracy = Stats.accuracy;
                    _currentMoveSpeed = Stats.moveSpeed * 0.4f; // Slow while firing
                    break;

                case NPCState.FLANK:
                    _currentFireRate = Stats.fireRate * 0.7f; // Less firing, more moving
                    _currentAccuracy = Stats.accuracy * 0.8f;
                    _currentMoveSpeed = Stats.moveSpeed * 1.3f; // Fast repositioning
                    break;

                case NPCState.SUPPRESS:
                    _currentFireRate = Stats.fireRate * 2.0f; // Double fire rate
                    _currentAccuracy = Stats.accuracy * 0.5f; // Half accuracy
                    _currentMoveSpeed = 0f; // Stationary in cover
                    break;

                case NPCState.RETREAT:
                    _currentFireRate = Stats.fireRate * 0.3f; // Occasional shots
                    _currentAccuracy = Stats.accuracy * 0.4f;
                    _currentMoveSpeed = Stats.moveSpeed * 1.2f;
                    break;

                case NPCState.ALLIED:
                    _currentFireRate = Stats.fireRate;
                    _currentAccuracy = Stats.accuracy * 0.9f;
                    _currentMoveSpeed = Stats.moveSpeed;
                    break;
            }

            if (_agent != null && _agent.isOnNavMesh)
                _agent.speed = _currentMoveSpeed;
        }

        // ====================================================================
        // Per-State Update Logic
        // ====================================================================

        private void UpdatePatrol()
        {
            // Auto-engage: switch to ATTACK when player is visible and in range
            if (autoEngageOnSight && _playerTarget != null &&
                _distanceToPlayer <= autoEngageRange && _hasLineOfSight)
            {
                SetState(NPCState.ATTACK);
                return;
            }

            // M3 FIX: Random wander if no waypoints assigned
            if (patrolWaypoints == null || patrolWaypoints.Length == 0)
            {
                if (_agent.isOnNavMesh && !_agent.pathPending && _agent.remainingDistance < 0.5f)
                {
                    Vector3 randomDir = UnityEngine.Random.insideUnitSphere * 4f;
                    randomDir.y = 0f;
                    Vector3 wanderTarget = transform.position + randomDir;
                    if (NavMesh.SamplePosition(wanderTarget, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                        _agent.SetDestination(hit.position);
                }
                return;
            }

            if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
                NavigateToNextWaypoint();
        }

        private void UpdateAttack()
        {
            if (_playerTarget == null) return;

            // Disengage: return to patrol if player is out of range or no LOS for too long
            if (_distanceToPlayer > engagementRange * 1.5f ||
                (!_hasLineOfSight && TimeSinceStateChange > 5f))
            {
                SetState(NPCState.PATROL);
                return;
            }

            // Face and slowly advance toward player
            FaceTarget(_playerTarget.position);

            if (_distanceToPlayer > engagementRange * 0.5f && _hasLineOfSight)
                _agent.SetDestination(_playerTarget.position);
            else if (!_hasLineOfSight)
                _agent.isStopped = true; // Don't path through walls
            else
                _agent.isStopped = true;

            TryFire(_playerTarget.position);
        }

        private void UpdateFlank()
        {
            if (_playerTarget == null) return;

            // Move to flank position, fire when in range
            if (!_agent.pathPending && _agent.remainingDistance < 1.5f)
            {
                FaceTarget(_playerTarget.position);
                TryFire(_playerTarget.position);

                // Reposition periodically
                if (TimeSinceStateChange > 3f && Time.time - _lastStateChangeTime > 3f)
                    NavigateToFlankPosition();
            }
        }

        private void UpdateSuppress()
        {
            if (_playerTarget == null) return;

            // M4 FIX: Once arrived at cover, stop moving and lock in place
            if (_agent.isOnNavMesh && !_agent.pathPending && _agent.remainingDistance < 1f)
            {
                _agent.isStopped = true;
                _agent.speed = 0f;
            }

            FaceTarget(_playerTarget.position);
            TryFire(_playerTarget.position); // High rate, low accuracy
        }

        private void UpdateRetreat()
        {
            if (_playerTarget == null) return;

            // Move away, occasional backward fire
            if (!_agent.pathPending && _agent.remainingDistance < 1f)
            {
                _agent.isStopped = true;
            }

            // Occasional shot while retreating
            if (Time.time - _lastFireTime > 1f / Mathf.Max(_currentFireRate, 0.1f))
            {
                FaceTarget(_playerTarget.position);
                TryFire(_playerTarget.position);
            }
        }

        private void UpdateAllied()
        {
            // Follow the player at a distance, attack former allies
            if (_playerTarget == null) return;

            float followDist = 4f;
            if (_distanceToPlayer > followDist)
            {
                _agent.isStopped = false;
                _agent.SetDestination(_playerTarget.position);
            }
            else
            {
                _agent.isStopped = true;
            }

            // Attack nearest non-allied NPC if we have a target
            if (_alliedTarget != null)
            {
                FaceTarget(_alliedTarget.position);
                TryFire(_alliedTarget.position);
            }
        }

        // ====================================================================
        // Sensors
        // ====================================================================

        private void UpdateSensors()
        {
            if (_playerTarget == null) return;

            _distanceToPlayer = Vector3.Distance(transform.position, _playerTarget.position);

            // Line of sight check — cast against everything except triggers
            // This ensures room walls (on any layer) properly block sight
            Vector3 origin = muzzlePoint != null ? muzzlePoint.position : transform.position + Vector3.up;
            Vector3 dir = (_playerTarget.position + Vector3.up) - origin;

            // Use default raycast (all layers) — if we hit something that ISN'T the player,
            // we don't have line of sight
            if (Physics.Raycast(origin, dir.normalized, out RaycastHit losHit, dir.magnitude,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                // Check if the thing we hit is the player (or player's child)
                _hasLineOfSight = losHit.collider.CompareTag("Player") ||
                    losHit.collider.GetComponentInParent<Threshold.Player.PlayerHealth>() != null;
            }
            else
            {
                // Nothing in the way
                _hasLineOfSight = true;
            }
        }

        // ====================================================================
        // Combat
        // ====================================================================

        private void TryFire(Vector3 targetPos)
        {
            if (_currentFireRate <= 0f) return;
            if (!_hasLineOfSight) return; // Can't fire through walls

            float interval = 1f / _currentFireRate;
            if (Time.time - _lastFireTime < interval) return;
            if (_distanceToPlayer > engagementRange) return;

            _lastFireTime = Time.time;

            // Accuracy roll: determine if shot hits
            bool hit = UnityEngine.Random.value < _currentAccuracy;

            // Raycast to target
            Vector3 origin = muzzlePoint != null ? muzzlePoint.position : transform.position + Vector3.up;
            Vector3 dir = (targetPos + Vector3.up) - origin;

            // Apply inaccuracy spread
            if (!hit)
            {
                float spread = (1f - _currentAccuracy) * 5f;
                dir += new Vector3(
                    UnityEngine.Random.Range(-spread, spread),
                    UnityEngine.Random.Range(-spread * 0.3f, spread * 0.3f),
                    UnityEngine.Random.Range(-spread, spread)
                );
            }

            Vector3 endPoint = origin + dir.normalized * engagementRange;

            if (Physics.Raycast(origin, dir.normalized, out RaycastHit hitInfo, engagementRange))
            {
                endPoint = hitInfo.point;

                // Apply damage to player via PlayerHealth
                var playerHealth = hitInfo.collider.GetComponent<Threshold.Player.PlayerHealth>();
                if (playerHealth != null)
                {
                    playerHealth.TakeDamage(Stats.damage);
                }
            }

            // Spawn red laser tracer
            Threshold.Player.ProjectileTracer.Spawn(origin, endPoint, npcTracerColor);
        }

        // ====================================================================
        // Navigation
        // ====================================================================

        private void NavigateToNextWaypoint()
        {
            if (patrolWaypoints == null || patrolWaypoints.Length == 0) return;

            _patrolIndex = (_patrolIndex + 1) % patrolWaypoints.Length;
            if (patrolWaypoints[_patrolIndex] != null && _agent.isOnNavMesh)
                _agent.SetDestination(patrolWaypoints[_patrolIndex].position);
        }

        private void NavigateToFlankPosition()
        {
            if (_playerTarget == null) return;

            // Calculate a position 90° off the player-NPC axis
            Vector3 toPlayer = (_playerTarget.position - transform.position).normalized;
            Vector3 perpendicular = Vector3.Cross(toPlayer, Vector3.up).normalized;

            // Randomly pick left or right flank
            float side = UnityEngine.Random.value > 0.5f ? 1f : -1f;
            float flankDist = UnityEngine.Random.Range(6f, 10f);
            Vector3 flankPos = _playerTarget.position + perpendicular * side * flankDist;

            if (NavMesh.SamplePosition(flankPos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            {
                _agent.SetDestination(hit.position);
            }
        }

        private void NavigateToCover()
        {
            Transform best = FindNearestCover();
            if (best != null && _agent.isOnNavMesh)
            {
                _currentCover = best;
                _agent.SetDestination(best.position);
                _agent.isStopped = false;
            }
        }

        private void NavigateToFurthestCover()
        {
            if (_playerTarget == null) return;

            // M1 FIX: If no cover points, fall back to moving directly away from player
            if (coverPoints == null || coverPoints.Length == 0)
            {
                Vector3 awayDir = (transform.position - _playerTarget.position).normalized;
                Vector3 retreatTarget = transform.position + awayDir * 8f;
                if (_agent.isOnNavMesh && NavMesh.SamplePosition(retreatTarget, out NavMeshHit fallbackHit, 5f, NavMesh.AllAreas))
                    _agent.SetDestination(fallbackHit.position);
                return;
            }

            Transform furthest = null;
            float maxDist = 0f;

            foreach (var cp in coverPoints)
            {
                if (cp == null) continue;
                float dist = Vector3.Distance(cp.position, _playerTarget.position);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    furthest = cp;
                }
            }

            if (furthest != null && _agent.isOnNavMesh)
            {
                _currentCover = furthest;
                _agent.SetDestination(furthest.position);
            }
        }

        private Transform FindNearestCover()
        {
            if (coverPoints == null || coverPoints.Length == 0) return null;

            Transform nearest = null;
            float minDist = float.MaxValue;

            foreach (var cp in coverPoints)
            {
                if (cp == null) continue;
                float dist = Vector3.Distance(transform.position, cp.position);
                if (dist < minDist) { minDist = dist; nearest = cp; }
            }
            return nearest;
        }

        private void FaceTarget(Vector3 target)
        {
            Vector3 dir = (target - transform.position);
            dir.y = 0;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(dir), Time.deltaTime * 8f);
        }

        // ====================================================================
        // Defection
        // ====================================================================

        private void TriggerDefection()
        {
            // Change tag so allies don't target this NPC.
            // Tags must be defined in Project Settings → Tags & Layers.
            // If "AlliedNPC" tag doesn't exist yet, skip gracefully.
            try { gameObject.tag = "AlliedNPC"; }
            catch (UnityException)
            {
                Debug.LogWarning($"[NPC {npcId}] Tag 'AlliedNPC' not defined in Project Settings. " +
                                 "Add it via Edit → Project Settings → Tags and Layers. Using 'Untagged'.");
            }

            // Change layer so physics/targeting can distinguish allied NPCs.
            // Layers must be defined in Project Settings → Tags & Layers.
            int alliedLayer = LayerMask.NameToLayer("AlliedNPC");
            if (alliedLayer >= 0)
            {
                gameObject.layer = alliedLayer;
            }
            else
            {
                Debug.LogWarning($"[NPC {npcId}] Layer 'AlliedNPC' not defined in Project Settings. " +
                                 "Add it via Edit → Project Settings → Tags and Layers.");
            }

            // Play VFX
            if (defectionVFX != null)
                Instantiate(defectionVFX, transform.position + Vector3.up, Quaternion.identity);

            Debug.Log($"[NPC {npcId}] ★ DEFECTED — now allied with the player.");
        }

        /// <summary>
        /// Set the target for an ALLIED NPC to attack (former ally).
        /// Called by the NPC Brain controller.
        /// </summary>
        public void SetAlliedTarget(Transform target)
        {
            _alliedTarget = target;
        }

        // ====================================================================
        // Damage
        // ====================================================================

        /// <summary>
        /// Apply damage to this NPC. Returns true if NPC dies.
        /// </summary>
        public bool TakeDamage(float amount)
        {
            if (IsDead) return false;

            CurrentHealth -= amount;
            if (CurrentHealth <= 0f)
            {
                CurrentHealth = 0f;
                OnDeath();
                return true;
            }
            return false;
        }

        private void OnDeath()
        {
            // Stop NavMeshAgent immediately
            if (_agent != null && _agent.isOnNavMesh)
                _agent.isStopped = true;
            if (_agent != null)
                _agent.enabled = false;

            // Disable ALL colliders so raycasts pass through corpse
            foreach (var col in GetComponentsInChildren<Collider>())
                col.enabled = false;

            // Stop animator
            if (animator != null)
                animator.enabled = false;

            if (logStateChanges)
                Debug.Log($"[NPC {npcId}] DEAD");

            // Play death sequence then destroy
            StartCoroutine(DeathSequence());
        }

        private IEnumerator DeathSequence()
        {
            float duration = 0.6f;
            float elapsed = 0f;
            Vector3 startScale = transform.localScale;
            Vector3 startPos = transform.position;

            // Tint all renderers dark
            var renderers = GetComponentsInChildren<Renderer>();
            MaterialPropertyBlock mpb = new();
            mpb.SetColor("_Color", new Color(0.15f, 0.05f, 0.05f, 1f));
            foreach (var r in renderers)
                r.SetPropertyBlock(mpb);

            // Shrink + sink
            while (elapsed < duration)
            {
                float t = elapsed / duration;
                transform.localScale = Vector3.Lerp(startScale, Vector3.zero, t * t);
                transform.position = startPos + Vector3.down * (t * 0.5f);
                elapsed += Time.deltaTime;
                yield return null;
            }

            Destroy(gameObject);
        }

        // ====================================================================
        // Animator Sync
        // ====================================================================

        /// <summary>
        /// Drives animator parameters from NavMeshAgent velocity.
        /// Works with the RoboCop controller (Walk_Anim bool).
        /// </summary>
        private void UpdateAnimator()
        {
            if (animator == null) return;

            // Walk_Anim = true when moving
            bool isMoving = _agent != null && _agent.enabled &&
                            _agent.velocity.sqrMagnitude > 0.1f;
            animator.SetBool("Walk_Anim", isMoving);
        }

        // ====================================================================
        // Snapshot for Brain Agent
        // ====================================================================

        /// <summary>
        /// Returns a snapshot of this NPC's current state for the Brain Agent's
        /// input payload. Called every 20 seconds by the brain controller.
        /// </summary>
        public NPCSnapshot GetSnapshot()
        {
            return new NPCSnapshot
            {
                npcId = npcId,
                archetypeType = archetype.ToString(),
                currentState = CurrentState.ToString(),
                healthPercent = HealthPercent,
                posX = transform.position.x,
                posY = transform.position.y,
                posZ = transform.position.z,
                hasLineOfSight = _hasLineOfSight,
                distanceToPlayer = _distanceToPlayer,
                isInCover = _currentCover != null &&
                    Vector3.Distance(transform.position, _currentCover.position) < 1.5f,
                timeSinceLastStateChange = TimeSinceStateChange
            };
        }

        // ====================================================================
        // Editor Gizmos
        // ====================================================================

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            // Engagement range
            Gizmos.color = CurrentState == NPCState.ALLIED
                ? new Color(0, 1, 0, 0.15f)
                : new Color(1, 0, 0, 0.15f);
            Gizmos.DrawWireSphere(transform.position, engagementRange);

            // State label
            UnityEditor.Handles.Label(transform.position + Vector3.up * 2.5f,
                $"{npcId}\n{archetype} | {CurrentState}\nHP: {HealthPercent:P0}");

            // Line of sight
            if (_playerTarget != null)
            {
                Gizmos.color = _hasLineOfSight ? Color.yellow : Color.gray;
                Vector3 origin = muzzlePoint != null ? muzzlePoint.position : transform.position + Vector3.up;
                Gizmos.DrawLine(origin, _playerTarget.position + Vector3.up);
            }
        }
#endif
    }
}
