// ============================================================================
// MechWarriorSetup.cs — Editor tool to configure the MechWarrior as the Player
// THRESHOLD — Google Antigravity Mobile Game Challenge 2026
//
// Creates a proper AnimatorController with parameter-driven transitions
// and attaches all required Player components to the MechWarrior prefab.
// Run via: Tools > THRESHOLD > Setup MechWarrior Player
// ============================================================================

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using System.IO;
using System.Linq;

namespace Threshold.Editor
{
    public static class MechWarriorSetup
    {
        // ====================================================================
        // Menu Entry
        // ====================================================================

        [MenuItem("Tools/THRESHOLD/Setup MechWarrior Player")]
        public static void SetupMechWarrior()
        {
            // 1. Build the Animator Controller
            var controller = BuildAnimatorController();
            if (controller == null)
            {
                Debug.LogError("[MechWarriorSetup] Failed to build AnimatorController.");
                return;
            }

            // 2. Setup the prefab
            string prefabPath = "Assets/Prefabs/Players/MechWarrior.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[MechWarriorSetup] Prefab not found at {prefabPath}");
                return;
            }

            // Open prefab for editing
            var prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                SetupPrefabComponents(prefabRoot, controller);
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
                Debug.Log("[MechWarriorSetup] ✅ MechWarrior prefab fully configured!");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            AssetDatabase.Refresh();
        }

        // ====================================================================
        // Animator Controller Builder
        // ====================================================================

        private static AnimatorController BuildAnimatorController()
        {
            string outputPath = "Assets/Animations/Player_AnimatorController.controller";

            // Ensure directory exists
            string dir = Path.GetDirectoryName(outputPath);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                // Create Animations folder
                if (!AssetDatabase.IsValidFolder("Assets/Animations"))
                    AssetDatabase.CreateFolder("Assets", "Animations");
            }

            // Create or overwrite the controller
            var controller = AnimatorController.CreateAnimatorControllerAtPath(outputPath);

            // Add parameters
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("IsFiring", AnimatorControllerParameterType.Bool);
            controller.AddParameter("IsReloading", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Die", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("SpeedMultiplier", AnimatorControllerParameterType.Float);

            // Get the base layer state machine
            var rootStateMachine = controller.layers[0].stateMachine;

            // ============================================================
            // Find animation clips from the SciFiWarrior asset
            // ============================================================
            string animFolder = "Assets/SciFiWarriorPBRHPPolyart";
            var allClips = AssetDatabase.FindAssets("t:AnimationClip", new[] { animFolder });

            AnimationClip clipIdle = FindClip(allClips, "Idle_Guard_AR");
            AnimationClip clipRun = FindClip(allClips, "Run_guard_AR");
            AnimationClip clipWalkShoot = FindClip(allClips, "WalkFront_Shoot_AR");
            AnimationClip clipIdleShoot = FindClip(allClips, "Idle_Shoot_Ar");
            AnimationClip clipReload = FindClip(allClips, "Reload");
            AnimationClip clipDie = FindClip(allClips, "Die");
            AnimationClip clipAutoShot = FindClip(allClips, "Shoot_Autoshot_AR");

            // Use AutoShot if SingleShot not found (auto = sustained fire loop)
            AnimationClip clipFire = clipAutoShot;
            if (clipFire == null)
            {
                clipFire = FindClip(allClips, "Shoot_SingleShot_AR");
            }

            Debug.Log($"[MechWarriorSetup] Clips found: " +
                      $"Idle={clipIdle != null}, Run={clipRun != null}, " +
                      $"WalkShoot={clipWalkShoot != null}, IdleShoot={clipIdleShoot != null}, " +
                      $"Reload={clipReload != null}, Die={clipDie != null}, " +
                      $"Fire={clipFire != null}");

            // ============================================================
            // States
            // ============================================================

            // -- IDLE (default state) --
            var idleState = rootStateMachine.AddState("Idle", new Vector3(300, 0, 0));
            idleState.motion = clipIdle;
            rootStateMachine.defaultState = idleState;

            // -- RUN --
            var runState = rootStateMachine.AddState("Run", new Vector3(300, 80, 0));
            runState.motion = clipRun;
            // Scale playback speed by analog stick deflection
            runState.speedParameterActive = true;
            runState.speedParameter = "SpeedMultiplier";

            // -- IDLE_SHOOT (standing still + firing) --
            var idleShootState = rootStateMachine.AddState("Idle_Shoot", new Vector3(550, 0, 0));
            idleShootState.motion = clipIdleShoot ?? clipFire;

            // -- WALK_SHOOT (moving + firing) --
            var walkShootState = rootStateMachine.AddState("Walk_Shoot", new Vector3(550, 80, 0));
            walkShootState.motion = clipWalkShoot;
            // Scale playback speed by analog stick deflection
            walkShootState.speedParameterActive = true;
            walkShootState.speedParameter = "SpeedMultiplier";

            // -- RELOAD --
            var reloadState = rootStateMachine.AddState("Reload", new Vector3(550, 200, 0));
            reloadState.motion = clipReload;

            // -- DIE --
            var dieState = rootStateMachine.AddState("Die", new Vector3(300, 300, 0));
            dieState.motion = clipDie;

            // ============================================================
            // Transitions
            // ============================================================

            // --- Locomotion: Idle <-> Run (based on Speed) ---

            // Idle → Run (Speed > 0.1)
            var t1 = idleState.AddTransition(runState);
            t1.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
            t1.AddCondition(AnimatorConditionMode.IfNot, 0, "IsFiring");
            t1.hasExitTime = false;
            t1.duration = 0.15f;

            // Run → Idle (Speed < 0.1)
            var t2 = runState.AddTransition(idleState);
            t2.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
            t2.AddCondition(AnimatorConditionMode.IfNot, 0, "IsFiring");
            t2.hasExitTime = false;
            t2.duration = 0.15f;

            // --- Firing transitions ---

            // Idle → Idle_Shoot (start firing while stationary)
            var t3 = idleState.AddTransition(idleShootState);
            t3.AddCondition(AnimatorConditionMode.If, 0, "IsFiring");
            t3.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
            t3.hasExitTime = false;
            t3.duration = 0.1f;

            // Idle_Shoot → Idle (stop firing while stationary)
            var t4 = idleShootState.AddTransition(idleState);
            t4.AddCondition(AnimatorConditionMode.IfNot, 0, "IsFiring");
            t4.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
            t4.hasExitTime = false;
            t4.duration = 0.15f;

            // Run → Walk_Shoot (start firing while moving)
            var t5 = runState.AddTransition(walkShootState);
            t5.AddCondition(AnimatorConditionMode.If, 0, "IsFiring");
            t5.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
            t5.hasExitTime = false;
            t5.duration = 0.1f;

            // Walk_Shoot → Run (stop firing while moving)
            var t6 = walkShootState.AddTransition(runState);
            t6.AddCondition(AnimatorConditionMode.IfNot, 0, "IsFiring");
            t6.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
            t6.hasExitTime = false;
            t6.duration = 0.15f;

            // Walk_Shoot → Idle_Shoot (stopped moving while firing)
            var t7 = walkShootState.AddTransition(idleShootState);
            t7.AddCondition(AnimatorConditionMode.If, 0, "IsFiring");
            t7.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
            t7.hasExitTime = false;
            t7.duration = 0.15f;

            // Idle_Shoot → Walk_Shoot (started moving while firing)
            var t8 = idleShootState.AddTransition(walkShootState);
            t8.AddCondition(AnimatorConditionMode.If, 0, "IsFiring");
            t8.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
            t8.hasExitTime = false;
            t8.duration = 0.1f;

            // Idle_Shoot → Run (stopped firing, started moving)
            var t8b = idleShootState.AddTransition(runState);
            t8b.AddCondition(AnimatorConditionMode.IfNot, 0, "IsFiring");
            t8b.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
            t8b.hasExitTime = false;
            t8b.duration = 0.15f;

            // Walk_Shoot → Idle (stopped firing and stopped moving)
            var t8c = walkShootState.AddTransition(idleState);
            t8c.AddCondition(AnimatorConditionMode.IfNot, 0, "IsFiring");
            t8c.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
            t8c.hasExitTime = false;
            t8c.duration = 0.15f;

            // --- Reload transitions (from any combat state) ---

            // Idle → Reload
            var t9 = idleState.AddTransition(reloadState);
            t9.AddCondition(AnimatorConditionMode.If, 0, "IsReloading");
            t9.hasExitTime = false;
            t9.duration = 0.15f;

            // Run → Reload
            var t10 = runState.AddTransition(reloadState);
            t10.AddCondition(AnimatorConditionMode.If, 0, "IsReloading");
            t10.hasExitTime = false;
            t10.duration = 0.15f;

            // Idle_Shoot → Reload
            var t11 = idleShootState.AddTransition(reloadState);
            t11.AddCondition(AnimatorConditionMode.If, 0, "IsReloading");
            t11.hasExitTime = false;
            t11.duration = 0.1f;

            // Walk_Shoot → Reload
            var t12 = walkShootState.AddTransition(reloadState);
            t12.AddCondition(AnimatorConditionMode.If, 0, "IsReloading");
            t12.hasExitTime = false;
            t12.duration = 0.1f;

            // Reload → Idle (reload done)
            var t13 = reloadState.AddTransition(idleState);
            t13.AddCondition(AnimatorConditionMode.IfNot, 0, "IsReloading");
            t13.hasExitTime = false;
            t13.duration = 0.2f;

            // --- Death (from Any State via trigger) ---
            var tDie = rootStateMachine.AddAnyStateTransition(dieState);
            tDie.AddCondition(AnimatorConditionMode.If, 0, "Die");
            tDie.hasExitTime = false;
            tDie.duration = 0.15f;
            tDie.canTransitionToSelf = false;

            // Save
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            Debug.Log($"[MechWarriorSetup] ✅ AnimatorController created at {outputPath}");
            return controller;
        }

        // ====================================================================
        // Prefab Component Setup
        // ====================================================================

        private static void SetupPrefabComponents(GameObject root, AnimatorController controller)
        {
            // --- Tag ---
            root.tag = "Player";
            root.layer = LayerMask.NameToLayer("Default"); // Keep default for now

            // --- Animator ---
            var animator = root.GetComponent<Animator>();
            if (animator == null) animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false; // We use Rigidbody movement
            animator.updateMode = AnimatorUpdateMode.Normal;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            // --- Rigidbody (required by PlayerController) ---
            var rb = root.GetComponent<Rigidbody>();
            if (rb == null) rb = root.AddComponent<Rigidbody>();
            rb.useGravity = true;
            rb.mass = 1f;
            rb.linearDamping = 0f;
            rb.angularDamping = 0.05f;
            rb.constraints = RigidbodyConstraints.FreezeRotation;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            rb.isKinematic = false;

            // --- Capsule Collider ---
            var col = root.GetComponent<CapsuleCollider>();
            if (col == null) col = root.AddComponent<CapsuleCollider>();
            // Approximate mech warrior height from mesh bounds
            col.center = new Vector3(0f, 0.9f, 0f);
            col.radius = 0.3f;
            col.height = 1.8f;
            col.direction = 1; // Y-axis

            // --- Player Scripts ---
            // PlayerHealth (required by PlayerController)
            if (root.GetComponent<Player.PlayerHealth>() == null)
                root.AddComponent<Player.PlayerHealth>();

            // PlayerWeapon (required by PlayerController)
            var weapon = root.GetComponent<Player.PlayerWeapon>();
            if (weapon == null)
                weapon = root.AddComponent<Player.PlayerWeapon>();

            // PlayerController
            if (root.GetComponent<Player.PlayerController>() == null)
                root.AddComponent<Player.PlayerController>();

            // PlayerAnimator (our new bridge)
            var pa = root.GetComponent<Player.PlayerAnimator>();
            if (pa == null)
                pa = root.AddComponent<Player.PlayerAnimator>();

            // --- Setup muzzle point ---
            // Try to find ArmPlacement_Right as a good muzzle reference
            var muzzle = FindDeepChild(root.transform, "ArmPlacement_Right");
            if (muzzle == null)
            {
                // Create a muzzle point child
                var muzzleObj = new GameObject("MuzzlePoint");
                muzzleObj.transform.SetParent(root.transform);
                muzzleObj.transform.localPosition = new Vector3(0f, 1.0f, 0.6f);
                muzzleObj.transform.localRotation = Quaternion.identity;
                muzzle = muzzleObj.transform;
                Debug.Log("[MechWarriorSetup] Created MuzzlePoint child.");
            }
            else
            {
                Debug.Log($"[MechWarriorSetup] Using existing bone '{muzzle.name}' as muzzle reference.");
            }

            // Set muzzle on PlayerWeapon via SerializedObject
            var so = new SerializedObject(weapon);
            var muzzleProp = so.FindProperty("muzzlePoint");
            if (muzzleProp != null)
            {
                muzzleProp.objectReferenceValue = muzzle;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // Set animator reference on PlayerAnimator
            var soAnim = new SerializedObject(pa);
            var animProp = soAnim.FindProperty("animator");
            if (animProp != null)
            {
                animProp.objectReferenceValue = animator;
                soAnim.ApplyModifiedPropertiesWithoutUndo();
            }

            Debug.Log("[MechWarriorSetup] ✅ Components configured: " +
                      "Animator, Rigidbody, CapsuleCollider, PlayerController, " +
                      "PlayerHealth, PlayerWeapon, PlayerAnimator");
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static AnimationClip FindClip(string[] guids, string clipName)
        {
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // Animation clips are often inside FBX files as sub-assets
                var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (var asset in assets)
                {
                    if (asset is AnimationClip clip && clip.name == clipName)
                        return clip;
                }
            }

            // Also search in the raw animation files
            var allAnimPaths = AssetDatabase.FindAssets("t:AnimationClip");
            foreach (var guid in allAnimPaths)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.Contains("SciFiWarrior")) continue;
                var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (var asset in assets)
                {
                    if (asset is AnimationClip clip && clip.name == clipName)
                        return clip;
                }
            }

            Debug.LogWarning($"[MechWarriorSetup] Animation clip '{clipName}' not found.");
            return null;
        }

        private static Transform FindDeepChild(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            foreach (Transform child in parent)
            {
                var result = FindDeepChild(child, name);
                if (result != null) return result;
            }
            return null;
        }
    }
}
#endif
