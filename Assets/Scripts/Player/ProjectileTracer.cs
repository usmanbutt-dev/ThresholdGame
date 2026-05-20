// ============================================================================
// ProjectileTracer.cs — Lightweight hitscan tracer visual
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Spawns a thin LineRenderer from origin to hit point that fades out.
// Purely visual — all damage is handled by raycast (hitscan).
// Call ProjectileTracer.Spawn() from any weapon system.
// ============================================================================

using UnityEngine;

namespace Threshold.Player
{
    /// <summary>
    /// Configuration struct for tracer appearance.
    /// Set these values on PlayerWeapon in the Inspector.
    /// </summary>
    [System.Serializable]
    public struct TracerConfig
    {
        [Tooltip("Main color of the laser beam.")]
        public Color color;

        [Tooltip("Width of the beam at the muzzle.")]
        [Range(0.01f, 0.5f)]
        public float startWidth;

        [Tooltip("Width of the beam at the end point.")]
        [Range(0.005f, 0.3f)]
        public float endWidth;

        [Tooltip("How long the tracer takes to fade out (seconds).")]
        [Range(0.02f, 1f)]
        public float fadeDuration;

        [Tooltip("Glow intensity multiplier for HDR bloom. 1 = normal, 2+ = bright glow.")]
        [Range(0.5f, 5f)]
        public float glowIntensity;

        /// <summary>Default cyan laser config.</summary>
        public static TracerConfig Default => new()
        {
            color         = new Color(0.3f, 0.9f, 1f, 0.9f),
            startWidth    = 0.06f,
            endWidth      = 0.02f,
            fadeDuration  = 0.1f,
            glowIntensity = 1.5f
        };
    }

    /// <summary>
    /// Visual tracer line effect for hitscan weapons.
    /// Self-destructs after fade duration. No physics.
    /// </summary>
    public class ProjectileTracer : MonoBehaviour
    {
        // ====================================================================
        // Internal
        // ====================================================================

        private LineRenderer _lineRenderer;
        private float _spawnTime;
        private Color _baseColor;
        private float _fadeDuration;
        private float _startWidth;
        private float _endWidth;

        // ====================================================================
        // Static Factory
        // ====================================================================

        /// <summary>
        /// Spawns a tracer line from origin to target using a TracerConfig.
        /// </summary>
        public static ProjectileTracer Spawn(Vector3 origin, Vector3 target, TracerConfig config)
        {
            var go = new GameObject("Tracer");
            var tracer = go.AddComponent<ProjectileTracer>();
            tracer.Initialize(origin, target, config);
            return tracer;
        }

        /// <summary>
        /// Spawns a tracer line from origin to target with the given color (legacy overload).
        /// </summary>
        public static ProjectileTracer Spawn(Vector3 origin, Vector3 target, Color color)
        {
            var cfg = TracerConfig.Default;
            cfg.color = color;
            return Spawn(origin, target, cfg);
        }

        /// <summary>
        /// Spawns a tracer with a default color.
        /// </summary>
        public static ProjectileTracer Spawn(Vector3 origin, Vector3 target)
        {
            return Spawn(origin, target, TracerConfig.Default);
        }

        // ====================================================================
        // Setup
        // ====================================================================

        private void Initialize(Vector3 origin, Vector3 target, TracerConfig config)
        {
            _baseColor    = config.color;
            _fadeDuration = Mathf.Max(config.fadeDuration, 0.02f);
            _startWidth   = config.startWidth;
            _endWidth     = config.endWidth;
            _spawnTime    = Time.time;

            // HDR glow: multiply color by intensity for bloom support
            Color hdrColor = _baseColor * config.glowIntensity;
            hdrColor.a = _baseColor.a;

            // Create LineRenderer
            _lineRenderer = gameObject.AddComponent<LineRenderer>();
            _lineRenderer.positionCount = 2;
            _lineRenderer.SetPosition(0, origin);
            _lineRenderer.SetPosition(1, target);

            // Material — use built-in sprite shader for additive glow
            _lineRenderer.material = new Material(Shader.Find("Sprites/Default"));

            // Width
            _lineRenderer.startWidth = _startWidth;
            _lineRenderer.endWidth   = _endWidth;

            // Color — apply HDR intensity
            _lineRenderer.startColor = hdrColor;
            _lineRenderer.endColor   = hdrColor * 0.7f;

            // Shadow off
            _lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _lineRenderer.receiveShadows = false;

            // Auto-destroy safety net
            Destroy(gameObject, _fadeDuration + 0.1f);
        }

        // ====================================================================
        // Fade
        // ====================================================================

        private void Update()
        {
            if (_lineRenderer == null) return;

            float elapsed = Time.time - _spawnTime;
            float t = Mathf.Clamp01(elapsed / _fadeDuration);

            // Fade alpha
            float alpha = Mathf.Lerp(_baseColor.a, 0f, t);
            Color fadedStart = new(_baseColor.r, _baseColor.g, _baseColor.b, alpha);
            Color fadedEnd = new(_baseColor.r * 0.7f, _baseColor.g * 0.7f, _baseColor.b * 0.7f, alpha * 0.7f);

            _lineRenderer.startColor = fadedStart;
            _lineRenderer.endColor   = fadedEnd;

            // Shrink width as it fades
            _lineRenderer.startWidth = _startWidth * (1f - t * 0.5f);
            _lineRenderer.endWidth   = _endWidth * (1f - t * 0.5f);

            if (t >= 1f)
            {
                Destroy(gameObject);
            }
        }
    }
}
