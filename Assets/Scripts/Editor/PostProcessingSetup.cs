// ============================================================================
// PostProcessingSetup.cs — Automated URP Post-Processing Configuration
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Creates a Volume Profile with recommended effects, spawns a Global Volume
// in the active scene, configures the camera, and adds SSAO to the renderer.
// Run via: Tools > THRESHOLD > Setup Post Processing
// ============================================================================

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.IO;
using System.Linq;

namespace Threshold.Editor
{
    public static class PostProcessingSetup
    {
        private const string ProfilePath = "Assets/Settings/GamePostProcessProfile.asset";

        // ====================================================================
        // Menu Entry
        // ====================================================================

        [MenuItem("Tools/THRESHOLD/Setup Post Processing")]
        public static void Setup()
        {
            Debug.Log("[PostProcessing] Starting post-processing setup...");

            // 1. Create or load Volume Profile
            var profile = CreateOrLoadProfile();

            // 2. Configure all overrides
            ConfigureBloom(profile);
            ConfigureColorAdjustments(profile);
            ConfigureTonemapping(profile);
            ConfigureVignette(profile);
            ConfigureFilmGrain(profile);

            // Save profile
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            // 3. Create Global Volume in scene
            CreateGlobalVolume(profile);

            // 4. Configure all cameras
            ConfigureCameras();

            // 5. Add SSAO renderer feature
            AddSSAOToRenderers();

            Debug.Log("[PostProcessing] ✅ Post-processing setup complete!");
        }

        // ====================================================================
        // Volume Profile
        // ====================================================================

        private static VolumeProfile CreateOrLoadProfile()
        {
            // Check if profile already exists
            var existing = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (existing != null)
            {
                Debug.Log("[PostProcessing] Found existing profile, updating...");
                return existing;
            }

            // Ensure directory exists
            string dir = Path.GetDirectoryName(ProfilePath);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                AssetDatabase.CreateFolder("Assets", "Settings");
            }

            // Create new profile
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
            Debug.Log($"[PostProcessing] Created Volume Profile at {ProfilePath}");
            return profile;
        }

        // ====================================================================
        // Effect: Bloom
        // ====================================================================

        private static void ConfigureBloom(VolumeProfile profile)
        {
            var bloom = GetOrAddOverride<Bloom>(profile);

            bloom.intensity.overrideState = true;
            bloom.intensity.value = 0.8f;

            bloom.threshold.overrideState = true;
            bloom.threshold.value = 0.9f;

            bloom.scatter.overrideState = true;
            bloom.scatter.value = 0.7f;

            bloom.tint.overrideState = true;
            bloom.tint.value = new Color(0.753f, 0.91f, 1f, 1f); // #C0E8FF — ice blue

            bloom.highQualityFiltering.overrideState = true;
            bloom.highQualityFiltering.value = true;

            Debug.Log("[PostProcessing]   ✓ Bloom configured");
        }

        // ====================================================================
        // Effect: Color Adjustments
        // ====================================================================

        private static void ConfigureColorAdjustments(VolumeProfile profile)
        {
            var color = GetOrAddOverride<ColorAdjustments>(profile);

            color.postExposure.overrideState = true;
            color.postExposure.value = 0.3f;

            color.contrast.overrideState = true;
            color.contrast.value = 25f;

            color.colorFilter.overrideState = true;
            color.colorFilter.value = new Color(0.91f, 0.94f, 1f, 1f); // #E8F0FF — subtle cool tint

            color.saturation.overrideState = true;
            color.saturation.value = -15f;

            color.hueShift.overrideState = true;
            color.hueShift.value = 0f;

            Debug.Log("[PostProcessing]   ✓ Color Adjustments configured");
        }

        // ====================================================================
        // Effect: Tonemapping
        // ====================================================================

        private static void ConfigureTonemapping(VolumeProfile profile)
        {
            var tonemap = GetOrAddOverride<Tonemapping>(profile);

            tonemap.mode.overrideState = true;
            tonemap.mode.value = TonemappingMode.ACES;

            Debug.Log("[PostProcessing]   ✓ Tonemapping configured (ACES)");
        }

        // ====================================================================
        // Effect: Vignette
        // ====================================================================

        private static void ConfigureVignette(VolumeProfile profile)
        {
            var vignette = GetOrAddOverride<Vignette>(profile);

            vignette.intensity.overrideState = true;
            vignette.intensity.value = 0.3f;

            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = 0.4f;

            vignette.rounded.overrideState = true;
            vignette.rounded.value = true;

            Debug.Log("[PostProcessing]   ✓ Vignette configured");
        }

        // ====================================================================
        // Effect: Film Grain
        // ====================================================================

        private static void ConfigureFilmGrain(VolumeProfile profile)
        {
            var grain = GetOrAddOverride<FilmGrain>(profile);

            grain.type.overrideState = true;
            grain.type.value = FilmGrainLookup.Thin1;

            grain.intensity.overrideState = true;
            grain.intensity.value = 0.15f;

            grain.response.overrideState = true;
            grain.response.value = 0.8f;

            Debug.Log("[PostProcessing]   ✓ Film Grain configured");
        }

        // ====================================================================
        // Global Volume (Scene)
        // ====================================================================

        private static void CreateGlobalVolume(VolumeProfile profile)
        {
            // Check if a Global Volume with our profile already exists
            var existingVolumes = Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
            foreach (var v in existingVolumes)
            {
                if (v.gameObject.name == "PostProcess_Volume")
                {
                    v.sharedProfile = profile;
                    v.isGlobal = true;
                    Debug.Log("[PostProcessing]   ✓ Updated existing PostProcess_Volume");
                    return;
                }
            }

            // Create new Global Volume
            var volumeObj = new GameObject("PostProcess_Volume");
            var volume = volumeObj.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;
            volume.priority = 1f;

            // Mark as not destroyable if in a persistent scene
            Undo.RegisterCreatedObjectUndo(volumeObj, "Create PostProcess Volume");

            Debug.Log("[PostProcessing]   ✓ Created Global Volume in scene");
        }

        // ====================================================================
        // Camera Configuration
        // ====================================================================

        private static void ConfigureCameras()
        {
            int configured = 0;
            var cameras = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);

            foreach (var cam in cameras)
            {
                var urpData = cam.GetUniversalAdditionalCameraData();
                if (urpData == null) continue;

                Undo.RecordObject(urpData, "Configure Camera Post Processing");

                // Enable post-processing
                urpData.renderPostProcessing = true;

                // Anti-aliasing: SMAA for quality, FXAA as fallback
                urpData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                urpData.antialiasingQuality = AntialiasingQuality.Medium;

                // Dithering to reduce color banding
                urpData.dithering = true;

                EditorUtility.SetDirty(urpData);
                configured++;
            }

            if (configured > 0)
            {
                Debug.Log($"[PostProcessing]   ✓ Configured {configured} camera(s): " +
                          "Post Processing ON, SMAA, Dithering ON");
            }
            else
            {
                Debug.LogWarning("[PostProcessing]   ⚠ No cameras found in scene. " +
                    "Post-processing will activate when the game spawns a camera.");
            }
        }

        // ====================================================================
        // SSAO Renderer Feature
        // ====================================================================

        private static void AddSSAOToRenderers()
        {
            string[] rendererPaths =
            {
                "Assets/Settings/Mobile_Renderer.asset",
                "Assets/Settings/PC_Renderer.asset"
            };

            foreach (var path in rendererPaths)
            {
                var rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(path);
                if (rendererData == null)
                {
                    Debug.LogWarning($"[PostProcessing]   ⚠ Renderer not found: {path}");
                    continue;
                }

                // Check if SSAO already exists
                bool hasSSAO = rendererData.rendererFeatures.Any(f =>
                    f != null && f.GetType().Name.Contains("ScreenSpaceAmbientOcclusion"));

                if (hasSSAO)
                {
                    Debug.Log($"[PostProcessing]   ✓ SSAO already exists on {Path.GetFileName(path)}");
                    continue;
                }

                try
                {
                    AddSSAOFeature(rendererData, path);
                    Debug.Log($"[PostProcessing]   ✓ SSAO added to {Path.GetFileName(path)}");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[PostProcessing]   ⚠ Could not auto-add SSAO to {Path.GetFileName(path)}: {ex.Message}\n" +
                        "Add it manually: Select the Renderer Asset → Add Renderer Feature → Screen Space Ambient Occlusion");
                }
            }
        }

        private static void AddSSAOFeature(ScriptableRendererData rendererData, string assetPath)
        {
            // Create the SSAO feature instance
            var ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
            ssao.name = "ScreenSpaceAmbientOcclusion";

            // Configure SSAO settings via SerializedObject
            // (settings struct is internal, so we use serialization)
            var soFeature = new SerializedObject(ssao);

            // Try to find and set common SSAO properties
            SetSerializedFloat(soFeature, "m_Settings.Intensity", 1.5f);
            SetSerializedFloat(soFeature, "m_Settings.Radius", 0.3f);
            SetSerializedBool(soFeature, "m_Settings.Downsample", true);

            soFeature.ApplyModifiedPropertiesWithoutUndo();

            // Add as sub-asset of the renderer data
            AssetDatabase.AddObjectToAsset(ssao, assetPath);

            // Add to the renderer features list via SerializedObject
            var so = new SerializedObject(rendererData);
            var featuresArray = so.FindProperty("m_RendererFeatures");
            int index = featuresArray.arraySize;
            featuresArray.arraySize++;
            featuresArray.GetArrayElementAtIndex(index).objectReferenceValue = ssao;

            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        /// <summary>
        /// Gets an existing override from the profile or adds a new one.
        /// </summary>
        private static T GetOrAddOverride<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet<T>(out var existing))
            {
                return existing;
            }
            return profile.Add<T>();
        }

        /// <summary>
        /// Safely sets a float property on a SerializedObject by path.
        /// </summary>
        private static void SetSerializedFloat(SerializedObject so, string path, float value)
        {
            var prop = so.FindProperty(path);
            if (prop != null)
            {
                prop.floatValue = value;
            }
        }

        /// <summary>
        /// Safely sets a bool property on a SerializedObject by path.
        /// </summary>
        private static void SetSerializedBool(SerializedObject so, string path, bool value)
        {
            var prop = so.FindProperty(path);
            if (prop != null)
            {
                prop.boolValue = value;
            }
        }
    }
}
#endif
