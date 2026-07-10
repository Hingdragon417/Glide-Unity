using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

public class NetworkPlayerSync : MonoBehaviour
{
    private const string RemoteVisualResourceName = "RemotePlayerVisual";
    private const string WalkControllerResourceName = "PlayerWalk";
    private static readonly int IsWalkingHash = Animator.StringToHash("IsWalking");

    [SerializeField] private DynamicTcpClient tcpClient;
    [SerializeField] private float sendRate = 15f;
    [SerializeField] private Color fallbackRemotePlayerColor = Color.white;

    private readonly Dictionary<int, Transform> remotePlayers = new();
    private readonly Dictionary<int, Vector3> targetPositions = new();
    private readonly Dictionary<int, Quaternion> targetRotations = new();

    private int localPlayerId = -1;
    private float nextSendTime;
    private Material remotePlayerMaterial;
    private bool loggedBinding;
    private bool loggedFirstSend;
    private bool replayedCachedStates;
    private float nextSendWarnTime;

    private void Awake()
    {
        ResolveTcpClient();
    }

    private void OnEnable()
    {
        ResolveTcpClient();

        if (tcpClient != null)
        {
            localPlayerId = tcpClient.LocalPlayerId;
            tcpClient.MessageReceived += HandleServerMessage;
            LogBindingOnce();
            ReplayCachedStates();
        }
    }

    private void OnDisable()
    {
        if (tcpClient != null)
        {
            tcpClient.MessageReceived -= HandleServerMessage;
        }
    }

    private void OnDestroy()
    {
        if (remotePlayerMaterial != null)
        {
            Destroy(remotePlayerMaterial);
            remotePlayerMaterial = null;
        }
    }

    private void Update()
    {
        if (localPlayerId < 0 && tcpClient != null && tcpClient.LocalPlayerId >= 0)
        {
            localPlayerId = tcpClient.LocalPlayerId;
        }

        RemoveLocalRemotePlayer();

        if (ResolveTcpClient() && tcpClient != null)
        {
            tcpClient.MessageReceived += HandleServerMessage;
            LogBindingOnce();
        }

        SendLocalState();
        ReplayCachedStates();
        SmoothRemotePlayers();
    }

    private void LogBindingOnce()
    {
        if (loggedBinding || tcpClient == null)
        {
            return;
        }

        loggedBinding = true;
        Debug.Log($"[NetSync] Bound to TCP client (localPlayerId={localPlayerId}, connected={tcpClient.IsConnected}).");
    }

    private bool ResolveTcpClient()
    {
        // The persistent DontDestroyOnLoad client created in the menu is the single
        // connection that actually joined the lobby listing, so always prefer it. A
        // scene-local DynamicTcpClient (such as the throwaway one in MainGame that
        // destroys itself as a duplicate) never joins a listing, so binding to it would
        // make the server silently drop our state and report the socket as disconnected.
        DynamicTcpClient resolvedClient = DynamicTcpClient.ActiveClient;

        if (resolvedClient == null)
        {
            resolvedClient = tcpClient != null ? tcpClient : FindAnyObjectByType<DynamicTcpClient>();
        }

        if (resolvedClient == null || resolvedClient == tcpClient)
        {
            return false;
        }

        if (tcpClient != null)
        {
            tcpClient.MessageReceived -= HandleServerMessage;
        }

        tcpClient = resolvedClient;
        localPlayerId = tcpClient.LocalPlayerId;
        replayedCachedStates = false;
        return true;
    }

    private void SendLocalState()
    {
        if (Time.time < nextSendTime)
        {
            return;
        }

        if (tcpClient == null || !tcpClient.IsConnected)
        {
            if (Time.time >= nextSendWarnTime)
            {
                nextSendWarnTime = Time.time + 2f;
                Debug.LogWarning($"[NetSync] Not sending state: TCP client {(tcpClient == null ? "is null" : "is disconnected")}.");
            }

            return;
        }

        nextSendTime = Time.time + 1f / Mathf.Max(1f, sendRate);

        if (!loggedFirstSend)
        {
            loggedFirstSend = true;
            Debug.Log($"[NetSync] Sending local state to server as player {localPlayerId}.");
        }

        Vector3 position = transform.position;
        Quaternion rotation = transform.rotation;

        string message = string.Join("|",
            "state",
            Format(position.x),
            Format(position.y),
            Format(position.z),
            Format(rotation.x),
            Format(rotation.y),
            Format(rotation.z),
            Format(rotation.w));

        _ = SendAsync(message);
    }

    private async Task SendAsync(string message)
    {
        await tcpClient.SendAsync(message);
    }

    private void HandleServerMessage(string message)
    {
        string[] parts = message.Split('|');

        if (parts.Length == 2 && parts[0] == "welcome" && int.TryParse(parts[1], out int welcomedId))
        {
            localPlayerId = welcomedId;
            RemoveRemotePlayer(localPlayerId);
            Debug.Log($"Joined multiplayer server as player {localPlayerId}");
            return;
        }

        if (parts.Length == 2 && parts[0] == "leave" && int.TryParse(parts[1], out int leavingId))
        {
            RemoveRemotePlayer(leavingId);
            return;
        }

        if (parts.Length == 9 && parts[0] == "state" && int.TryParse(parts[1], out int playerId))
        {
            if (IsLocalPlayerId(playerId))
            {
                RemoveRemotePlayer(playerId);
                return;
            }

            if (!TryParseState(parts, out Vector3 position, out Quaternion rotation))
            {
                return;
            }

            bool isNewPlayer = !remotePlayers.ContainsKey(playerId);

            EnsureRemotePlayer(playerId);
            targetPositions[playerId] = position;
            targetRotations[playerId] = rotation;

            // Snap a freshly spawned remote player onto its first reported pose so it
            // does not visibly streak in from the world origin while smoothing catches up.
            if (isNewPlayer && remotePlayers.TryGetValue(playerId, out Transform spawnedPlayer))
            {
                spawnedPlayer.SetPositionAndRotation(position, rotation);
            }
        }
    }

    private bool TryParseState(string[] parts, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!TryParseFloat(parts[2], out float x) ||
            !TryParseFloat(parts[3], out float y) ||
            !TryParseFloat(parts[4], out float z) ||
            !TryParseFloat(parts[5], out float qx) ||
            !TryParseFloat(parts[6], out float qy) ||
            !TryParseFloat(parts[7], out float qz) ||
            !TryParseFloat(parts[8], out float qw))
        {
            return false;
        }

        position = new Vector3(x, y, z);
        rotation = new Quaternion(qx, qy, qz, qw);
        return true;
    }

    private bool IsLocalPlayerId(int playerId)
    {
        return playerId >= 0 &&
            (playerId == localPlayerId ||
             (tcpClient != null && playerId == tcpClient.LocalPlayerId) ||
             (DynamicTcpClient.ActiveClient != null && playerId == DynamicTcpClient.ActiveClient.LocalPlayerId));
    }

    private void ReplayCachedStates()
    {
        if (replayedCachedStates || tcpClient == null || localPlayerId < 0)
        {
            return;
        }

        replayedCachedStates = true;

        string[] cachedStateMessages = tcpClient.GetCachedStateMessages();
        foreach (string cachedStateMessage in cachedStateMessages)
        {
            HandleServerMessage(cachedStateMessage);
        }

        RemoveRemotePlayer(localPlayerId);

        if (cachedStateMessages.Length > 0)
        {
            Debug.Log($"[NetSync] Replayed {cachedStateMessages.Length} cached player state messages.");
        }
    }

    private void EnsureRemotePlayer(int playerId)
    {
        if (IsLocalPlayerId(playerId))
        {
            RemoveRemotePlayer(playerId);
            Debug.Log($"[NetSync] Ignored local player state {playerId}.");
            return;
        }

        if (remotePlayers.ContainsKey(playerId))
        {
            return;
        }

        GameObject remotePlayer = CreateRemotePlayerObject();
        remotePlayer.name = $"Remote Player {playerId}";

        remotePlayers[playerId] = remotePlayer.transform;
        targetPositions[playerId] = remotePlayer.transform.position;
        targetRotations[playerId] = remotePlayer.transform.rotation;

        Debug.Log($"[NetSync] Spawned remote player {playerId}.");
    }

    private GameObject CreateRemotePlayerObject()
    {
        GameObject remotePlayer = CreateRemotePlayerFromPrefab();

        if (remotePlayer.GetComponentsInChildren<Renderer>(true).Length == 0)
        {
            Destroy(remotePlayer);
            remotePlayer = CreateFallbackRemotePlayer();
        }

        StripLocalOnlyComponents(remotePlayer);
        ResetLayers(remotePlayer, LayerMask.NameToLayer("Default"));
        ApplyRemotePlayerMaterial(remotePlayer);
        EnableRemoteRenderers(remotePlayer);
        AlignRemoteVisualToLocalCollider(remotePlayer);
        EnsureRemoteAnimator(remotePlayer);
        return remotePlayer;
    }

    private GameObject CreateRemotePlayerFromPrefab()
    {
        GameObject remoteVisualPrefab = Resources.Load<GameObject>(RemoteVisualResourceName);
        if (remoteVisualPrefab != null)
        {
            GameObject remotePlayer = Instantiate(remoteVisualPrefab);
            remotePlayer.name = "Remote Player Visual";
            CopyTransform(transform, remotePlayer.transform);
            return remotePlayer;
        }

        GameObject fallbackRoot = new("Remote Player Visual");
        CopyTransform(transform, fallbackRoot.transform);
        CopyLocalPlayerVisuals(fallbackRoot.transform);
        return fallbackRoot;
    }

    private bool CopyLocalPlayerVisuals(Transform remoteRoot)
    {
        Transform visualRoot = GetPreferredVisualRoot();

        if (visualRoot != null)
        {
            GameObject visualClone = Instantiate(visualRoot.gameObject, remoteRoot);
            visualClone.name = visualRoot.name;
            CopyLocalTransform(visualRoot, visualClone.transform);
            return true;
        }

        bool copiedAny = false;
        foreach (Transform child in transform)
        {
            if (!IsVisualChild(child))
            {
                continue;
            }

            GameObject visualClone = Instantiate(child.gameObject, remoteRoot);
            visualClone.name = child.name;
            CopyLocalTransform(child, visualClone.transform);
            copiedAny = true;
        }

        return copiedAny;
    }

    private Transform GetPreferredVisualRoot()
    {
        PlayerMovement movement = GetComponent<PlayerMovement>();
        if (movement != null && HasRenderer(movement.playerModel))
        {
            return movement.playerModel;
        }

        Transform modelRoot = transform.Find("PlayerModel");
        return HasRenderer(modelRoot) ? modelRoot : null;
    }

    private bool IsVisualChild(Transform child)
    {
        if (child == null || child.GetComponentInChildren<Renderer>(true) == null)
        {
            return false;
        }

        if (child.GetComponentInChildren<Camera>(true) != null ||
            child.GetComponentInChildren<Canvas>(true) != null ||
            child.GetComponentInChildren<NetworkPlayerSync>(true) != null ||
            child.GetComponentInChildren<DynamicTcpClient>(true) != null)
        {
            return false;
        }

        return true;
    }

    private static bool HasRenderer(Transform root)
    {
        return root != null && root.GetComponentInChildren<Renderer>(true) != null;
    }

    private static void CopyTransform(Transform source, Transform target)
    {
        target.SetPositionAndRotation(source.position, source.rotation);
        target.localScale = source.lossyScale;
    }

    private static void CopyLocalTransform(Transform source, Transform target)
    {
        target.localPosition = source.localPosition;
        target.localRotation = source.localRotation;
        target.localScale = source.localScale;
    }

    private GameObject CreateFallbackRemotePlayer()
    {
        GameObject remotePlayer = GameObject.CreatePrimitive(PrimitiveType.Capsule);

        Renderer renderer = remotePlayer.GetComponent<Renderer>();

        if (renderer != null)
        {
            // CreatePrimitive assigns the built-in render pipeline's default material, which
            // a Scriptable Render Pipeline (this project uses HDRP) cannot draw, so the body
            // would be invisible. Swap in a material whose shader matches the active pipeline.
            renderer.sharedMaterial = GetRemotePlayerMaterial();
        }

        return remotePlayer;
    }

    private static void StripLocalOnlyComponents(GameObject remotePlayer)
    {
        foreach (NetworkPlayerSync sync in remotePlayer.GetComponentsInChildren<NetworkPlayerSync>(true))
        {
            sync.enabled = false;
            Destroy(sync);
        }

        foreach (PlayerMovement movement in remotePlayer.GetComponentsInChildren<PlayerMovement>(true))
        {
            movement.enabled = false;
            Destroy(movement);
        }

        foreach (DynamicTcpClient client in remotePlayer.GetComponentsInChildren<DynamicTcpClient>(true))
        {
            client.enabled = false;
            Destroy(client);
        }

        foreach (Rigidbody body in remotePlayer.GetComponentsInChildren<Rigidbody>(true))
        {
            body.isKinematic = true;
            Destroy(body);
        }

        foreach (Camera camera in remotePlayer.GetComponentsInChildren<Camera>(true))
        {
            camera.enabled = false;
            Destroy(camera);
        }

        foreach (AudioListener listener in remotePlayer.GetComponentsInChildren<AudioListener>(true))
        {
            listener.enabled = false;
            Destroy(listener);
        }

        foreach (Collider collider in remotePlayer.GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = false;
        }
    }

    private void EnableRemoteRenderers(GameObject remotePlayer)
    {
        int enabledRendererCount = 0;

        foreach (Renderer renderer in remotePlayer.GetComponentsInChildren<Renderer>(true))
        {
            renderer.enabled = true;
            enabledRendererCount++;
        }

        if (enabledRendererCount == 0)
        {
            Debug.LogWarning($"[NetSync] Remote player '{remotePlayer.name}' has no renderers.");
        }
        else
        {
            Debug.Log($"[NetSync] Remote player '{remotePlayer.name}' renderers enabled: {enabledRendererCount}.");
        }
    }

    private void ApplyRemotePlayerMaterial(GameObject remotePlayer)
    {
        Material material = GetRemotePlayerMaterial();

        foreach (Renderer renderer in remotePlayer.GetComponentsInChildren<Renderer>(true))
        {
            Material[] sharedMaterials = renderer.sharedMaterials;

            if (sharedMaterials == null || sharedMaterials.Length == 0)
            {
                renderer.sharedMaterial = material;
                continue;
            }

            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                sharedMaterials[i] = material;
            }

            renderer.sharedMaterials = sharedMaterials;
        }
    }

    private void AlignRemoteVisualToLocalCollider(GameObject remotePlayer)
    {
        Renderer[] renderers = remotePlayer.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return;
        }

        bool hasBounds = false;
        Bounds visualBounds = default;

        foreach (Renderer renderer in renderers)
        {
            if (!hasBounds)
            {
                visualBounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                visualBounds.Encapsulate(renderer.bounds);
            }
        }

        if (!hasBounds)
        {
            return;
        }

        float targetBottomY = remotePlayer.transform.position.y + GetLocalSolidColliderBottomOffset() + 0.02f;
        float liftAmount = targetBottomY - visualBounds.min.y;

        if (Mathf.Abs(liftAmount) > 0.001f)
        {
            foreach (Transform child in remotePlayer.transform)
            {
                child.position += Vector3.up * liftAmount;
            }
        }
    }

    private float GetLocalSolidColliderBottomOffset()
    {
        Collider[] colliders = GetComponents<Collider>();
        float bottomOffset = 0f;
        bool foundSolidCollider = false;

        foreach (Collider collider in colliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger)
            {
                continue;
            }

            float offset = collider.bounds.min.y - transform.position.y;

            if (!foundSolidCollider)
            {
                bottomOffset = offset;
                foundSolidCollider = true;
            }
            else
            {
                bottomOffset = Mathf.Min(bottomOffset, offset);
            }
        }

        return bottomOffset;
    }

    private static void EnsureRemoteAnimator(GameObject remotePlayer)
    {
        if (remotePlayer == null || remotePlayer.GetComponentsInChildren<Renderer>(true).Length == 0)
        {
            return;
        }

        Animator animator = remotePlayer.GetComponent<Animator>();
        if (animator == null)
        {
            animator = remotePlayer.AddComponent<Animator>();
        }

        if (animator.runtimeAnimatorController == null)
        {
            animator.runtimeAnimatorController = Resources.Load<RuntimeAnimatorController>(WalkControllerResourceName);
        }

        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
    }

    private static void SetRemoteWalking(Transform remotePlayer, bool isWalking)
    {
        if (remotePlayer == null)
        {
            return;
        }

        Animator animator = remotePlayer.GetComponent<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            return;
        }

        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            if (parameter.nameHash == IsWalkingHash && parameter.type == AnimatorControllerParameterType.Bool)
            {
                animator.SetBool(IsWalkingHash, isWalking);
                return;
            }
        }
    }

    private static void ResetLayers(GameObject root, int layer)
    {
        if (layer < 0)
        {
            return;
        }

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            child.gameObject.layer = layer;
        }
    }

    private Material GetRemotePlayerMaterial()
    {
        if (remotePlayerMaterial != null)
        {
            return remotePlayerMaterial;
        }

        Shader shader = Shader.Find("HDRP/Lit")
            ?? Shader.Find("Universal Render Pipeline/Lit")
            ?? Shader.Find("Standard")
            ?? Shader.Find("Sprites/Default");

        remotePlayerMaterial = new Material(shader);

        // Different pipelines expose the tint under different property names.
        if (remotePlayerMaterial.HasProperty("_BaseColor"))
        {
            remotePlayerMaterial.SetColor("_BaseColor", fallbackRemotePlayerColor);
        }

        if (remotePlayerMaterial.HasProperty("_Color"))
        {
            remotePlayerMaterial.SetColor("_Color", fallbackRemotePlayerColor);
        }

        return remotePlayerMaterial;
    }

    private void RemoveRemotePlayer(int playerId)
    {
        if (!remotePlayers.TryGetValue(playerId, out Transform remotePlayer))
        {
            return;
        }

        Destroy(remotePlayer.gameObject);
        remotePlayers.Remove(playerId);
        targetPositions.Remove(playerId);
        targetRotations.Remove(playerId);
    }

    private void RemoveLocalRemotePlayer()
    {
        if (localPlayerId >= 0)
        {
            RemoveRemotePlayer(localPlayerId);
        }

        if (tcpClient != null && tcpClient.LocalPlayerId >= 0)
        {
            RemoveRemotePlayer(tcpClient.LocalPlayerId);
        }

        if (DynamicTcpClient.ActiveClient != null && DynamicTcpClient.ActiveClient.LocalPlayerId >= 0)
        {
            RemoveRemotePlayer(DynamicTcpClient.ActiveClient.LocalPlayerId);
        }
    }

    private void SmoothRemotePlayers()
    {
        foreach (KeyValuePair<int, Transform> entry in remotePlayers)
        {
            int playerId = entry.Key;
            Transform remotePlayer = entry.Value;

            if (targetPositions.TryGetValue(playerId, out Vector3 targetPosition))
            {
                SetRemoteWalking(remotePlayer, (targetPosition - remotePlayer.position).sqrMagnitude > 0.0004f);
                remotePlayer.position = Vector3.Lerp(remotePlayer.position, targetPosition, Time.deltaTime * 12f);
            }
            else
            {
                SetRemoteWalking(remotePlayer, false);
            }

            if (targetRotations.TryGetValue(playerId, out Quaternion targetRotation))
            {
                remotePlayer.rotation = Quaternion.Slerp(remotePlayer.rotation, targetRotation, Time.deltaTime * 12f);
            }
        }
    }

    private static string Format(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool TryParseFloat(string value, out float parsedValue)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue);
    }
}
