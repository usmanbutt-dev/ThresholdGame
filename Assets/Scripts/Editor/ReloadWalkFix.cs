#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using System.IO;

namespace Threshold.Editor
{
    /// <summary>
    /// Standalone Editor utility that applies the reload walking fix in-place
    /// without overwriting or altering any other configurations, prefabs, or states.
    /// Run via: Tools > THRESHOLD > Fix Reload Walking
    /// </summary>
    public static class ReloadWalkFix
    {
        [MenuItem("Tools/THRESHOLD/Fix Reload Walking")]
        public static void ApplyReloadWalkingFix()
        {
            string controllerPath = "Assets/Animations/Player_AnimatorController.controller";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);

            if (controller == null)
            {
                Debug.LogError($"[ReloadWalkFix] AnimatorController not found at {controllerPath}. " +
                               "Please make sure the controller exists first.");
                return;
            }

            Undo.RegisterCompleteObjectUndo(controller, "Fix Reload Walking");

            // 1. Create or load the Upper Body Avatar Mask
            string maskPath = "Assets/Animations/Player_UpperBodyMask.mask";
            var upperBodyMask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
            if (upperBodyMask == null)
            {
                upperBodyMask = new AvatarMask();
                
                // Humanoid configuration: chest, head, arms, and IK are active. Root and legs are inactive.
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, false);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head, true);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg, false);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg, false);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm, true);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm, true);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftHandIK, true);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightHandIK, true);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFootIK, false);
                upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFootIK, false);

                AssetDatabase.CreateAsset(upperBodyMask, maskPath);
                Debug.Log($"[ReloadWalkFix] Created Upper Body Avatar Mask at {maskPath}");
            }

            // 2. Find and salvage the Reload Clip from the Base Layer, then delete the base reload state
            AnimationClip reloadClip = null;
            var baseStateMachine = controller.layers[0].stateMachine;
            ChildAnimatorState baseReloadState = default;
            bool foundBaseReload = false;

            foreach (var state in baseStateMachine.states)
            {
                if (state.state.name == "Reload")
                {
                    baseReloadState = state;
                    reloadClip = state.state.motion as AnimationClip;
                    foundBaseReload = true;
                    break;
                }
            }

            if (foundBaseReload)
            {
                baseStateMachine.RemoveState(baseReloadState.state);
                Debug.Log("[ReloadWalkFix] Salvaged reload clip and removed 'Reload' state from Base Layer.");
            }
            else
            {
                Debug.LogWarning("[ReloadWalkFix] 'Reload' state was not found on the Base Layer. Searching asset database for a Reload clip...");
                // Fallback search in the SciFiWarrior folder
                var allClips = AssetDatabase.FindAssets("Reload t:AnimationClip");
                if (allClips.Length > 0)
                {
                    string path = AssetDatabase.GUIDToAssetPath(allClips[0]);
                    var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                    foreach (var asset in assets)
                    {
                        if (asset is AnimationClip clip && clip.name == "Reload")
                        {
                            reloadClip = clip;
                            break;
                        }
                    }
                }
            }

            if (reloadClip == null)
            {
                Debug.LogError("[ReloadWalkFix] Failed to find the 'Reload' AnimationClip. Cannot complete the reload fix.");
                return;
            }

            // 3. Setup/Find the Upper Body Layer
            int upperLayerIndex = -1;
            for (int i = 0; i < controller.layers.Length; i++)
            {
                if (controller.layers[i].name == "Upper Body Layer")
                {
                    upperLayerIndex = i;
                    break;
                }
            }

            if (upperLayerIndex == -1)
            {
                controller.AddLayer("Upper Body Layer");
                upperLayerIndex = controller.layers.Length - 1;
                Debug.Log("[ReloadWalkFix] Added 'Upper Body Layer' to the AnimatorController.");
            }

            // Apply weight and mask
            var layers = controller.layers;
            var upperLayer = layers[upperLayerIndex];
            upperLayer.defaultWeight = 1f;
            upperLayer.avatarMask = upperBodyMask;
            controller.layers = layers;

            var upperStateMachine = upperLayer.stateMachine;

            // 4. Configure States on the Upper Body Layer
            AnimatorState upperIdleState = null;
            AnimatorState upperReloadState = null;

            foreach (var state in upperStateMachine.states)
            {
                if (state.state.name == "Upper_Idle")
                {
                    upperIdleState = state.state;
                }
                else if (state.state.name == "Reload")
                {
                    upperReloadState = state.state;
                }
            }

            if (upperIdleState == null)
            {
                upperIdleState = upperStateMachine.AddState("Upper_Idle", new Vector3(300, 0, 0));
                upperIdleState.motion = null; // Let base layer locomotion pass through
            }
            upperStateMachine.defaultState = upperIdleState;

            if (upperReloadState == null)
            {
                upperReloadState = upperStateMachine.AddState("Reload", new Vector3(300, 120, 0));
            }
            upperReloadState.motion = reloadClip;

            // Clean up existing transitions on these upper states to avoid duplicates
            upperIdleState.transitions = new AnimatorStateTransition[0];
            upperReloadState.transitions = new AnimatorStateTransition[0];

            // Add new transitions
            var tStart = upperIdleState.AddTransition(upperReloadState);
            tStart.AddCondition(AnimatorConditionMode.If, 0f, "IsReloading");
            tStart.hasExitTime = false;
            tStart.duration = 0.15f;

            var tEnd = upperReloadState.AddTransition(upperIdleState);
            tEnd.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsReloading");
            tEnd.hasExitTime = false;
            tEnd.duration = 0.2f;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            Debug.Log("[ReloadWalkFix] ✅ In-place reload walking fix successfully applied! " +
                      "You can now walk and reload normally.");
        }
    }
}
#endif
