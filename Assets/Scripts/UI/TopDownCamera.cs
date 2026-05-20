// ============================================================================
// TopDownCamera.cs — Isometric follow camera for portrait mobile shooter
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Optimized for 1080×1920 portrait display with isometric 3/4 view.
// Features: smooth follow, joystick-aware look-ahead, room-aware framing,
// dynamic zoom, screen-shake with decay, and smooth room transitions.
// ============================================================================

using System.Collections;
using UnityEngine;

namespace Threshold.UI
{
    /// <summary>
    /// Isometric follow camera designed for portrait mobile roguelite shooter.
    /// Attach to the Main Camera GameObject.
    /// </summary>
    public class TopDownCamera : MonoBehaviour
    {
        // ====================================================================
        // Configuration — Isometric View
        // ====================================================================

        [Header("Target")]
        [Tooltip("The player transform to follow. Auto-finds 'Player' tag if null.")]
        public Transform target;

        [Header("Isometric View")]
        [Tooltip("Camera pitch angle (X rotation). 55–65° gives classic isometric feel.")]
        [Range(35f, 85f)]
        public float pitchAngle = 60f;

        [Tooltip("Camera yaw angle (Y rotation). 45° for true isometric, 0° for straight-on.")]
        [Range(-45f, 45f)]
        public float yawAngle = 0f;

        [Tooltip("Height above the player.")]
        public float height = 14f;

        [Tooltip("Forward offset (shifts view ahead of player in camera's forward direction).")]
        public float forwardOffset = 1f;

        [Header("Orthographic (Portrait Optimized)")]
        [Tooltip("If true, uses orthographic projection for clean isometric look.")]
        public bool useOrthographic = true;

        [Tooltip("Base orthographic size. Tuned for 1080x1920 portrait.")]
        public float orthoSize = 8f;

        [Tooltip("Min/max zoom range for orthographic size.")]
        public float orthoSizeMin = 6f;
        public float orthoSizeMax = 12f;

        // ====================================================================
        // Configuration — Follow Behavior
        // ====================================================================

        [Header("Follow Smoothing")]
        [Tooltip("How fast the camera follows (higher = snappier).")]
        public float followSpeed = 8f;

        [Tooltip("Smoothing damping factor. Lower = more responsive.")]
        [Range(0.01f, 0.5f)]
        public float smoothDamping = 0.08f;

        [Header("Look-Ahead (Joystick Direction)")]
        [Tooltip("How far ahead the camera shifts when moving.")]
        public float lookAheadDistance = 2f;

        [Tooltip("Smoothing speed for look-ahead adjustment.")]
        public float lookAheadSmoothing = 5f;

        [Tooltip("Look-ahead bias toward vertical (portrait: more vertical visibility).")]
        [Range(1f, 2f)]
        public float verticalLookAheadBias = 1.4f;

        // ====================================================================
        // Configuration — Room Awareness
        // ====================================================================

        [Header("Room Framing")]
        [Tooltip("If true, camera adjusts zoom and position to frame the current room.")]
        public bool roomAware = false;

        [Tooltip("Module width for room calculations (matches RoomModule.moduleWidth).")]
        public float moduleWidth = 10f;

        [Tooltip("How fast zoom adjusts when entering a new room.")]
        public float zoomTransitionSpeed = 3f;

        [Tooltip("Padding around room edges (in world units) to prevent clipping.")]
        public float roomPadding = 1.5f;

        // ====================================================================
        // Configuration — Screen Shake
        // ====================================================================

        [Header("Screen Shake")]
        [Tooltip("Shake decay curve. X=time(0-1), Y=intensity(0-1).")]
        public AnimationCurve shakeDecayCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

        [Tooltip("Maximum shake offset in world units.")]
        public float maxShakeOffset = 0.5f;

        // ====================================================================
        // Runtime State
        // ====================================================================

        /// <summary>Current camera mode.</summary>
        public CameraMode Mode { get; private set; } = CameraMode.FollowPlayer;

        // ====================================================================
        // Singleton
        // ====================================================================

        public static TopDownCamera Instance { get; private set; }

        // ====================================================================
        // Internal
        // ====================================================================

        private Camera _camera;
        private Rigidbody _cachedRb;
        private Vector3 _currentLookAhead;
        private Vector3 _velocity; // For SmoothDamp
        private float _targetOrthoSize;
        private float _currentOrthoSize;
        private Vector3 _shakeOffset;
        private Coroutine _shakeCoroutine;

        // Room tracking
        private Vector3 _currentRoomCenter;
        private Vector3 _currentRoomSize;
        private bool _hasActiveRoom;

        // ====================================================================
        // Enums
        // ====================================================================

        public enum CameraMode
        {
            FollowPlayer,   // Normal gameplay — follow with look-ahead
            FrameRoom,      // Lock to room center (e.g. during NPC spawn)
            Transitioning   // Smooth move between rooms
        }

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            _camera = GetComponent<Camera>();


        }

        private void Start()
        {
            // Auto-find player
            if (target == null)
            {
                var playerObj = GameObject.FindGameObjectWithTag("Player");
                if (playerObj != null) target = playerObj.transform;
            }

            // Cache rigidbody
            if (target != null)
                _cachedRb = target.GetComponent<Rigidbody>();

            // Configure camera for portrait isometric
            ConfigureCamera();

            // Initialize sizes
            _targetOrthoSize = orthoSize;
            _currentOrthoSize = orthoSize;

            // Snap to initial position
            if (target != null)
            {
                _currentLookAhead = Vector3.zero;
                transform.position = CalculateDesiredPosition();
                transform.rotation = CalculateDesiredRotation();
            }
        }

        private void LateUpdate()
        {
            // Auto-recover: find player if target was lost or not yet spawned
            if (target == null)
            {
                var playerObj = GameObject.FindGameObjectWithTag("Player");
                if (playerObj != null)
                {
                    SetTarget(playerObj.transform);
                    Debug.Log("[TopDownCamera] Auto-found player target.");
                }
                else
                {
                    return;
                }
            }

            UpdateLookAhead();
            UpdateZoom();

            // Calculate desired position
            Vector3 desired = CalculateDesiredPosition();

            // Smooth follow
            transform.position = Vector3.SmoothDamp(
                transform.position, desired + _shakeOffset,
                ref _velocity, smoothDamping);

            // Set rotation (fixed for isometric — no smooth needed)
            transform.rotation = CalculateDesiredRotation();

            // Apply orthographic zoom
            if (_camera != null && _camera.orthographic)
            {
                _camera.orthographicSize = Mathf.Lerp(
                    _camera.orthographicSize, _currentOrthoSize,
                    zoomTransitionSpeed * Time.deltaTime);
            }
        }

        // ====================================================================
        // Camera Configuration
        // ====================================================================

        private void ConfigureCamera()
        {
            if (_camera == null) return;

            if (useOrthographic)
            {
                _camera.orthographic = true;
                _camera.orthographicSize = orthoSize;

                // Near/far for isometric
                _camera.nearClipPlane = 0.1f;
                _camera.farClipPlane = 100f;
            }

            // Portrait aspect ratio
            // (Unity handles this automatically based on device orientation)
        }

        // ====================================================================
        // Position & Rotation
        // ====================================================================

        private Vector3 CalculateDesiredPosition()
        {
            Vector3 focusPoint = target.position + _currentLookAhead;

            // Calculate camera offset from pitch and yaw angles
            Quaternion rotation = CalculateDesiredRotation();
            Vector3 backward = rotation * Vector3.back;
            Vector3 up = rotation * Vector3.up;

            // Position = focus point + height along the camera's up-back direction
            float pitchRad = pitchAngle * Mathf.Deg2Rad;
            float distance = height / Mathf.Sin(pitchRad);

            Vector3 pos = focusPoint - rotation * Vector3.forward * distance;

            // Apply forward offset (shifts focus point ahead in camera's view)
            pos += rotation * Vector3.forward * (-forwardOffset);

            // Room-aware clamping
            if (roomAware && _hasActiveRoom)
            {
                pos = ClampToRoomBounds(pos, focusPoint);
            }

            return pos;
        }

        private Quaternion CalculateDesiredRotation()
        {
            return Quaternion.Euler(pitchAngle, yawAngle, 0f);
        }

        // ====================================================================
        // Look-Ahead (Joystick-Aware)
        // ====================================================================

        private void UpdateLookAhead()
        {
            Vector3 moveDir = Vector3.zero;

            // Use player velocity for look-ahead direction
            if (_cachedRb != null && _cachedRb.linearVelocity.sqrMagnitude > 0.1f)
            {
                moveDir = _cachedRb.linearVelocity.normalized;
            }
            else if (target != null)
            {
                // Fall back to facing direction when stationary
                moveDir = target.forward;
                moveDir *= 0.3f; // Subtle when not moving
            }

            // Only XZ plane
            moveDir.y = 0f;

            // Apply vertical bias for portrait mode (see more ahead vertically)
            moveDir.z *= verticalLookAheadBias;

            Vector3 targetLookAhead = moveDir.normalized * lookAheadDistance * moveDir.magnitude;

            // Smooth interpolation
            _currentLookAhead = Vector3.Lerp(
                _currentLookAhead, targetLookAhead,
                lookAheadSmoothing * Time.deltaTime);
        }

        // ====================================================================
        // Room-Aware Framing
        // ====================================================================

        /// <summary>
        /// Call when the player enters a new room. Camera adjusts zoom to fit.
        /// </summary>
        public void OnRoomEnter(Vector3 roomCenter, float roomWidth = -1f)
        {
            _hasActiveRoom = true;
            _currentRoomCenter = roomCenter;

            float width = roomWidth > 0 ? roomWidth : moduleWidth;
            _currentRoomSize = new Vector3(width, 0f, width);

            // Adjust orthographic zoom to fit the room
            if (useOrthographic)
            {
                // For portrait (height > width), room width determines zoom
                // We want the room to fill ~70% of the screen width
                float aspect = _camera != null ? _camera.aspect : 0.5625f; // 9:16
                float roomHalfWidth = (width + roomPadding * 2f) * 0.5f;

                // Ortho size = half-height in world units
                // Screen width in world units = ortho size * 2 * aspect
                // We want: roomWidth = screenWorldWidth * 0.7
                // → orthoSize = roomHalfWidth / (aspect * 0.7)
                float neededSize = roomHalfWidth / (aspect * 0.7f);
                _targetOrthoSize = Mathf.Clamp(neededSize, orthoSizeMin, orthoSizeMax);
            }

            Debug.Log($"[Camera] Room enter: center={roomCenter}, zoom={_targetOrthoSize:F1}");
        }

        /// <summary>
        /// Call when player enters a room by grid position (col, row).
        /// Calculates center automatically from module width.
        /// </summary>
        public void OnRoomEnterByGrid(int col, int row)
        {
            Vector3 center = new(col * moduleWidth, 0f, row * moduleWidth);
            OnRoomEnter(center, moduleWidth);
        }

        /// <summary>
        /// Resets room awareness (e.g. between runs).
        /// </summary>
        public void ClearRoomBounds()
        {
            _hasActiveRoom = false;
            _targetOrthoSize = orthoSize;
        }

        private Vector3 ClampToRoomBounds(Vector3 cameraPos, Vector3 focusPoint)
        {
            // In orthographic mode, clamp the focus point so the viewport
            // doesn't extend beyond the room boundaries
            if (!useOrthographic || _camera == null) return cameraPos;

            float halfHeight = _currentOrthoSize;
            float halfWidth = _currentOrthoSize * _camera.aspect;
            float roomHalf = _currentRoomSize.x * 0.5f + roomPadding;

            // Clamp the focus point within room bounds
            float clampedX = Mathf.Clamp(
                focusPoint.x,
                _currentRoomCenter.x - roomHalf + halfWidth,
                _currentRoomCenter.x + roomHalf - halfWidth);
            float clampedZ = Mathf.Clamp(
                focusPoint.z,
                _currentRoomCenter.z - roomHalf + halfHeight,
                _currentRoomCenter.z + roomHalf - halfHeight);

            // Recalculate camera position with clamped focus
            Vector3 clampedFocus = new(clampedX, focusPoint.y, clampedZ);
            Vector3 delta = clampedFocus - focusPoint;

            return cameraPos + delta;
        }

        private void UpdateZoom()
        {
            _currentOrthoSize = Mathf.Lerp(
                _currentOrthoSize, _targetOrthoSize,
                zoomTransitionSpeed * Time.deltaTime);
        }

        // ====================================================================
        // Public API
        // ====================================================================

        /// <summary>
        /// Immediately snaps camera to target (no smoothing).
        /// </summary>
        public void SnapToTarget()
        {
            if (target == null) return;
            _currentLookAhead = Vector3.zero;
            _velocity = Vector3.zero;
            _shakeOffset = Vector3.zero;
            transform.position = CalculateDesiredPosition();
            transform.rotation = CalculateDesiredRotation();

            if (_camera != null && _camera.orthographic)
            {
                _currentOrthoSize = _targetOrthoSize;
                _camera.orthographicSize = _currentOrthoSize;
            }
        }

        /// <summary>
        /// Sets a new target and recaches its Rigidbody.
        /// </summary>
        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            _cachedRb = newTarget != null ? newTarget.GetComponent<Rigidbody>() : null;
            if (newTarget != null) SnapToTarget();
        }

        /// <summary>
        /// Smoothly transitions to a new focus point over duration seconds.
        /// Useful for cutscenes or boss room reveals.
        /// </summary>
        public void PanTo(Vector3 worldPosition, float duration = 1f)
        {
            StartCoroutine(PanCoroutine(worldPosition, duration));
        }

        /// <summary>
        /// Sets the zoom level directly (orthographic size).
        /// </summary>
        public void SetZoom(float orthoSizeTarget)
        {
            _targetOrthoSize = Mathf.Clamp(orthoSizeTarget, orthoSizeMin, orthoSizeMax);
        }

        // ====================================================================
        // Screen Shake
        // ====================================================================

        /// <summary>
        /// Applies a screen-shake effect with smooth decay.
        /// </summary>
        public void Shake(float intensity = 0.3f, float duration = 0.2f)
        {
            intensity = Mathf.Min(intensity, maxShakeOffset);

            if (_shakeCoroutine != null)
                StopCoroutine(_shakeCoroutine);

            _shakeCoroutine = StartCoroutine(ShakeCoroutine(intensity, duration));
        }

        private IEnumerator ShakeCoroutine(float intensity, float duration)
        {
            float elapsed = 0f;

            while (elapsed < duration)
            {
                float t = elapsed / duration;

                // Use decay curve for natural falloff
                float curveValue = shakeDecayCurve.Evaluate(t);

                // Random direction, intensity modulated by curve
                Vector2 offset2D = Random.insideUnitCircle * intensity * curveValue;
                _shakeOffset = new Vector3(offset2D.x, 0f, offset2D.y);

                elapsed += Time.deltaTime;
                yield return null;
            }

            _shakeOffset = Vector3.zero;
            _shakeCoroutine = null;
        }

        // ====================================================================
        // Pan / Transition Coroutines
        // ====================================================================

        private IEnumerator PanCoroutine(Vector3 worldTarget, float duration)
        {
            var prevMode = Mode;
            Mode = CameraMode.Transitioning;

            Vector3 startPos = transform.position;
            Vector3 endPos = CalculatePositionForFocus(worldTarget);

            float elapsed = 0f;
            while (elapsed < duration)
            {
                float t = elapsed / duration;
                float ease = SmoothStep(t);

                transform.position = Vector3.Lerp(startPos, endPos, ease);
                elapsed += Time.deltaTime;
                yield return null;
            }

            transform.position = endPos;
            Mode = prevMode;
        }

        private Vector3 CalculatePositionForFocus(Vector3 focusPoint)
        {
            float pitchRad = pitchAngle * Mathf.Deg2Rad;
            float distance = height / Mathf.Sin(pitchRad);
            Quaternion rot = CalculateDesiredRotation();
            return focusPoint - rot * Vector3.forward * distance;
        }

        // ====================================================================
        // Utility
        // ====================================================================

        /// <summary>Smooth step (ease-in-out) for transitions.</summary>
        private static float SmoothStep(float t)
        {
            return t * t * (3f - 2f * t);
        }

#if UNITY_EDITOR
        // ====================================================================
        // Editor Gizmos — visualize camera frustum and room bounds
        // ====================================================================

        private void OnDrawGizmosSelected()
        {
            if (_hasActiveRoom)
            {
                // Draw room bounds
                Gizmos.color = new Color(0.3f, 0.85f, 1f, 0.3f);
                Vector3 size = _currentRoomSize + Vector3.up * 3f;
                Gizmos.DrawWireCube(_currentRoomCenter + Vector3.up * 1.5f, size);

                // Draw padded bounds
                Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.2f);
                Vector3 paddedSize = size + new Vector3(roomPadding * 2f, 0f, roomPadding * 2f);
                Gizmos.DrawWireCube(_currentRoomCenter + Vector3.up * 1.5f, paddedSize);
            }

            // Draw look-ahead vector
            if (target != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(target.position, target.position + _currentLookAhead);
                Gizmos.DrawSphere(target.position + _currentLookAhead, 0.2f);
            }
        }
#endif
    }
}
