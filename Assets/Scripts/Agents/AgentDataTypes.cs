// ============================================================================
// AgentDataTypes.cs — Shared data structures for the Gemini Agent system
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Threshold.Agents
{
    /// <summary>
    /// Which LLM provider to use for agent calls.
    /// Toggle in the GeminiAgentBridge Inspector.
    /// </summary>
    public enum LLMProvider
    {
        Gemini,
        Groq,
        Nvidia
    }

    /// <summary>
    /// Gemini model tier selection. Flash for low-latency calls (NPC Brain),
    /// Pro for complex reasoning (Director, Level Gen, QC, Reward).
    /// </summary>
    public enum GeminiModel
    {
        Flash,
        Pro
    }

    /// <summary>
    /// Request payload sent to the Gemini API via GeminiAgentBridge.
    /// </summary>
    [Serializable]
    public class AgentRequest
    {
        /// <summary>Display name of the calling agent (e.g. "director", "npc_brain").</summary>
        public string agentName;

        /// <summary>System prompt defining the agent's identity, role, and output format.</summary>
        public string systemPrompt;

        /// <summary>JSON string of the current game state to reason over.</summary>
        public string gameStateJson;

        /// <summary>Which Gemini model to use for this call.</summary>
        public GeminiModel model;

        /// <summary>Request timeout in seconds. Default 15s for Flash, 20s for Pro.</summary>
        public int timeoutSeconds;

        public AgentRequest(string agentName, string systemPrompt, string gameStateJson,
                            GeminiModel model = GeminiModel.Flash, int timeoutSeconds = -1)
        {
            this.agentName = agentName;
            this.systemPrompt = systemPrompt;
            this.gameStateJson = gameStateJson;
            this.model = model;
            // Default timeout: 15s for Flash, 45s for Pro (49B Nemotron needs warm-up on free tier)
            this.timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : (model == GeminiModel.Flash ? 15 : 45);
        }
    }

    /// <summary>
    /// Parsed response from the Gemini API, including the validated 5-step trace.
    /// </summary>
    [Serializable]
    public class AgentResponse
    {
        /// <summary>Whether the API call succeeded and produced a valid trace.</summary>
        public bool success;

        /// <summary>The parsed 5-step agent reasoning trace. Null on failure.</summary>
        public AgentTrace trace;

        /// <summary>Raw text content from the Gemini response.</summary>
        public string rawContent;

        /// <summary>Error message if the call failed. Empty on success.</summary>
        public string error;

        /// <summary>Round-trip latency in milliseconds.</summary>
        public long latencyMs;

        /// <summary>Name of the agent that made this call.</summary>
        public string agentName;

        public static AgentResponse Success(string agentName, AgentTrace trace, string rawContent, long latencyMs)
        {
            return new AgentResponse
            {
                success = true,
                agentName = agentName,
                trace = trace,
                rawContent = rawContent,
                error = string.Empty,
                latencyMs = latencyMs
            };
        }

        public static AgentResponse Failure(string agentName, string error, long latencyMs = 0)
        {
            return new AgentResponse
            {
                success = false,
                agentName = agentName,
                trace = null,
                rawContent = string.Empty,
                error = error,
                latencyMs = latencyMs
            };
        }
    }

    /// <summary>
    /// The hackathon-mandated 5-step reasoning trace.
    /// Every Gemini call must return this structure for judge evaluation.
    /// </summary>
    [Serializable]
    public class AgentTrace
    {
        /// <summary>Step 1: Raw game-state data the agent observed.</summary>
        public string observation;

        /// <summary>Step 2: What the agent concludes (skill level, risk, pattern).</summary>
        public string inference;

        /// <summary>Step 3: Chosen action with WHY explanation.</summary>
        public string decision;

        /// <summary>Step 4: Structured JSON output consumed by Unity systems.</summary>
        public string action;

        /// <summary>Step 5: What to measure next to verify the decision worked.</summary>
        public string evaluation_plan;

        /// <summary>
        /// Validates that all 5 trace fields are present and non-empty.
        /// </summary>
        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(observation)
                && !string.IsNullOrWhiteSpace(inference)
                && !string.IsNullOrWhiteSpace(decision)
                && !string.IsNullOrWhiteSpace(action)
                && !string.IsNullOrWhiteSpace(evaluation_plan);
        }
    }

    /// <summary>
    /// A timestamped log entry for a single agent call, used for trace export
    /// and the in-game debug panel.
    /// </summary>
    [Serializable]
    public class AgentTraceEntry
    {
        /// <summary>ISO 8601 timestamp of when the call was initiated.</summary>
        public string timestamp;

        /// <summary>Name of the agent that made the call.</summary>
        public string agentName;

        /// <summary>Gemini model used (Flash or Pro).</summary>
        public string model;

        /// <summary>Round-trip latency in milliseconds.</summary>
        public long latencyMs;

        /// <summary>Whether the call succeeded.</summary>
        public bool success;

        /// <summary>The 5-step trace (null on failure).</summary>
        public AgentTrace trace;

        /// <summary>Error message if call failed.</summary>
        public string error;
    }

    /// <summary>
    /// Wrapper for serialising a list of trace entries to JSON for export.
    /// </summary>
    [Serializable]
    public class AgentTraceExport
    {
        public string gameTitle = "THRESHOLD";
        public string teamName = "The Aethers";
        public string exportTimestamp;
        public int totalCalls;
        public int successfulCalls;
        public int failedCalls;
        public List<AgentTraceEntry> entries;
    }

    // ========================================================================
    // Gemini API JSON structures (for serialisation/deserialisation)
    // ========================================================================

    /// <summary>
    /// Request body sent to the Gemini REST API.
    /// </summary>
    [Serializable]
    public class GeminiApiRequest
    {
        public GeminiContent[] contents;
        public GeminiSystemInstruction system_instruction;
        public GeminiGenerationConfig generationConfig;
    }

    [Serializable]
    public class GeminiSystemInstruction
    {
        public GeminiPart[] parts;
    }

    [Serializable]
    public class GeminiContent
    {
        public string role;
        public GeminiPart[] parts;
    }

    [Serializable]
    public class GeminiPart
    {
        public string text;
    }

    [Serializable]
    public class GeminiGenerationConfig
    {
        public float temperature;
        public int maxOutputTokens;
        public string responseMimeType;
    }

    /// <summary>
    /// Top-level response from the Gemini REST API.
    /// </summary>
    [Serializable]
    public class GeminiApiResponse
    {
        public GeminiCandidate[] candidates;
        public GeminiError error;
    }

    [Serializable]
    public class GeminiCandidate
    {
        public GeminiContent content;
    }

    [Serializable]
    public class GeminiError
    {
        public int code;
        public string message;
        public string status;
    }

    // ========================================================================
    // Groq API JSON structures (OpenAI-compatible chat completions format)
    // ========================================================================

    /// <summary>
    /// Request body sent to the Groq REST API (OpenAI chat completions format).
    /// </summary>
    [Serializable]
    public class GroqApiRequest
    {
        public string model;
        public GroqMessage[] messages;
        public float temperature;
        public int max_tokens;
        public GroqResponseFormat response_format;
    }

    [Serializable]
    public class GroqMessage
    {
        public string role;
        public string content;
    }

    [Serializable]
    public class GroqResponseFormat
    {
        public string type; // "json_object"
    }

    /// <summary>
    /// Top-level response from the Groq REST API.
    /// </summary>
    [Serializable]
    public class GroqApiResponse
    {
        public GroqChoice[] choices;
        public GroqUsage usage;
        public GroqError error;
    }

    [Serializable]
    public class GroqChoice
    {
        public GroqChoiceMessage message;
        public string finish_reason;
    }

    [Serializable]
    public class GroqChoiceMessage
    {
        public string role;
        public string content;
    }

    [Serializable]
    public class GroqUsage
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int total_tokens;
    }

    [Serializable]
    public class GroqError
    {
        public string message;
        public string type;
        public string code;
    }
}
