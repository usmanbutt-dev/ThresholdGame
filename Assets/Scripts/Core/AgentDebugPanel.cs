// ============================================================================
// AgentDebugPanel.cs — Runtime UI overlay for live agent trace visibility
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Toggle-able IMGUI panel that shows the latest trace from each of the
// 5 agents, a scrollable history, and an export button. Color-coded
// status: green=success, yellow=fallback/mock, red=failure.
// Only active in Debug/Development builds.
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using Threshold.Agents;
using UnityEngine;

namespace Threshold.Core
{
    /// <summary>
    /// Immediate-mode GUI debug panel for agent trace visibility.
    /// Attach to any persistent GameObject. Auto-hides in Release builds.
    /// </summary>
    public class AgentDebugPanel : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================

        [Header("Panel Settings")]
        [SerializeField] private KeyCode toggleKey = KeyCode.F1;
        [SerializeField] private bool startOpen = false;

        [Header("Layout")]
        [SerializeField] private float panelWidth = 520f;
        [SerializeField] private float panelHeight = 600f;
        [SerializeField] private int maxHistoryDisplay = 50;

        // State
        private bool _isOpen;
        private Vector2 _latestScroll;
        private Vector2 _historyScroll;
        private bool _showHistory;
        private string _lastExportPath;
        private float _lastExportTime;

        // Cached styles (created once in OnGUI)
        private GUIStyle _headerStyle;
        private GUIStyle _cardStyle;
        private GUIStyle _successStyle;
        private GUIStyle _warnStyle;
        private GUIStyle _errorStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _monoStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _sectionStyle;
        private bool _stylesInitialized;

        // Agent display names and order
        private static readonly string[] AgentNames = { "director", "level_gen", "npc_brain", "qc", "reward" };
        private static readonly string[] AgentLabels = { "🎯 Director", "🏗 Level Gen", "🧠 NPC Brain", "✅ QC Agent", "🏆 Reward" };

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            // Only enable in debug/dev builds
            if (!Debug.isDebugBuild && !Application.isEditor)
            {
                enabled = false;
                return;
            }
            _isOpen = startOpen;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
                _isOpen = !_isOpen;
        }

        // ====================================================================
        // Toggle Button (always visible in debug builds)
        // ====================================================================

        private void OnGUI()
        {
            if (!_stylesInitialized) InitStyles();

            // Toggle button in top-right corner
            float btnW = 140f, btnH = 30f;
            Rect btnRect = new Rect(Screen.width - btnW - 10, 10, btnW, btnH);

            var bridge = GeminiAgentBridge.Instance;
            int total = bridge != null ? bridge.TotalCalls : 0;
            string btnLabel = _isOpen ? $"◆ Traces ({total})" : $"◇ Traces ({total})";

            if (GUI.Button(btnRect, btnLabel, _buttonStyle))
                _isOpen = !_isOpen;

            if (!_isOpen) return;

            // Main panel
            float px = Screen.width - panelWidth - 10;
            float py = btnH + 20;
            Rect panelRect = new Rect(px, py, panelWidth, panelHeight);

            GUI.Box(panelRect, "", _cardStyle);
            GUILayout.BeginArea(new Rect(px + 10, py + 10, panelWidth - 20, panelHeight - 20));
            DrawPanel(bridge);
            GUILayout.EndArea();
        }

        // ====================================================================
        // Panel Content
        // ====================================================================

        private void DrawPanel(GeminiAgentBridge bridge)
        {
            // Header
            GUILayout.Label("THRESHOLD — Agent Traces", _headerStyle);
            GUILayout.Space(4);

            if (bridge == null)
            {
                GUILayout.Label("⚠ GeminiAgentBridge not found in scene.", _warnStyle);
                return;
            }

            // Status bar
            string mode = bridge.IsMockMode ? "MOCK" : "LIVE";
            Color modeColor = bridge.IsMockMode ? Color.yellow : Color.green;
            GUI.color = modeColor;
            GUILayout.Label($"Mode: {mode}  |  Calls: {bridge.TotalCalls}  |  " +
                            $"OK: {bridge.SuccessfulCalls}  Fail: {bridge.FailedCalls}  " +
                            $"Rate-limited: {bridge.RateLimitRejections}", _labelStyle);
            GUI.color = Color.white;

            GUILayout.Space(6);

            // Tab buttons
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_showHistory ? "Latest" : "▸ Latest", _buttonStyle, GUILayout.Width(80)))
                _showHistory = false;
            if (GUILayout.Button(_showHistory ? "▸ History" : "History", _buttonStyle, GUILayout.Width(80)))
                _showHistory = true;

            GUILayout.FlexibleSpace();

            // Export button
            if (GUILayout.Button("📁 Export JSON", _buttonStyle, GUILayout.Width(120)))
            {
                _lastExportPath = bridge.ExportTraces();
                _lastExportTime = Time.realtimeSinceStartup;
            }
            GUILayout.EndHorizontal();

            // Export feedback
            if (_lastExportPath != null && Time.realtimeSinceStartup - _lastExportTime < 5f)
            {
                GUI.color = Color.green;
                GUILayout.Label($"✓ Exported → {_lastExportPath}", _monoStyle);
                GUI.color = Color.white;
            }

            GUILayout.Space(6);

            if (_showHistory)
                DrawHistory(bridge);
            else
                DrawLatest(bridge);
        }

        // ====================================================================
        // Latest Tab: One card per agent
        // ====================================================================

        private void DrawLatest(GeminiAgentBridge bridge)
        {
            _latestScroll = GUILayout.BeginScrollView(_latestScroll);

            var latestTraces = bridge.GetLatestTracePerAgent();

            for (int i = 0; i < AgentNames.Length; i++)
            {
                string name = AgentNames[i];
                string label = AgentLabels[i];

                latestTraces.TryGetValue(name, out var entry);
                DrawTraceCard(label, entry, name == "qc");
                GUILayout.Space(4);
            }

            GUILayout.EndScrollView();
        }

        // ====================================================================
        // History Tab: Scrollable log of all traces
        // ====================================================================

        private void DrawHistory(GeminiAgentBridge bridge)
        {
            var log = bridge.TraceLog;
            int count = log.Count;
            int start = Mathf.Max(0, count - maxHistoryDisplay);

            GUILayout.Label($"Showing {Mathf.Min(count, maxHistoryDisplay)} of {count} traces", _labelStyle);
            GUILayout.Space(4);

            _historyScroll = GUILayout.BeginScrollView(_historyScroll);

            // Show newest first
            for (int i = count - 1; i >= start; i--)
            {
                var entry = log[i];
                DrawHistoryEntry(entry, count - i);
            }

            GUILayout.EndScrollView();
        }

        // ====================================================================
        // Card Rendering
        // ====================================================================

        private void DrawTraceCard(string label, AgentTraceEntry entry, bool isQC)
        {
            // Card background
            GUILayout.BeginVertical(_sectionStyle);

            if (entry == null)
            {
                GUI.color = new Color(0.5f, 0.5f, 0.5f);
                GUILayout.Label($"{label}  —  No data yet", _labelStyle);
                GUI.color = Color.white;
                GUILayout.EndVertical();
                return;
            }

            // Status color
            Color statusColor = GetStatusColor(entry);
            GUI.color = statusColor;

            // Agent header with latency
            string latency = $"{entry.latencyMs}ms";
            string timestamp = FormatTimestamp(entry.timestamp);
            GUILayout.Label($"{label}  |  {latency}  |  {timestamp}", _labelStyle);

            GUI.color = Color.white;

            // QC prominence
            if (isQC && entry.success && entry.trace != null)
            {
                string decision = entry.trace.decision ?? "";
                bool accepted = decision.ToUpperInvariant().Contains("ACCEPT");
                bool rejected = decision.ToUpperInvariant().Contains("REJECT");

                if (accepted)
                {
                    GUI.color = Color.green;
                    GUILayout.Label("  ✓ ACCEPTED", _headerStyle);
                }
                else if (rejected)
                {
                    GUI.color = Color.red;
                    GUILayout.Label("  ✗ REJECTED", _headerStyle);
                }
                GUI.color = Color.white;
            }

            // Decision summary
            if (entry.success && entry.trace != null)
            {
                string decision = Truncate(entry.trace.decision ?? "(no decision)", 180);
                GUILayout.Label(decision, _monoStyle);
            }
            else
            {
                GUI.color = Color.red;
                GUILayout.Label($"Error: {Truncate(entry.error ?? "unknown", 120)}", _monoStyle);
                GUI.color = Color.white;
            }

            GUILayout.EndVertical();
        }

        private void DrawHistoryEntry(AgentTraceEntry entry, int index)
        {
            Color statusColor = GetStatusColor(entry);
            GUI.color = statusColor;

            GUILayout.BeginVertical(_sectionStyle);

            string agentDisplay = AgentNames.Contains(entry.agentName)
                ? AgentLabels[System.Array.IndexOf(AgentNames, entry.agentName)]
                : entry.agentName;

            string header = $"#{index} {agentDisplay}  |  {entry.latencyMs}ms  |  " +
                            $"{FormatTimestamp(entry.timestamp)}  |  {entry.model}";
            GUILayout.Label(header, _labelStyle);

            GUI.color = Color.white;

            if (entry.success && entry.trace != null)
            {
                GUILayout.Label(Truncate(entry.trace.decision ?? "-", 160), _monoStyle);
            }
            else
            {
                GUI.color = new Color(1f, 0.5f, 0.5f);
                GUILayout.Label($"✗ {Truncate(entry.error ?? "unknown", 120)}", _monoStyle);
                GUI.color = Color.white;
            }

            GUILayout.EndVertical();
            GUILayout.Space(2);
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private Color GetStatusColor(AgentTraceEntry entry)
        {
            if (!entry.success) return new Color(1f, 0.3f, 0.3f);   // Red
            if (entry.trace?.observation?.Contains("[MOCK]") == true)
                return new Color(1f, 0.9f, 0.3f);                    // Yellow
            return new Color(0.3f, 1f, 0.5f);                        // Green
        }

        private static string FormatTimestamp(string iso)
        {
            if (string.IsNullOrEmpty(iso) || iso.Length < 19) return iso ?? "";
            // Show just HH:MM:SS from ISO timestamp
            return iso.Substring(11, 8);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ====================================================================
        // Style Initialization
        // ====================================================================

        private void InitStyles()
        {
            _stylesInitialized = true;

            // Semi-transparent dark background
            var bgTex = MakeTex(2, 2, new Color(0.08f, 0.08f, 0.12f, 0.92f));
            var sectionTex = MakeTex(2, 2, new Color(0.12f, 0.12f, 0.18f, 0.85f));

            _cardStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = bgTex }
            };

            _sectionStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = sectionTex },
                padding = new RectOffset(8, 8, 4, 4),
                margin = new RectOffset(0, 0, 2, 2)
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.95f, 1f) },
                padding = new RectOffset(0, 0, 2, 2)
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.8f, 0.85f, 0.9f) },
                wordWrap = true
            };

            _monoStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.7f, 0.75f, 0.8f) },
                wordWrap = true,
                padding = new RectOffset(8, 4, 2, 2)
            };

            _successStyle = new GUIStyle(_labelStyle) { normal = { textColor = Color.green } };
            _warnStyle = new GUIStyle(_labelStyle) { normal = { textColor = Color.yellow } };
            _errorStyle = new GUIStyle(_labelStyle) { normal = { textColor = Color.red } };

            var btnTex = MakeTex(2, 2, new Color(0.2f, 0.22f, 0.3f, 0.9f));
            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                normal = { background = btnTex, textColor = Color.white },
                hover = { textColor = new Color(0.5f, 0.8f, 1f) },
                padding = new RectOffset(8, 8, 4, 4)
            };
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
