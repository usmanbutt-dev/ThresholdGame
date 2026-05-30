// ============================================================================
// ThresholdUIManager.cs — Master UI coordinator
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Central manager that initializes all UI systems, manages transitions
// between gameplay and summary screens, and provides a unified API.
// Add this single component to bootstrap the entire UI.
// ============================================================================

using UnityEngine;
using UnityEngine.UI;

namespace Threshold.UI
{
    /// <summary>
    /// Master UI manager — add this component to a GameObject and it
    /// bootstraps the entire THRESHOLD UI system at runtime.
    /// </summary>
    public class ThresholdUIManager : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("Components (auto-created if null)")]
        public TopDownCamera topDownCamera;
        public VirtualJoystick joystick;
        public AimJoystick aimJoystick;
        public GameplayHUD hud;
        public DefectionPopup defectionPopup;
        public RunSummaryScreen summaryScreen;
        public PauseScreen pauseScreen;

        [Header("Canvas Settings")]
        public int canvasSortOrder = 100;
        public Vector2 referenceResolution = new(1080, 1920);

        [Header("Pause Button Settings (Inspector-Adjustable)")]
        [Tooltip("Offset of the pause button from top-right corner of the safe area.")]
        public Vector2 pauseButtonOffset = new(-24f, -82f);

        [Tooltip("Diameter of the circular pause button in screen pixels.")]
        public float pauseButtonSize = 60f;

        [Tooltip("Color and transparency of the pause button.")]
        public Color pauseButtonColor = new(0.12f, 0.12f, 0.18f, 0.65f);

        [Tooltip("Text size of the pause icon (❚❚).")]
        public int pauseButtonIconSize = 28;

        // ====================================================================
        // State
        // ====================================================================

        /// <summary>True when the summary screen is visible.</summary>
        public bool IsInSummary { get; private set; }

        /// <summary>True when the game is paused.</summary>
        public bool IsPaused => pauseScreen != null && pauseScreen.IsPaused;

        /// <summary>True when gameplay HUD is active.</summary>
        public bool IsInGameplay => !IsInSummary && !IsPaused;

        // ====================================================================
        // Singleton
        // ====================================================================

        public static ThresholdUIManager Instance { get; private set; }

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
            EnsureCanvas();
            EnsureEventSystem();
            InitializeComponents();

            Debug.Log("[ThresholdUI] All UI systems initialized.");
        }

        private void Update()
        {
            // Apply pause button settings dynamically every frame so they can be tuned live in the Inspector
            if (_pauseButtonObj != null)
            {
                var rect = _pauseButtonObj.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.anchoredPosition = pauseButtonOffset;
                    rect.sizeDelta = new Vector2(pauseButtonSize, pauseButtonSize);
                }

                var img = _pauseButtonObj.GetComponent<Image>();
                if (img != null)
                {
                    img.color = pauseButtonColor;
                }

                var text = _pauseButtonObj.GetComponentInChildren<Text>();
                if (text != null)
                {
                    text.fontSize = pauseButtonIconSize;
                }
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ====================================================================
        // Initialization
        // ====================================================================

        private void EnsureCanvas()
        {
            if (FindAnyObjectByType<Canvas>() != null) return;

            var canvasObj = new GameObject("THRESHOLD_Canvas");
            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = canvasSortOrder;

            var scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;
            scaler.matchWidthOrHeight = 0.5f;

            canvasObj.AddComponent<GraphicRaycaster>();
        }

        private void EnsureEventSystem()
        {
            if (FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() != null) return;

            var esObj = new GameObject("EventSystem");
            esObj.AddComponent<UnityEngine.EventSystems.EventSystem>();
#if ENABLE_INPUT_SYSTEM
            esObj.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            esObj.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
#endif
        }

        private void InitializeComponents()
        {
            // Top-down camera — attach to main camera
            if (topDownCamera == null)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    topDownCamera = cam.gameObject.GetComponent<TopDownCamera>();
                    if (topDownCamera == null)
                        topDownCamera = cam.gameObject.AddComponent<TopDownCamera>();
                }
            }

            // Virtual joystick (movement — left stick)
            if (joystick == null)
            {
                joystick = FindAnyObjectByType<VirtualJoystick>();
                if (joystick == null)
                {
                    var obj = new GameObject("VirtualJoystick");
                    obj.transform.SetParent(transform);
                    joystick = obj.AddComponent<VirtualJoystick>();
                }
            }

            // Aim joystick (aim/shoot — right stick)
            if (aimJoystick == null)
            {
                aimJoystick = FindAnyObjectByType<AimJoystick>();
                if (aimJoystick == null)
                {
                    var obj = new GameObject("AimJoystick");
                    obj.transform.SetParent(transform);
                    aimJoystick = obj.AddComponent<AimJoystick>();
                }
            }

            // Gameplay HUD
            if (hud == null)
            {
                hud = FindAnyObjectByType<GameplayHUD>();
                if (hud == null)
                {
                    var obj = new GameObject("GameplayHUD");
                    obj.transform.SetParent(transform);
                    hud = obj.AddComponent<GameplayHUD>();
                }
            }

            // Defection popup
            if (defectionPopup == null)
            {
                defectionPopup = FindAnyObjectByType<DefectionPopup>();
                if (defectionPopup == null)
                {
                    var obj = new GameObject("DefectionPopup");
                    obj.transform.SetParent(transform);
                    defectionPopup = obj.AddComponent<DefectionPopup>();
                }
            }

            // Run summary screen
            if (summaryScreen == null)
            {
                summaryScreen = FindAnyObjectByType<RunSummaryScreen>();
                if (summaryScreen == null)
                {
                    var obj = new GameObject("RunSummaryScreen");
                    obj.transform.SetParent(transform);
                    summaryScreen = obj.AddComponent<RunSummaryScreen>();
                }
            }

            // Pause screen
            if (pauseScreen == null)
            {
                pauseScreen = FindAnyObjectByType<PauseScreen>();
                if (pauseScreen == null)
                {
                    var obj = new GameObject("PauseScreen");
                    obj.transform.SetParent(transform);
                    pauseScreen = obj.AddComponent<PauseScreen>();
                }
            }

            // Wire up the continue event
            summaryScreen.OnContinue += HandleSummaryContinue;

            // Wire up pause screen events so gameplay UI is restored
            pauseScreen.OnResume += () => SetGameplayUIActive(true);

            // Build the pause button on the HUD canvas
            BuildPauseButton();
        }

        // ====================================================================
        // Public API — Game Loop Integration
        // ====================================================================

        /// <summary>
        /// Initializes HUD for a new run.
        /// </summary>
        public void StartRun(int totalRooms)
        {
            IsInSummary = false;
            hud.ResetForNewRun(totalRooms);
            SetGameplayUIActive(true);
            Debug.Log($"[ThresholdUI] Run started — {totalRooms} rooms");
        }

        /// <summary>
        /// Shows the between-runs summary screen.
        /// Pauses gameplay time scale.
        /// </summary>
        public void ShowRunSummary(RunSummaryData data)
        {
            IsInSummary = true;
            SetGameplayUIActive(false);
            summaryScreen.Show(data);
            Time.timeScale = 0f;
        }

        /// <summary>
        /// Shows a defection notification popup.
        /// </summary>
        public void ShowDefection(string npcId, string archetype = "")
        {
            defectionPopup.Show(npcId, archetype);
        }

        /// <summary>
        /// Updates health on the HUD.
        /// </summary>
        public void UpdateHealth(float normalized)
        {
            hud.SetHealth(normalized);
        }

        /// <summary>
        /// Updates ammo on the HUD.
        /// </summary>
        public void UpdateAmmo(int current, int max)
        {
            hud.SetAmmo(current, max);
        }

        /// <summary>
        /// Records a kill on the HUD.
        /// </summary>
        public void RecordKill()
        {
            hud.AddKill();
        }

        /// <summary>
        /// Updates room progress on the HUD.
        /// </summary>
        public void UpdateRoomProgress(int current, int total)
        {
            hud.SetRoomProgress(current, total);
        }

        /// <summary>
        /// Gets the current movement input from the virtual joystick.
        /// Returns Vector3 in world XZ space.
        /// </summary>
        public Vector3 GetMoveInput()
        {
            return joystick != null ? joystick.MoveDirection : Vector3.zero;
        }

        /// <summary>
        /// Returns true while the fire button is held (or aim stick is active).
        /// </summary>
        public bool IsFireHeld()
        {
            if (aimJoystick != null && aimJoystick.IsAiming) return true;
            return hud != null && hud.IsFiring;
        }

        /// <summary>
        /// Gets the current aim input from the aim joystick.
        /// Returns Vector3 in world XZ space.
        /// </summary>
        public Vector3 GetAimInput()
        {
            return aimJoystick != null ? aimJoystick.AimDirection : Vector3.zero;
        }

        /// <summary>
        /// Shakes the camera (e.g. on hit or explosion).
        /// </summary>
        public void ShakeCamera(float intensity = 0.3f, float duration = 0.2f)
        {
            topDownCamera?.Shake(intensity, duration);
        }

        // ====================================================================
        // Pause API
        // ====================================================================

        /// <summary>
        /// Toggles the pause state. Call from the HUD pause button.
        /// </summary>
        public void TogglePause()
        {
            if (IsInSummary) return; // Don't allow pausing during summary
            if (pauseScreen == null) return;

            if (pauseScreen.IsPaused)
                ResumeGame();
            else
                PauseGame();
        }

        /// <summary>
        /// Pauses the game and shows the pause screen.
        /// </summary>
        public void PauseGame()
        {
            if (IsInSummary || IsPaused) return;
            if (pauseScreen == null) return;

            SetGameplayUIActive(false);
            pauseScreen.Pause();
        }

        /// <summary>
        /// Resumes the game and hides the pause screen.
        /// </summary>
        public void ResumeGame()
        {
            if (!IsPaused) return;
            if (pauseScreen == null) return;

            pauseScreen.Resume();
            SetGameplayUIActive(true);
        }

        // ====================================================================
        // Internal
        // ====================================================================

        private GameObject _pauseButtonObj;

        private void SetGameplayUIActive(bool active)
        {
            if (joystick != null)
                joystick.gameObject.SetActive(active);
            if (aimJoystick != null)
                aimJoystick.gameObject.SetActive(active);
            if (_pauseButtonObj != null)
                _pauseButtonObj.SetActive(active);
        }

        private void HandleSummaryContinue()
        {
            IsInSummary = false;
            SetGameplayUIActive(true);
            Time.timeScale = 1f;
            Debug.Log("[ThresholdUI] Summary dismissed — starting next run.");
        }

        /// <summary>
        /// Creates a small pause button (❚❚) in the top-right corner of the HUD.
        /// </summary>
        private void BuildPauseButton()
        {
            var canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null) return;

            // Notch safe area container created by GameplayHUD
            Transform parentTransform = canvas.transform;
            var safeArea = GameObject.Find("SafeArea_Container");
            if (safeArea != null)
            {
                parentTransform = safeArea.transform;
            }

            // Container
            var btnObj = new GameObject("PauseButton", typeof(RectTransform), typeof(Image));
            btnObj.transform.SetParent(parentTransform, false);

            var btnRect = btnObj.GetComponent<RectTransform>();
            // Position: top-right, below the ammo bar
            btnRect.anchorMin = btnRect.anchorMax = new Vector2(1f, 1f);
            btnRect.pivot = new Vector2(1f, 1f);
            btnRect.anchoredPosition = pauseButtonOffset;
            btnRect.sizeDelta = new Vector2(pauseButtonSize, pauseButtonSize);

            var btnImage = btnObj.GetComponent<Image>();
            btnImage.color = pauseButtonColor;
            btnImage.raycastTarget = true;

            // Pause icon text (❚❚)
            var iconObj = new GameObject("PauseIcon", typeof(RectTransform), typeof(Text));
            iconObj.transform.SetParent(btnObj.transform, false);
            var iconRect = iconObj.GetComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = iconRect.offsetMax = Vector2.zero;

            var iconText = iconObj.GetComponent<Text>();
            iconText.text = "❚❚";
            iconText.fontSize = pauseButtonIconSize;
            iconText.color = new Color(0.9f, 0.9f, 0.95f, 0.9f);
            iconText.alignment = TextAnchor.MiddleCenter;
            iconText.font = Font.CreateDynamicFontFromOSFont("Arial", pauseButtonIconSize);
            iconText.raycastTarget = false;

            // Button component
            var btn = btnObj.AddComponent<Button>();
            btn.targetGraphic = btnImage;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.8f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener(TogglePause);

            _pauseButtonObj = btnObj;
            Debug.Log("[ThresholdUI] Pause button created.");
        }
    }
}
