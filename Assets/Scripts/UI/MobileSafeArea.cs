// ============================================================================
// MobileSafeArea.cs — Mobile safe-area / notch adaptation component
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Industry-standard solution for handling device notches, dynamic islands,
// bottom home indicators, and rounded screen corners on iOS and Android.
// Converts pixel-based Screen.safeArea to normalized UI anchors (0 to 1).
// ============================================================================

using UnityEngine;

namespace Threshold.UI
{
    /// <summary>
    /// Adjusts a RectTransform's anchors to fit the physical device's safe area.
    /// Place on a full-screen container to automatically protect child UI elements
    /// from camera notches, dynamic islands, and home indicators.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class MobileSafeArea : MonoBehaviour
    {
        private RectTransform _rectTransform;
        private Rect _lastSafeArea = new(0, 0, 0, 0);

        private void Awake()
        {
            _rectTransform = GetComponent<RectTransform>();
        }

        private void Start()
        {
            ApplySafeArea();
        }

        private void Update()
        {
            // Dynamic check in case of screen rotation or split-screen size changes
            if (_lastSafeArea != Screen.safeArea)
            {
                ApplySafeArea();
            }
        }

        /// <summary>
        /// Translates pixel safe area coordinates to normalized canvas anchors.
        /// </summary>
        private void ApplySafeArea()
        {
            _lastSafeArea = Screen.safeArea;

            // Handle edge case of empty safe area
            if (Screen.width == 0 || Screen.height == 0) return;

            Vector2 anchorMin = _lastSafeArea.position;
            Vector2 anchorMax = _lastSafeArea.position + _lastSafeArea.size;

            // Normalize coordinates between 0 and 1
            anchorMin.x /= Screen.width;
            anchorMin.y /= Screen.height;
            anchorMax.x /= Screen.width;
            anchorMax.y /= Screen.height;

            // Apply anchors
            _rectTransform.anchorMin = anchorMin;
            _rectTransform.anchorMax = anchorMax;

            // Zero out pixel offsets to let anchors drive the layout
            _rectTransform.offsetMin = Vector2.zero;
            _rectTransform.offsetMax = Vector2.zero;
        }
    }
}
