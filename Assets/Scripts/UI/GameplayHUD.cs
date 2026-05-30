// ============================================================================
// GameplayHUD.cs — In-game heads-up display for mobile shooter
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Self-building HUD: health bar, ammo counter, kill counter, room progress,
// fire button with auto-aim. All built programmatically — no prefab needed.
// ============================================================================

using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Threshold.UI
{
    /// <summary>
    /// Gameplay HUD — builds all UI elements at runtime.
    /// Attach to any persistent GameObject.
    /// </summary>
    public class GameplayHUD : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("Health Bar")]
        public Color healthFullColor = new(0.2f, 0.9f, 0.4f, 1f);
        public Color healthLowColor = new(0.95f, 0.2f, 0.2f, 1f);
        public float healthLowThreshold = 0.3f;

        [Header("Ammo")]
        public int maxAmmo = 30;

        [Header("Fire Button")]
        [Tooltip("Size of the fire button in screen pixels.")]
        public float fireButtonSize = 160f;
        public Color fireButtonColor = new(0.95f, 0.25f, 0.25f, 0.6f);
        public Color fireButtonPressedColor = new(1f, 0.5f, 0.2f, 0.9f);

        // ====================================================================
        // Events
        // ====================================================================

        /// <summary>Fired when the fire button is pressed.</summary>
        public event Action OnFirePressed;

        /// <summary>Fired when the fire button is released.</summary>
        public event Action OnFireReleased;

        /// <summary>True while fire button is held down (or AimJoystick is active).</summary>
        public bool IsFiring => (AimJoystick.Instance != null && AimJoystick.Instance.IsAiming) || _isFiringButton;

        // ====================================================================
        // Singleton
        // ====================================================================

        public static GameplayHUD Instance { get; private set; }

        // ====================================================================
        // Internal References (serialized so editor tool can pre-assign)
        // ====================================================================

        private Canvas _canvas;
        private Transform _safeAreaTransform;

        // Health
        [SerializeField] private RectTransform _healthBarBg;
        [SerializeField] private RectTransform _healthBarFill;
        [SerializeField] private Image _healthFillImage;
        [SerializeField] private Text _healthText;

        // Ammo
        [SerializeField] private Text _ammoText;
        [SerializeField] private Image _ammoIcon;

        // Kill counter
        [SerializeField] private Text _killText;

        // Room progress
        [SerializeField] private Text _roomText;
        [SerializeField] private RectTransform _roomProgressBg;
        [SerializeField] private RectTransform _roomProgressFill;
        [SerializeField] private Image _roomFillImage;

        // Fire button (legacy — replaced by AimJoystick but kept for fallback)
        private Image _fireButtonImage;
        private bool _isFiringButton;

        // Current values
        private float _currentHealth = 1f;
        private int _currentAmmo;
        private int _kills;
        private int _roomsCurrent;
        private int _roomsTotal;

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
            _currentAmmo = maxAmmo;
            BuildUI();
            RefreshAll();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ====================================================================
        // UI Construction
        // ====================================================================

        private void BuildUI()
        {
            _canvas = GetOrCreateCanvas();

            // Create a central safe area container so all HUD elements adapt to notched mobile devices
            var safeAreaObj = new GameObject("SafeArea_Container", typeof(RectTransform));
            safeAreaObj.transform.SetParent(_canvas.transform, false);
            var safeAreaRect = safeAreaObj.GetComponent<RectTransform>();
            safeAreaRect.anchorMin = Vector2.zero;
            safeAreaRect.anchorMax = Vector2.one;
            safeAreaRect.offsetMin = Vector2.zero;
            safeAreaRect.offsetMax = Vector2.zero;
            safeAreaObj.AddComponent<MobileSafeArea>();
            _safeAreaTransform = safeAreaObj.transform;

            // Skip building elements that are already wired by editor
            if (_healthBarFill == null) BuildHealthBar();
            if (_ammoText == null) BuildAmmoCounter();
            if (_killText == null) BuildKillCounter();
            if (_roomText == null) BuildRoomProgress();

            // Only build fire button if no AimJoystick exists
            if (AimJoystick.Instance == null && FindAnyObjectByType<AimJoystick>() == null)
                BuildFireButton();
        }

        private Canvas GetOrCreateCanvas()
        {
            var canvas = FindAnyObjectByType<Canvas>();
            if (canvas != null) return canvas;

            var obj = new GameObject("UI_Canvas");
            canvas = obj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = obj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            obj.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        // --- HEALTH BAR (top-left) ---
        private void BuildHealthBar()
        {
            // Container
            var container = CreatePanel("HealthBar_Container", _safeAreaTransform);
            var containerRect = container.GetComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(0f, 1f);
            containerRect.anchorMax = new Vector2(0f, 1f);
            containerRect.pivot = new Vector2(0f, 1f);
            containerRect.anchoredPosition = new Vector2(24f, -24f);
            containerRect.sizeDelta = new Vector2(300f, 50f);

            // Health icon (+ symbol)
            var iconObj = CreateText("Health_Icon", container.transform, "+", 28,
                new Color(0.9f, 0.3f, 0.3f, 1f), TextAnchor.MiddleCenter);
            var iconRect = iconObj.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0f);
            iconRect.anchorMax = new Vector2(0f, 1f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(0f, 0f);
            iconRect.sizeDelta = new Vector2(30f, 0f);

            // Background bar
            var bgObj = CreatePanel("HealthBar_Bg", container.transform);
            _healthBarBg = bgObj.GetComponent<RectTransform>();
            _healthBarBg.anchorMin = new Vector2(0f, 0.15f);
            _healthBarBg.anchorMax = new Vector2(1f, 0.85f);
            _healthBarBg.offsetMin = new Vector2(36f, 0f);
            _healthBarBg.offsetMax = new Vector2(-8f, 0f);
            bgObj.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

            // Fill bar
            var fillObj = CreatePanel("HealthBar_Fill", bgObj.transform);
            _healthBarFill = fillObj.GetComponent<RectTransform>();
            _healthBarFill.anchorMin = Vector2.zero;
            _healthBarFill.anchorMax = Vector2.one;
            _healthBarFill.offsetMin = new Vector2(3f, 3f);
            _healthBarFill.offsetMax = new Vector2(-3f, -3f);
            _healthFillImage = fillObj.GetComponent<Image>();
            _healthFillImage.color = healthFullColor;

            // Health text overlay
            var textObj = CreateText("Health_Text", bgObj.transform, "100%", 18,
                Color.white, TextAnchor.MiddleCenter);
            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            _healthText = textObj.GetComponent<Text>();
        }

        // --- AMMO COUNTER (top-right) ---
        private void BuildAmmoCounter()
        {
            var container = CreatePanel("Ammo_Container", _safeAreaTransform);
            var containerRect = container.GetComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(1f, 1f);
            containerRect.anchorMax = new Vector2(1f, 1f);
            containerRect.pivot = new Vector2(1f, 1f);
            containerRect.anchoredPosition = new Vector2(-24f, -24f);
            containerRect.sizeDelta = new Vector2(180f, 50f);
            container.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);

            // Ammo text
            var textObj = CreateText("Ammo_Text", container.transform, "30 / 30", 26,
                new Color(1f, 0.85f, 0.3f, 1f), TextAnchor.MiddleCenter);
            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);
            _ammoText = textObj.GetComponent<Text>();
        }

        // --- KILL COUNTER (top-center-left, below health) ---
        private void BuildKillCounter()
        {
            var textObj = CreateText("Kill_Text", _safeAreaTransform,
                "KILLS: 0", 20, new Color(1f, 0.4f, 0.4f, 0.9f), TextAnchor.MiddleLeft);
            var rect = textObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(24f, -82f);
            rect.sizeDelta = new Vector2(200f, 30f);
            _killText = textObj.GetComponent<Text>();
        }

        // --- ROOM PROGRESS (top-center) ---
        private void BuildRoomProgress()
        {
            var container = CreatePanel("Room_Container", _safeAreaTransform);
            var containerRect = container.GetComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(0.5f, 1f);
            containerRect.anchorMax = new Vector2(0.5f, 1f);
            containerRect.pivot = new Vector2(0.5f, 1f);
            containerRect.anchoredPosition = new Vector2(0f, -24f);
            containerRect.sizeDelta = new Vector2(260f, 44f);
            container.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);

            // Room text
            var textObj = CreateText("Room_Text", container.transform,
                "ROOM 1 / 7", 18, Color.white, TextAnchor.MiddleCenter);
            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 0.5f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);
            _roomText = textObj.GetComponent<Text>();

            // Progress bar bg
            var bgObj = CreatePanel("RoomProgress_Bg", container.transform);
            _roomProgressBg = bgObj.GetComponent<RectTransform>();
            _roomProgressBg.anchorMin = new Vector2(0.05f, 0.1f);
            _roomProgressBg.anchorMax = new Vector2(0.95f, 0.4f);
            _roomProgressBg.offsetMin = Vector2.zero;
            _roomProgressBg.offsetMax = Vector2.zero;
            bgObj.GetComponent<Image>().color = new Color(0.3f, 0.3f, 0.3f, 0.8f);

            // Progress bar fill
            var fillObj = CreatePanel("RoomProgress_Fill", bgObj.transform);
            _roomProgressFill = fillObj.GetComponent<RectTransform>();
            _roomProgressFill.anchorMin = Vector2.zero;
            _roomProgressFill.anchorMax = new Vector2(0f, 1f); // Start at 0 width
            _roomProgressFill.offsetMin = Vector2.zero;
            _roomProgressFill.offsetMax = Vector2.zero;
            _roomFillImage = fillObj.GetComponent<Image>();
            _roomFillImage.color = new Color(0.3f, 0.8f, 1f, 0.9f);
        }

        // --- FIRE BUTTON (bottom-right) ---
        private void BuildFireButton()
        {
            var btnObj = CreatePanel("Fire_Button", _safeAreaTransform);
            var btnRect = btnObj.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1f, 0f);
            btnRect.anchorMax = new Vector2(1f, 0f);
            btnRect.pivot = new Vector2(1f, 0f);
            btnRect.anchoredPosition = new Vector2(-80f, 100f);
            btnRect.sizeDelta = new Vector2(fireButtonSize, fireButtonSize);

            _fireButtonImage = btnObj.GetComponent<Image>();
            _fireButtonImage.color = fireButtonColor;
            MakeCircular(_fireButtonImage);

            // Crosshair label
            var label = CreateText("Fire_Label", btnObj.transform,
                "⊕", 48, Color.white, TextAnchor.MiddleCenter);
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            // Fire button events
            var trigger = btnObj.AddComponent<FireButtonHandler>();
            trigger.hud = this;
        }

        // ====================================================================
        // UI Helpers
        // ====================================================================

        private GameObject CreatePanel(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            return go;
        }

        private GameObject CreateText(string name, Transform parent, string text,
            int fontSize, Color color, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = anchor;
            t.font = Font.CreateDynamicFontFromOSFont("Arial", fontSize);
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return go;
        }

        private void MakeCircular(Image img)
        {
            int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = size / 2f;
            float radius = center - 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    float alpha = Mathf.Clamp01((radius - dist) * 2f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            tex.Apply();
            img.sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        // ====================================================================
        // Public API — Called by game systems
        // ====================================================================

        /// <summary>Update health display (0-1 normalized).</summary>
        public void SetHealth(float normalized)
        {
            _currentHealth = Mathf.Clamp01(normalized);
            RefreshHealth();
        }

        /// <summary>Update ammo display.</summary>
        public void SetAmmo(int current, int max)
        {
            _currentAmmo = current;
            maxAmmo = max;
            RefreshAmmo();
        }

        /// <summary>Increment kill counter.</summary>
        public void AddKill()
        {
            _kills++;
            RefreshKills();
        }

        /// <summary>Set kill count directly.</summary>
        public void SetKills(int count)
        {
            _kills = count;
            RefreshKills();
        }

        /// <summary>Update room progress.</summary>
        public void SetRoomProgress(int current, int total)
        {
            _roomsCurrent = current;
            _roomsTotal = total;
            RefreshRoomProgress();
        }

        /// <summary>Reset all HUD values for a new run.</summary>
        public void ResetForNewRun(int totalRooms)
        {
            _currentHealth = 1f;
            _currentAmmo = maxAmmo;
            _kills = 0;
            _roomsCurrent = 0;
            _roomsTotal = totalRooms;
            RefreshAll();
        }

        // ====================================================================
        // Fire Button Callbacks
        // ====================================================================

        internal void HandleFireDown()
        {
            _isFiringButton = true;
            _fireButtonImage.color = fireButtonPressedColor;
            OnFirePressed?.Invoke();
        }

        internal void HandleFireUp()
        {
            _isFiringButton = false;
            _fireButtonImage.color = fireButtonColor;
            OnFireReleased?.Invoke();
        }

        // ====================================================================
        // Refresh Methods
        // ====================================================================

        private void RefreshAll()
        {
            RefreshHealth();
            RefreshAmmo();
            RefreshKills();
            RefreshRoomProgress();
        }

        private void RefreshHealth()
        {
            if (_healthBarFill == null) return;

            // Scale fill bar
            _healthBarFill.anchorMax = new Vector2(_currentHealth, 1f);

            // Color lerp: green → red
            _healthFillImage.color = _currentHealth > healthLowThreshold
                ? Color.Lerp(new Color(0.9f, 0.7f, 0.1f), healthFullColor, _currentHealth)
                : healthLowColor;

            // Text
            int pct = Mathf.RoundToInt(_currentHealth * 100f);
            _healthText.text = $"{pct}%";

            // Pulse effect at low health
            if (_currentHealth <= healthLowThreshold)
            {
                float pulse = Mathf.PingPong(Time.time * 3f, 0.3f);
                _healthFillImage.color = Color.Lerp(healthLowColor, Color.white, pulse);
            }
        }

        private void RefreshAmmo()
        {
            if (_ammoText == null) return;
            _ammoText.text = $"{_currentAmmo} / {maxAmmo}";

            // Flash red when low
            _ammoText.color = _currentAmmo <= 5
                ? new Color(1f, 0.3f, 0.3f, 1f)
                : new Color(1f, 0.85f, 0.3f, 1f);
        }

        private void RefreshKills()
        {
            if (_killText == null) return;
            _killText.text = $"KILLS: {_kills}";
        }

        private void RefreshRoomProgress()
        {
            if (_roomText == null || _roomProgressFill == null) return;

            _roomText.text = $"ROOM {_roomsCurrent} / {_roomsTotal}";

            float progress = _roomsTotal > 0 ? (float)_roomsCurrent / _roomsTotal : 0f;
            _roomProgressFill.anchorMax = new Vector2(progress, 1f);
        }

        private void Update()
        {
            // Continuous pulse for low health
            if (_currentHealth <= healthLowThreshold)
                RefreshHealth();
        }
    }

    // ========================================================================
    // Helper: Fire button event handler
    // ========================================================================

    /// <summary>
    /// Handles fire button press/release events.
    /// </summary>
    public class FireButtonHandler : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler
    {
        [HideInInspector] public GameplayHUD hud;

        public void OnPointerDown(PointerEventData e) => hud?.HandleFireDown();
        public void OnPointerUp(PointerEventData e) => hud?.HandleFireUp();
    }
}
