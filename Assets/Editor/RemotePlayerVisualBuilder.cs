using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class RemotePlayerVisualBuilder
{
    private const string ScenePath = "Assets/Scenes/MainGame.unity";
    private const string SourcePlayerName = "NEW_PLAYER_MODEL";
    private const string PrefabPath = "Assets/Resources/RemotePlayerVisual.prefab";
    private const string WalkClipPath = "Assets/Walk.anim";
    private const string WalkControllerPath = "Assets/Resources/PlayerWalk.controller";
    private const string WalkParameterName = "IsWalking";

    [MenuItem("Build/Glide/Rebuild Walk Animator Controller")]
    public static void RebuildWalkAnimatorController()
    {
        AnimationClip walkClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(WalkClipPath);
        if (walkClip == null)
        {
            throw new FileNotFoundException($"Could not find walk animation at {WalkClipPath}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(WalkControllerPath));

        if (File.Exists(WalkControllerPath))
        {
            AssetDatabase.DeleteAsset(WalkControllerPath);
        }

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(WalkControllerPath);
        controller.AddParameter(WalkParameterName, AnimatorControllerParameterType.Bool);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        AnimatorState idleState = stateMachine.AddState("Idle");
        AnimatorState walkState = stateMachine.AddState("Walk");
        walkState.motion = walkClip;
        stateMachine.defaultState = idleState;

        AnimatorStateTransition toWalk = idleState.AddTransition(walkState);
        toWalk.hasExitTime = false;
        toWalk.duration = 0.08f;
        toWalk.AddCondition(AnimatorConditionMode.If, 0f, WalkParameterName);

        AnimatorStateTransition toIdle = walkState.AddTransition(idleState);
        toIdle.hasExitTime = false;
        toIdle.duration = 0.08f;
        toIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, WalkParameterName);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"Rebuilt walk animator controller: {WalkControllerPath}");
    }

    [MenuItem("Build/Glide/Rebuild Remote Player Visual")]
    public static void RebuildRemotePlayerVisual()
    {
        EditorSceneManager.OpenScene(ScenePath);

        GameObject sourcePlayer = GameObject.Find(SourcePlayerName);
        if (sourcePlayer == null)
        {
            throw new FileNotFoundException($"Could not find '{SourcePlayerName}' in {ScenePath}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));

        GameObject prefabRoot = new("RemotePlayerVisual");
        prefabRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        prefabRoot.transform.localScale = Vector3.one;
        AssignWalkAnimator(prefabRoot);

        try
        {
            foreach (Transform child in sourcePlayer.transform)
            {
                if (!IsRemoteVisualChild(child))
                {
                    continue;
                }

                GameObject clone = Object.Instantiate(child.gameObject, prefabRoot.transform);
                clone.name = child.name;
                clone.transform.localPosition = child.localPosition;
                clone.transform.localRotation = child.localRotation;
                clone.transform.localScale = child.localScale;
                StripRemoteOnlyCloneComponents(clone);
            }

            if (prefabRoot.GetComponentsInChildren<Renderer>(true).Length == 0)
            {
                throw new MissingComponentException($"'{SourcePlayerName}' did not provide any renderer children for {PrefabPath}.");
            }

            PrefabUtility.SaveAsPrefabAsset(prefabRoot, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Rebuilt remote player visual prefab: {PrefabPath}");
        }
        finally
        {
            Object.DestroyImmediate(prefabRoot);
        }
    }

    [MenuItem("Build/Glide/Port MainGame To New Player Model")]
    public static void PortMainGameToNewPlayerModel()
    {
        EditorSceneManager.OpenScene(ScenePath);

        GameObject newPlayer = GameObject.Find(SourcePlayerName);
        if (newPlayer == null)
        {
            throw new FileNotFoundException($"Could not find '{SourcePlayerName}' in {ScenePath}.");
        }

        GameObject oldPlayer = FindSceneObjectByName("Player_");
        NetworkPlayerSync oldNetworkSync = oldPlayer != null ? oldPlayer.GetComponent<NetworkPlayerSync>() : null;

        if (newPlayer.GetComponent<PlayerMovement>() == null)
        {
            newPlayer.AddComponent<PlayerMovement>();
        }

        NetworkPlayerSync newNetworkSync = newPlayer.GetComponent<NetworkPlayerSync>();
        if (newNetworkSync == null)
        {
            newNetworkSync = newPlayer.AddComponent<NetworkPlayerSync>();
        }

        AssignWalkAnimator(newPlayer);
        AssignPlayerModelReference(newPlayer);

        if (oldNetworkSync != null)
        {
            EditorUtility.CopySerialized(oldNetworkSync, newNetworkSync);
            Object.DestroyImmediate(oldNetworkSync);
        }

        newPlayer.SetActive(true);

        if (oldPlayer != null)
        {
            Object.DestroyImmediate(oldPlayer);
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log($"Ported MainGame player networking to '{SourcePlayerName}'.");
    }

    [MenuItem("Build/Glide/Assign Walk Animator In MainGame")]
    public static void AssignWalkAnimatorInMainGame()
    {
        EditorSceneManager.OpenScene(ScenePath);

        GameObject newPlayer = GameObject.Find(SourcePlayerName);
        if (newPlayer == null)
        {
            throw new FileNotFoundException($"Could not find '{SourcePlayerName}' in {ScenePath}.");
        }

        AssignWalkAnimator(newPlayer);
        AssignPlayerModelReference(newPlayer);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log($"Assigned walk animator controller to '{SourcePlayerName}'.");
    }

    private static bool IsRemoteVisualChild(Transform child)
    {
        if (child == null || child.GetComponentInChildren<Renderer>(true) == null)
        {
            return false;
        }

        return child.GetComponentInChildren<Camera>(true) == null &&
            child.GetComponentInChildren<Canvas>(true) == null &&
            child.GetComponentInChildren<PlayerMovement>(true) == null &&
            child.GetComponentInChildren<NetworkPlayerSync>(true) == null &&
            child.GetComponentInChildren<DynamicTcpClient>(true) == null;
    }

    private static GameObject FindSceneObjectByName(string objectName)
    {
        foreach (GameObject sceneObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (sceneObject.name != objectName || EditorUtility.IsPersistent(sceneObject))
            {
                continue;
            }

            if (sceneObject.scene.IsValid() && sceneObject.scene.path == ScenePath)
            {
                return sceneObject;
            }
        }

        return null;
    }

    private static void AssignWalkAnimator(GameObject target)
    {
        RuntimeAnimatorController controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(WalkControllerPath);
        if (controller == null)
        {
            RebuildWalkAnimatorController();
            controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(WalkControllerPath);
        }

        if (controller == null)
        {
            throw new FileNotFoundException($"Could not load walk animator controller at {WalkControllerPath}.");
        }

        Animator animator = target.GetComponent<Animator>();
        if (animator == null)
        {
            animator = target.AddComponent<Animator>();
        }

        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
    }

    private static void AssignPlayerModelReference(GameObject target)
    {
        PlayerMovement movement = target.GetComponent<PlayerMovement>();
        if (movement == null)
        {
            return;
        }

        Transform modelRoot = target.transform.Find("Object_2");
        if (modelRoot == null)
        {
            return;
        }

        SerializedObject serializedMovement = new(movement);
        SerializedProperty playerModelProperty = serializedMovement.FindProperty("playerModel");
        playerModelProperty.objectReferenceValue = modelRoot;
        serializedMovement.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void StripRemoteOnlyCloneComponents(GameObject clone)
    {
        foreach (Camera camera in clone.GetComponentsInChildren<Camera>(true))
        {
            Object.DestroyImmediate(camera);
        }

        foreach (AudioListener listener in clone.GetComponentsInChildren<AudioListener>(true))
        {
            Object.DestroyImmediate(listener);
        }

        foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
        {
            Object.DestroyImmediate(collider);
        }

        foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
        {
            Object.DestroyImmediate(body);
        }

        foreach (MonoBehaviour behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
        {
            Object.DestroyImmediate(behaviour);
        }
    }
}
