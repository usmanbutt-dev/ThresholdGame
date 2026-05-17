// ============================================================================
// GeminiAgentBridge.cs — Singleton communication layer for all 5 Gemini agents
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Usage:
//   var response = await GeminiAgentBridge.Instance.SendAgentRequest(request);
//   if (response.success) { /* use response.trace */ }
//
// All calls are async (UnityWebRequest), never block the game thread.
// Every response is validated for the hackathon-mandated 5-step trace format.
// All calls are logged as AgentTraceEntry for export and debug panel.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Threshold.Agents
{
    /// <summary>
    /// MonoBehaviour singleton that handles all Gemini API communication.
    /// Attach to a persistent GameObject in the scene.
    /// API key is read from environment variable GEMINI_API_KEY or set via Inspector.
    /// </summary>
    public class GeminiAgentBridge : MonoBehaviour
    {
        // ====================================================================
        // Singleton
        // ====================================================================

        private static GeminiAgentBridge _instance;

        public static GeminiAgentBridge Instance
        {
            get
            {
                if (_instance == null)
                {
                    Debug.LogError("[GeminiAgentBridge] Instance not found. " +
                                   "Ensure a GeminiAgentBridge exists in the scene.");
                }
                return _instance;
            }
        }

        // ====================================================================
        // Configuration (Inspector)
        // ====================================================================

        [Header("API Configuration")]
        [Tooltip("Gemini API key. If empty, reads from GEMINI_API_KEY environment variable.")]
        [SerializeField] private string apiKey = "";

        [Tooltip("Base URL for the Gemini API (v1beta endpoint).")]
        [SerializeField] private string apiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

        [Header("Model Identifiers")]
        [SerializeField] private string flashModelId = "gemini-2.0-flash";
        [SerializeField] private string proModelId = "gemini-2.0-pro";

        [Header("Generation Defaults")]
        [SerializeField] private float defaultTemperature = 0.7f;
        [SerializeField] private int maxOutputTokens = 2048;

        [Header("Rate Limiting (Free Tier Protection)")]
        [Tooltip("Max Flash requests per minute. Free tier: 15 RPM.")]
        [SerializeField] private int flashRpmLimit = 15;
        [Tooltip("Max Flash requests per day. Free tier: 1500 RPD.")]
        [SerializeField] private int flashRpdLimit = 1500;
        [Tooltip("Max Pro requests per minute. Free tier: 2 RPM.")]
        [SerializeField] private int proRpmLimit = 2;
        [Tooltip("Max Pro requests per day. Free tier: 50 RPD.")]
        [SerializeField] private int proRpdLimit = 50;

        [Header("Development Mode")]
        [Tooltip("When enabled, returns mock responses instead of calling the API. Use during development to avoid burning API quota.")]
        [SerializeField] private bool useMockResponses = false;
        [Tooltip("Simulated latency range in ms for mock responses.")]
        [SerializeField] private int mockMinLatencyMs = 200;
        [SerializeField] private int mockMaxLatencyMs = 800;

        [Header("Debug")]
        [SerializeField] private bool logToConsole = true;
        [SerializeField] private int maxStoredTraces = 500;

        // ====================================================================
        // Trace Storage
        // ====================================================================

        private readonly List<AgentTraceEntry> _traceLog = new();
        private readonly Dictionary<string, AgentTraceEntry> _lastTraceByAgent = new();

        // Rate limiting state
        private readonly List<float> _flashCallTimestamps = new();
        private readonly List<float> _proCallTimestamps = new();
        private int _flashDailyCount;
        private int _proDailyCount;
        private int _dailyDate; // Day-of-year tracker for daily reset
        private int _rateLimitRejections;
        private int _mockCallsServed;

        /// <summary>Total number of API calls made this session.</summary>
        public int TotalCalls => _traceLog.Count;

        /// <summary>Number of successful API calls this session.</summary>
        public int SuccessfulCalls => _traceLog.Count(e => e.success);

        /// <summary>Number of failed API calls this session.</summary>
        public int FailedCalls => _traceLog.Count(e => !e.success);

        /// <summary>Whether mock mode is active (no real API calls).</summary>
        public bool IsMockMode => useMockResponses;

        /// <summary>Number of calls rejected by rate limiter this session.</summary>
        public int RateLimitRejections => _rateLimitRejections;

        /// <summary>Read-only access to the full trace log this session.</summary>
        public IReadOnlyList<AgentTraceEntry> TraceLog => _traceLog;

        /// <summary>Get the most recent trace for a specific agent.</summary>
        public AgentTraceEntry GetLastTrace(string agentName)
        {
            return _lastTraceByAgent.TryGetValue(agentName, out var entry) ? entry : null;
        }

        /// <summary>Get the most recent trace for each agent (for debug panel).</summary>
        public Dictionary<string, AgentTraceEntry> GetLatestTracePerAgent()
        {
            return new Dictionary<string, AgentTraceEntry>(_lastTraceByAgent);
        }

        /// <summary>
        /// Exports all session traces to a timestamped JSON file.
        /// Returns the file path on success, or null on failure.
        /// </summary>
        public string ExportTraces()
        {
            try
            {
                var export = new AgentTraceExport
                {
                    exportTimestamp = DateTime.UtcNow.ToString("o"),
                    totalCalls = _traceLog.Count,
                    successfulCalls = _traceLog.Count(e => e.success),
                    failedCalls = _traceLog.Count(e => !e.success),
                    entries = new List<AgentTraceEntry>(_traceLog)
                };

                string fileName = $"threshold_traces_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                string path = System.IO.Path.Combine(Application.persistentDataPath, fileName);
                System.IO.File.WriteAllText(path, JsonUtility.ToJson(export, true));

                Debug.Log($"[GeminiAgentBridge] Exported {export.totalCalls} traces → {path}");
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeminiAgentBridge] Export failed: {ex.Message}");
                return null;
            }
        }

        // ====================================================================
        // System prompt suffix appended to every agent call to enforce trace format.
        // ====================================================================

        private const string TraceFormatInstruction = @"

CRITICAL OUTPUT FORMAT REQUIREMENT:
You MUST respond with ONLY a valid JSON object containing exactly these 5 fields:
{
  ""observation"": ""<what you observed from the game state data>"",
  ""inference"": ""<what you conclude from those observations>"",
  ""decision"": ""<what action you chose and WHY>"",
  ""action"": ""<structured JSON string of the action to execute>"",
  ""evaluation_plan"": ""<what to measure next to check if this decision worked>""
}
Do NOT include any text outside the JSON object. Do NOT use markdown code fences.
All 5 fields are mandatory and must be non-empty strings.";

        // ====================================================================
        // Lifecycle
        // ====================================================================

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[GeminiAgentBridge] Duplicate instance destroyed.");
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            ResolveApiKey();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// Resolves the API key from Inspector field or environment variable.
        /// </summary>
        private void ResolveApiKey()
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Debug.LogWarning("[GeminiAgentBridge] No API key configured. " +
                                 "Set via Inspector or GEMINI_API_KEY environment variable.");
            }
            else if (logToConsole)
            {
                Debug.Log("[GeminiAgentBridge] API key loaded. " +
                         $"Flash: {flashModelId}, Pro: {proModelId}");
            }
        }

        // ====================================================================
        // Public API
        // ====================================================================

        /// <summary>
        /// Sends an agent request to the Gemini API asynchronously.
        /// Returns an AgentResponse with the parsed 5-step trace on success,
        /// or a failure response with error details. Never throws.
        /// </summary>
        public async Task<AgentResponse> SendAgentRequest(AgentRequest request)
        {
            if (request == null)
            {
                return AgentResponse.Failure("unknown", "AgentRequest is null.");
            }

            // --- Mock mode: return fake response, never hit API ---
            if (useMockResponses)
            {
                return await ServeMockResponse(request);
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return AgentResponse.Failure(request.agentName, "No API key configured.");
            }

            // --- Rate limit check ---
            string rateLimitError = CheckRateLimit(request.model);
            if (rateLimitError != null)
            {
                _rateLimitRejections++;
                var rlResponse = AgentResponse.Failure(request.agentName, rateLimitError);
                LogTrace(request, rlResponse);
                return rlResponse;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Build the request
                string modelId = request.model == GeminiModel.Flash ? flashModelId : proModelId;
                string url = $"{apiBaseUrl}/{modelId}:generateContent?key={apiKey}";
                string body = BuildRequestBody(request);

                // Record this call for rate limiting
                RecordApiCall(request.model);

                if (logToConsole)
                {
                    Debug.Log($"[GeminiAgentBridge] → {request.agentName} " +
                             $"({request.model}) sending request...");
                }

                // Send HTTP request
                string responseText = await SendHttpRequest(url, body, request.timeoutSeconds);
                stopwatch.Stop();

                // Parse the Gemini API response envelope
                string content = ExtractContentFromResponse(responseText);

                // Parse and validate the 5-step trace
                AgentTrace trace = ParseTrace(content);

                if (trace == null || !trace.IsValid())
                {
                    var failResponse = AgentResponse.Failure(
                        request.agentName,
                        $"Invalid trace format. Missing required fields. Raw: {Truncate(content, 300)}",
                        stopwatch.ElapsedMilliseconds
                    );
                    LogTrace(request, failResponse);
                    return failResponse;
                }

                var successResponse = AgentResponse.Success(
                    request.agentName, trace, content, stopwatch.ElapsedMilliseconds
                );
                LogTrace(request, successResponse);

                if (logToConsole)
                {
                    Debug.Log($"[GeminiAgentBridge] ✓ {request.agentName} " +
                             $"responded in {stopwatch.ElapsedMilliseconds}ms");
                }

                return successResponse;
            }
            catch (TimeoutException)
            {
                stopwatch.Stop();
                var response = AgentResponse.Failure(
                    request.agentName,
                    $"Request timed out after {request.timeoutSeconds}s.",
                    stopwatch.ElapsedMilliseconds
                );
                LogTrace(request, response);
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var response = AgentResponse.Failure(
                    request.agentName,
                    $"Unexpected error: {ex.Message}",
                    stopwatch.ElapsedMilliseconds
                );
                LogTrace(request, response);
                Debug.LogError($"[GeminiAgentBridge] ✗ {request.agentName} error: {ex.Message}");
                return response;
            }
        }



        /// <summary>
        /// Clears all stored traces. Useful between test sessions.
        /// </summary>
        public void ClearTraces()
        {
            _traceLog.Clear();
            _lastTraceByAgent.Clear();
        }

        /// <summary>
        /// Checks whether the API key is configured and non-empty.
        /// In mock mode, always returns true.
        /// </summary>
        public bool IsConfigured => useMockResponses || !string.IsNullOrWhiteSpace(apiKey);

        /// <summary>
        /// Returns a human-readable usage report for monitoring quota.
        /// </summary>
        public string GetUsageReport()
        {
            ResetDailyCountIfNeeded();
            return $"[Usage Report]\n" +
                   $"  Mode: {(useMockResponses ? "MOCK (no API calls)" : "LIVE")}\n" +
                   $"  Flash: {_flashDailyCount}/{flashRpdLimit} daily, {GetRecentCallCount(_flashCallTimestamps)}/{flashRpmLimit} RPM\n" +
                   $"  Pro:   {_proDailyCount}/{proRpdLimit} daily, {GetRecentCallCount(_proCallTimestamps)}/{proRpmLimit} RPM\n" +
                   $"  Total calls: {TotalCalls} (✓{SuccessfulCalls} ✗{FailedCalls})\n" +
                   $"  Rate limit rejections: {_rateLimitRejections}\n" +
                   $"  Mock calls served: {_mockCallsServed}";
        }

        // ====================================================================
        // Internal: HTTP
        // ====================================================================

        /// <summary>
        /// Sends an HTTP POST request using UnityWebRequest.
        /// Runs on the main thread but yields asynchronously.
        /// </summary>
        private async Task<string> SendHttpRequest(string url, string jsonBody, int timeoutSeconds)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

            using var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = timeoutSeconds;

            var operation = request.SendWebRequest();

            // Await the async operation without blocking the main thread
            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                throw new Exception($"Network error: {request.error}");
            }

            if (request.result == UnityWebRequest.Result.DataProcessingError)
            {
                throw new Exception($"Data processing error: {request.error}");
            }

            // Check for timeout (UnityWebRequest sets error on timeout)
            if (request.error != null && request.error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                throw new TimeoutException($"Request timed out after {timeoutSeconds}s.");
            }

            string responseText = request.downloadHandler.text;

            // Check for HTTP error responses (4xx, 5xx)
            if (request.responseCode >= 400)
            {
                string errorDetail = TryExtractApiError(responseText);
                throw new Exception($"HTTP {request.responseCode}: {errorDetail}");
            }

            return responseText;
        }

        // ====================================================================
        // Internal: Request Building
        // ====================================================================

        /// <summary>
        /// Builds the Gemini API request body JSON with system prompt,
        /// trace format instruction, and game state.
        /// </summary>
        private string BuildRequestBody(AgentRequest request)
        {
            var apiRequest = new GeminiApiRequest
            {
                system_instruction = new GeminiSystemInstruction
                {
                    parts = new[]
                    {
                        new GeminiPart { text = request.systemPrompt + TraceFormatInstruction }
                    }
                },
                contents = new[]
                {
                    new GeminiContent
                    {
                        role = "user",
                        parts = new[]
                        {
                            new GeminiPart
                            {
                                text = $"Current game state:\n{request.gameStateJson}"
                            }
                        }
                    }
                },
                generationConfig = new GeminiGenerationConfig
                {
                    temperature = defaultTemperature,
                    maxOutputTokens = maxOutputTokens,
                    responseMimeType = "application/json"
                }
            };

            return JsonUtility.ToJson(apiRequest);
        }

        // ====================================================================
        // Internal: Response Parsing
        // ====================================================================

        /// <summary>
        /// Extracts the text content from a Gemini API response envelope.
        /// </summary>
        private string ExtractContentFromResponse(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                throw new Exception("Empty response from Gemini API.");
            }

            var apiResponse = JsonUtility.FromJson<GeminiApiResponse>(responseJson);

            if (apiResponse.error != null && !string.IsNullOrEmpty(apiResponse.error.message))
            {
                throw new Exception($"Gemini API error ({apiResponse.error.code}): " +
                                   $"{apiResponse.error.message}");
            }

            if (apiResponse.candidates == null || apiResponse.candidates.Length == 0)
            {
                throw new Exception("Gemini API returned no candidates.");
            }

            var content = apiResponse.candidates[0]?.content;
            if (content?.parts == null || content.parts.Length == 0)
            {
                throw new Exception("Gemini API candidate has no content parts.");
            }

            return content.parts[0].text;
        }

        /// <summary>
        /// Parses the 5-step trace from the response text.
        /// Handles both clean JSON and JSON embedded in markdown fences.
        /// </summary>
        private AgentTrace ParseTrace(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            // Strip markdown code fences if present
            string cleaned = text.Trim();
            if (cleaned.StartsWith("```"))
            {
                int firstNewline = cleaned.IndexOf('\n');
                if (firstNewline > 0)
                    cleaned = cleaned.Substring(firstNewline + 1);

                if (cleaned.EndsWith("```"))
                    cleaned = cleaned.Substring(0, cleaned.Length - 3);

                cleaned = cleaned.Trim();
            }

            try
            {
                return JsonUtility.FromJson<AgentTrace>(cleaned);
            }
            catch (Exception ex)
            {
                if (logToConsole)
                {
                    Debug.LogWarning($"[GeminiAgentBridge] Failed to parse trace JSON: {ex.Message}\n" +
                                    $"Raw content: {Truncate(cleaned, 200)}");
                }
                return null;
            }
        }

        /// <summary>
        /// Tries to extract a readable error message from an API error response.
        /// </summary>
        private string TryExtractApiError(string responseBody)
        {
            try
            {
                var error = JsonUtility.FromJson<GeminiApiResponse>(responseBody);
                if (error?.error != null)
                {
                    return $"{error.error.status}: {error.error.message}";
                }
            }
            catch
            {
                // Fall through to return raw body
            }

            return Truncate(responseBody, 200);
        }

        // ====================================================================
        // Internal: Trace Logging
        // ====================================================================

        /// <summary>
        /// Records a trace entry for both the sequential log and per-agent lookup.
        /// </summary>
        private void LogTrace(AgentRequest request, AgentResponse response)
        {
            var entry = new AgentTraceEntry
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                agentName = request.agentName,
                model = request.model.ToString(),
                latencyMs = response.latencyMs,
                success = response.success,
                trace = response.trace,
                error = response.error
            };

            _traceLog.Add(entry);
            _lastTraceByAgent[request.agentName] = entry;

            // Evict oldest entries if over capacity
            while (_traceLog.Count > maxStoredTraces)
            {
                _traceLog.RemoveAt(0);
            }

            if (logToConsole && !response.success)
            {
                Debug.LogWarning($"[GeminiAgentBridge] ✗ {request.agentName}: {response.error}");
            }
        }

        // ====================================================================
        // Rate Limiting
        // ====================================================================

        /// <summary>
        /// Checks if the request would exceed free-tier rate limits.
        /// Returns null if OK, or an error message if blocked.
        /// </summary>
        private string CheckRateLimit(GeminiModel model)
        {
            ResetDailyCountIfNeeded();

            if (model == GeminiModel.Flash)
            {
                if (_flashDailyCount >= flashRpdLimit)
                    return $"Flash daily limit reached ({flashRpdLimit} RPD). Wait until tomorrow or enable mock mode.";
                if (GetRecentCallCount(_flashCallTimestamps) >= flashRpmLimit)
                    return $"Flash rate limit reached ({flashRpmLimit} RPM). Wait ~60s or enable mock mode.";
            }
            else
            {
                if (_proDailyCount >= proRpdLimit)
                    return $"Pro daily limit reached ({proRpdLimit} RPD). Wait until tomorrow or enable mock mode.";
                if (GetRecentCallCount(_proCallTimestamps) >= proRpmLimit)
                    return $"Pro rate limit reached ({proRpmLimit} RPM). Wait ~60s or enable mock mode.";
            }

            return null;
        }

        /// <summary>
        /// Records a successful API call for rate limiting.
        /// </summary>
        private void RecordApiCall(GeminiModel model)
        {
            float now = Time.realtimeSinceStartup;

            if (model == GeminiModel.Flash)
            {
                _flashCallTimestamps.Add(now);
                _flashDailyCount++;
            }
            else
            {
                _proCallTimestamps.Add(now);
                _proDailyCount++;
            }
        }

        /// <summary>
        /// Counts API calls in the last 60 seconds (RPM window).
        /// Also prunes old timestamps.
        /// </summary>
        private int GetRecentCallCount(List<float> timestamps)
        {
            float cutoff = Time.realtimeSinceStartup - 60f;
            timestamps.RemoveAll(t => t < cutoff);
            return timestamps.Count;
        }

        /// <summary>
        /// Resets daily counters if the calendar day has changed.
        /// </summary>
        private void ResetDailyCountIfNeeded()
        {
            int today = DateTime.UtcNow.DayOfYear;
            if (_dailyDate != today)
            {
                _dailyDate = today;
                _flashDailyCount = 0;
                _proDailyCount = 0;
                if (logToConsole)
                    Debug.Log("[GeminiAgentBridge] Daily rate limit counters reset.");
            }
        }

        // ====================================================================
        // Mock Response System
        // ====================================================================

        /// <summary>
        /// Returns a realistic mock response without calling the API.
        /// Traces are agent-specific so the debug panel shows meaningful data.
        /// </summary>
        private async Task<AgentResponse> ServeMockResponse(AgentRequest request)
        {
            var stopwatch = Stopwatch.StartNew();

            // Simulate network latency
            int fakeLatency = UnityEngine.Random.Range(mockMinLatencyMs, mockMaxLatencyMs);
            await Task.Delay(fakeLatency);
            stopwatch.Stop();

            AgentTrace trace = GenerateMockTrace(request.agentName);
            var response = AgentResponse.Success(
                request.agentName, trace, "[MOCK]", stopwatch.ElapsedMilliseconds
            );
            LogTrace(request, response);
            _mockCallsServed++;

            if (logToConsole)
            {
                Debug.Log($"[GeminiAgentBridge] ⚡ MOCK {request.agentName} " +
                         $"responded in {stopwatch.ElapsedMilliseconds}ms (no API call)");
            }

            return response;
        }

        /// <summary>
        /// Generates a plausible mock trace for a given agent.
        /// </summary>
        private AgentTrace GenerateMockTrace(string agentName)
        {
            return agentName switch
            {
                "director" => new AgentTrace
                {
                    observation = "[MOCK] Player completed 6 rooms, 2 deaths, 72% accuracy, 22s avg room time.",
                    inference = "[MOCK] Player is mid-skill. 2-loss streak suggests slight frustration. Accuracy is decent but room times are slow.",
                    decision = "[MOCK] Reduce difficulty by 0.15x, keep room count at 7, swap one ELITE for GRUNT. Maintain engagement.",
                    action = "{\"difficulty_multiplier\": 1.05, \"room_count\": 7, \"elite_count\": 0, \"grunt_count\": 4, \"npc_tactic\": \"ATTACK\"}",
                    evaluation_plan = "[MOCK] Monitor: if next run deaths < 2 and room time < 20s, calibration is correct. If deaths > 3, reduce further."
                },
                "level_gen" => new AgentTrace
                {
                    observation = "[MOCK] Difficulty 1.05x, 7 rooms requested, player prefers linear paths.",
                    inference = "[MOCK] Player struggles with branching layouts. Linear with one optional branch balances challenge and exploration.",
                    decision = "[MOCK] Generate 5 STRAIGHT + 1 T-JUNCTION + 1 DEAD END. Place LOOT in dead end as optional reward.",
                    action = "{\"rooms\": [\"STRAIGHT\",\"STRAIGHT\",\"T-JUNCTION\",\"STRAIGHT\",\"DEAD END\",\"STRAIGHT\",\"STRAIGHT\"], \"roles\": [\"ENTRY\",\"COMBAT\",\"COMBAT\",\"PACING\",\"LOOT\",\"COMBAT\",\"EXIT\"]}",
                    evaluation_plan = "[MOCK] Track if player explores the DEAD END branch. If yes, increase branching in future runs."
                },
                "npc_brain" => new AgentTrace
                {
                    observation = "[MOCK] Player is stationary behind cover. Kill streak 3. NPC-02 health 85%. 4 enemies remain.",
                    inference = "[MOCK] Player is camping a strong position. Direct ATTACK is ineffective. FLANK would exploit blind spot.",
                    decision = "[MOCK] Set NPC-02 to FLANK. NPC-01 and NPC-03 maintain SUPPRESS to pin player.",
                    action = "{\"npc_02\": \"FLANK\", \"npc_01\": \"SUPPRESS\", \"npc_03\": \"SUPPRESS\", \"npc_04\": \"PATROL\"}",
                    evaluation_plan = "[MOCK] If player repositions within 10s, FLANK was effective. If player eliminates NPC-02, reconsider."
                },
                "qc" => new AgentTrace
                {
                    observation = "[MOCK] Level config: 7 rooms, 1 branch, 18 spawn points. All rooms have valid exits.",
                    inference = "[MOCK] Layout is solvable. Spawn density is within bounds. No overlapping spawn zones detected.",
                    decision = "[MOCK] ACCEPT — all validation checks passed.",
                    action = "{\"status\": \"ACCEPTED\", \"failures\": [], \"validation_checks\": 12, \"passed\": 12}",
                    evaluation_plan = "[MOCK] Log player completion rate for this layout. If < 40%, flag for review."
                },
                "reward" => new AgentTrace
                {
                    observation = "[MOCK] Run completed. 18 kills, 72% accuracy, 2 deaths, no exploits detected.",
                    inference = "[MOCK] Moderate effort, clean play. Player improved accuracy from 65% last run.",
                    decision = "[MOCK] Award base XP + 15% improvement bonus. Suggest challenge: clear 2 rooms without damage.",
                    action = "{\"xp\": 240, \"bonus_multiplier\": 1.15, \"challenge\": \"Clear 2 rooms without taking damage for 2x XP\"}",
                    evaluation_plan = "[MOCK] Track if player accepts the challenge next run. If yes, monitor completion rate."
                },
                _ => new AgentTrace
                {
                    observation = $"[MOCK] Generic observation for agent '{agentName}'.",
                    inference = $"[MOCK] Generic inference for agent '{agentName}'.",
                    decision = $"[MOCK] Generic decision for agent '{agentName}'.",
                    action = "{\"mock\": true}",
                    evaluation_plan = $"[MOCK] Generic evaluation plan for agent '{agentName}'."
                }
            };
        }

        // ====================================================================
        // Utility
        // ====================================================================

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }
    }
}
