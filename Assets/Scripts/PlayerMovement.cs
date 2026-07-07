using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    private const string LocalPlayerBodyLayerName = "LocalPlayerBody";
    private const string WalkControllerResourceName = "PlayerWalk";
    private static readonly int IsWalkingHash = Animator.StringToHash("IsWalking");

    [Header("Movement")]
    public float mouseSensitivity = 0.5f;
    public Transform playerCamera;
    public bool hideLocalBodyFromCamera = false;

    [Header("Glide Animation")]
    public Transform playerModel;
    public float modelGroundClearance = 0.02f;
    [Tooltip("Y is treated as world-space eye height so scaled player model roots do not pull the camera down.")]
    public Vector3 defaultCameraLocalPosition = new(0f, 1.65f, 0f);
    public float glideBodyPitch = 90f;
    public float glideBankAngle = 25f;
    public float glideAnimSmooth = 8f;

    [Header("Runtime (read-only-ish)")]
    public Vector3 lastCheckpoint;
    public float cameraRotation;
    public float currentGlideSpeed;
    public float currentSpeedMultiplier = 1f;

    private Rigidbody rb;
    private bool isGrounded;
    private bool isGliding;
    private Coroutine speedBoostCoroutine;
    private int originalPlayerCameraCullingMask = -1;
    private bool alignedPlayerModelToCollider;
    private Animator playerAnimator;
    private bool animatorHasWalkingParameter;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        if (rb != null)
        {
            rb.freezeRotation = true;
        }

        ResolveSetupReferences();
    }

    void Start()
    {
        lastCheckpoint = transform.position;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        hideLocalBodyFromCamera = true;
        ApplyLocalBodyVisibility();
    }

    void Update()
    {
        if (playerCamera == null)
        {
            ResolveSetupReferences();
        }

        if (Mouse.current != null)
        {
            Vector2 mouse = Mouse.current.delta.ReadValue();

            transform.Rotate(0, mouse.x * mouseSensitivity, 0);

            cameraRotation -= mouse.y * mouseSensitivity;
            cameraRotation = Mathf.Clamp(cameraRotation, -90f, 90f);

            if (playerCamera != null)
            {
                playerCamera.localRotation = Quaternion.Euler(cameraRotation, 0, 0);
            }
        }

        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame && isGrounded)
        {
            rb.AddForce(Vector3.up * GameplayRules.JumpForce, ForceMode.Impulse);
        }

        bool holdingGlide = !isGrounded && Keyboard.current != null && Keyboard.current.spaceKey.isPressed;

        // Glide only ENGAGES once you're no longer rising (top of the jump/arc).
        // That moment's height becomes your ceiling — you can never climb above it.
        if (holdingGlide && !isGliding && rb.linearVelocity.y <= 0.1f)
        {
            currentGlideSpeed = Mathf.Max(
                new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z).magnitude,
                GameplayRules.GlideStartSpeed
            );
            isGliding = true;
        }
        else if (!holdingGlide)
        {
            isGliding = false;
        }
    }

    private void ResolveSetupReferences()
    {
        if (playerCamera == null)
        {
            Camera childCamera = GetComponentInChildren<Camera>(true);
            if (childCamera != null)
            {
                playerCamera = childCamera.transform;
            }
        }

        AttachCameraToPlayerRoot();

        if (playerModel != null)
        {
            AlignPlayerModelToCollider();
            ResolvePlayerAnimator();
            return;
        }

        Transform existingModelRoot = transform.Find("PlayerModel");
        if (existingModelRoot != null)
        {
            playerModel = existingModelRoot;
            ResolvePlayerAnimator();
            return;
        }

        Transform animatedModelRoot = GetAnimatedModelRoot();
        if (animatedModelRoot != null)
        {
            playerModel = animatedModelRoot;
            AlignPlayerModelToCollider();
            ResolvePlayerAnimator();
            return;
        }

        List<Transform> visualChildren = new();

        foreach (Transform child in transform)
        {
            if (child == playerCamera || child.GetComponent<Camera>() != null)
            {
                continue;
            }

            if (child.GetComponentInChildren<Renderer>(true) != null ||
                child.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
            {
                visualChildren.Add(child);
            }
        }

        if (visualChildren.Count == 0)
        {
            return;
        }

        GameObject modelRoot = new("PlayerModel");
        Transform modelTransform = modelRoot.transform;
        modelTransform.SetParent(transform, false);
        modelTransform.localPosition = Vector3.zero;
        modelTransform.localRotation = Quaternion.identity;
        modelTransform.localScale = Vector3.one;

        foreach (Transform visualChild in visualChildren)
        {
            visualChild.SetParent(modelTransform, true);
        }

        playerModel = modelTransform;
        AlignPlayerModelToCollider();
        ResolvePlayerAnimator();
    }

    private Transform GetAnimatedModelRoot()
    {
        if (GetComponent<Animator>() == null)
        {
            return null;
        }

        Transform object2 = transform.Find("Object_2");
        if (object2 != null && object2.GetComponentInChildren<Renderer>(true) != null)
        {
            return object2;
        }

        foreach (Transform child in transform)
        {
            if (child == playerCamera || child.GetComponent<Camera>() != null)
            {
                continue;
            }

            if (child.GetComponentInChildren<Renderer>(true) != null)
            {
                return child;
            }
        }

        return null;
    }

    private void AttachCameraToPlayerRoot()
    {
        if (playerCamera == null)
        {
            return;
        }

        bool movedParent = playerCamera.parent != transform;
        if (movedParent)
        {
            playerCamera.SetParent(transform, false);
        }

        playerCamera.localPosition = GetCameraLocalPositionForWorldHeight();

        if (movedParent)
        {
            playerCamera.localRotation = Quaternion.identity;
        }

        playerCamera.localScale = Vector3.one;
    }

    private Vector3 GetCameraLocalPositionForWorldHeight()
    {
        Vector3 localPosition = defaultCameraLocalPosition;
        float rootScaleY = Mathf.Abs(transform.lossyScale.y);

        if (rootScaleY > 0.0001f)
        {
            localPosition.y = defaultCameraLocalPosition.y / rootScaleY;
        }

        return localPosition;
    }

    private void AlignPlayerModelToCollider()
    {
        if (alignedPlayerModelToCollider || playerModel == null)
        {
            return;
        }

        Renderer[] renderers = playerModel.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return;
        }

        bool hasBounds = false;
        Bounds visualBounds = default;

        foreach (Renderer renderer in renderers)
        {
            if (playerCamera != null && renderer.transform.IsChildOf(playerCamera))
            {
                continue;
            }

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

        float targetBottomY = GetColliderBottomY() + modelGroundClearance;
        float liftAmount = targetBottomY - visualBounds.min.y;

        if (Mathf.Abs(liftAmount) > 0.001f)
        {
            playerModel.position += Vector3.up * liftAmount;
        }

        alignedPlayerModelToCollider = true;
    }

    private float GetColliderBottomY()
    {
        Collider[] colliders = GetComponents<Collider>();
        float bottomY = transform.position.y;
        bool foundSolidCollider = false;

        foreach (Collider collider in colliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger)
            {
                continue;
            }

            if (!foundSolidCollider)
            {
                bottomY = collider.bounds.min.y;
                foundSolidCollider = true;
            }
            else
            {
                bottomY = Mathf.Min(bottomY, collider.bounds.min.y);
            }
        }

        return bottomY;
    }

    private void ResolvePlayerAnimator()
    {
        if (playerModel == null)
        {
            return;
        }

        if (playerAnimator == null)
        {
            playerAnimator = GetComponent<Animator>();
        }

        if (playerAnimator == null)
        {
            playerAnimator = playerModel.GetComponent<Animator>();
        }

        if (playerAnimator == null)
        {
            playerAnimator = playerModel.gameObject.AddComponent<Animator>();
        }

        if (playerAnimator.runtimeAnimatorController == null)
        {
            playerAnimator.runtimeAnimatorController = Resources.Load<RuntimeAnimatorController>(WalkControllerResourceName);
        }

        playerAnimator.applyRootMotion = false;
        playerAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animatorHasWalkingParameter = HasAnimatorParameter(playerAnimator, IsWalkingHash, AnimatorControllerParameterType.Bool);
    }

    private void SetWalkingAnimation(bool isWalkingNow)
    {
        if (playerAnimator == null)
        {
            ResolvePlayerAnimator();
        }

        if (playerAnimator != null && animatorHasWalkingParameter)
        {
            playerAnimator.SetBool(IsWalkingHash, isWalkingNow);
        }
    }

    private static bool HasAnimatorParameter(Animator animator, int parameterHash, AnimatorControllerParameterType parameterType)
    {
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            return false;
        }

        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            if (parameter.nameHash == parameterHash && parameter.type == parameterType)
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyLocalBodyVisibility()
    {
        Camera cameraComponent = playerCamera != null ? playerCamera.GetComponent<Camera>() : null;
        if (cameraComponent == null)
        {
            return;
        }

        int localBodyLayer = LayerMask.NameToLayer(LocalPlayerBodyLayerName);
        if (localBodyLayer < 0)
        {
            Debug.LogWarning($"Layer '{LocalPlayerBodyLayerName}' is missing. Local body will stay visible.");
            return;
        }

        if (originalPlayerCameraCullingMask < 0)
        {
            originalPlayerCameraCullingMask = cameraComponent.cullingMask;
        }

        Transform rendererRoot = playerModel != null ? playerModel : transform;
        foreach (Renderer renderer in rendererRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (playerCamera != null && renderer.transform.IsChildOf(playerCamera))
            {
                continue;
            }

#if !UNITY_EDITOR
            renderer.enabled = !hideLocalBodyFromCamera;
#else
            renderer.enabled = true;
            SetLayerRecursively(renderer.transform, localBodyLayer);
#endif
        }

#if UNITY_EDITOR
        cameraComponent.cullingMask = hideLocalBodyFromCamera
            ? originalPlayerCameraCullingMask & ~(1 << localBodyLayer)
            : originalPlayerCameraCullingMask;
#endif
    }

    private static void SetLayerRecursively(Transform root, int layer)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            child.gameObject.layer = layer;
        }
    }

    void FixedUpdate()
    {
        if (isGliding)
        {
            SetWalkingAnimation(false);
            Glide();
            return;
        }

        Vector2 moveInput = ReadMoveInput();
        float x = moveInput.x;
        float z = moveInput.y;

        Vector3 movement = transform.right * x + transform.forward * z;
        movement = Vector3.ClampMagnitude(movement, 1f);
        SetWalkingAnimation(isGrounded && movement.sqrMagnitude > 0.01f);

        Vector3 velocity = movement * (GameplayRules.WalkSpeed * currentSpeedMultiplier);
        float verticalVelocity = rb.linearVelocity.y;

        if (isGrounded)
            currentGlideSpeed = 0f;

        rb.linearVelocity = new Vector3(velocity.x, verticalVelocity, velocity.z);
    }

    void Glide()
    {
        Vector3 lookDir = (playerCamera != null ? playerCamera.forward : transform.forward).normalized;
        float pitch = lookDir.y; // +up, -down

        float naturalMaxSpeed = GameplayRules.LevelGlideSpeed + GameplayRules.DiveSpeedBonus;
        float maxSpeed = Mathf.Max(GameplayRules.GlideMaxSpeed, naturalMaxSpeed) * currentSpeedMultiplier;
        float levelSpeed = GameplayRules.LevelGlideSpeed * currentSpeedMultiplier;
        float diveBonus = GameplayRules.DiveSpeedBonus * currentSpeedMultiplier;

        // Looking up bleeds speed. Looking back down gradually builds it again.
        if (pitch < -0.05f)
        {
            float diveAmount = Mathf.InverseLerp(0.05f, 0.75f, -pitch);
            float diveTargetSpeed = levelSpeed + diveBonus * diveAmount;
            currentGlideSpeed = Mathf.MoveTowards(
                currentGlideSpeed,
                diveTargetSpeed,
                GameplayRules.GlideDiveAcceleration * diveAmount * Time.fixedDeltaTime
            );
        }
        else if (pitch > 0.05f)
        {
            float climbAmount = Mathf.InverseLerp(0.05f, 0.75f, pitch);
            currentGlideSpeed = Mathf.MoveTowards(
                currentGlideSpeed,
                GameplayRules.MinGlideSpeed,
                GameplayRules.GlideClimbSlowdown * climbAmount * Time.fixedDeltaTime
            );
        }

        currentGlideSpeed -= currentGlideSpeed * GameplayRules.GlideDrag * Time.fixedDeltaTime;

        if (Mathf.Abs(pitch) < 0.15f && currentGlideSpeed < levelSpeed)
        {
            currentGlideSpeed = Mathf.MoveTowards(
                currentGlideSpeed,
                levelSpeed,
                GameplayRules.LevelGlideAcceleration * Time.fixedDeltaTime
            );
        }

        currentGlideSpeed = Mathf.Clamp(currentGlideSpeed, GameplayRules.MinGlideSpeed, maxSpeed);

        Vector3 targetVelocity = lookDir * currentGlideSpeed;
        targetVelocity.y -= GameplayRules.GlideSink;
        targetVelocity.y = Mathf.Min(targetVelocity.y, -GameplayRules.GlideMinDescentSpeed);

        Vector3 newVelocity = Vector3.Lerp(rb.linearVelocity, targetVelocity, GameplayRules.GlideTurnSpeed * Time.fixedDeltaTime);
        newVelocity.y = Mathf.Min(newVelocity.y, -GameplayRules.GlideMinDescentSpeed);

        rb.linearVelocity = newVelocity;
    }

    private Vector2 ReadMoveInput()
    {
        if (Keyboard.current != null)
        {
            float x = 0f;
            float z = 0f;

            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) x -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) x += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) z -= 1f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) z += 1f;

            return Vector2.ClampMagnitude(new Vector2(x, z), 1f);
        }

        return new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
    }

    void LateUpdate()
    {
        if (playerModel == null) return;

        Quaternion target;

        if (isGliding)
        {
            Vector3 lookDir = (playerCamera != null ? playerCamera.forward : transform.forward).normalized;
            float lookPitchDeg = Mathf.Asin(Mathf.Clamp(lookDir.y, -1f, 1f)) * Mathf.Rad2Deg;

            float bodyPitch = glideBodyPitch - lookPitchDeg;
            float roll = -Input.GetAxis("Horizontal") * glideBankAngle;

            target = Quaternion.Euler(bodyPitch, 0f, roll);
        }
        else
        {
            target = Quaternion.identity;
        }

        playerModel.localRotation = Quaternion.Slerp(
            playerModel.localRotation,
            target,
            glideAnimSmooth * Time.deltaTime
        );
    }

    void OnCollisionStay(Collision collision)
    {
        foreach (ContactPoint contact in collision.contacts)
        {
            if (contact.normal.y > 0.5f)
            {
                isGrounded = true;
                isGliding = false;
                return;
            }
        }
    }

    void OnCollisionExit(Collision collision)
    {
        isGrounded = false;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Checkpoint"))
        {
            ActivateCheckpoint(other.transform.position);
            Destroy(other.gameObject);
        }
    }

    public void ActivateCheckpoint(Vector3 checkpointPosition)
    {
        lastCheckpoint = checkpointPosition;

        if (speedBoostCoroutine != null)
            StopCoroutine(speedBoostCoroutine);

        speedBoostCoroutine = StartCoroutine(SpeedBoost());
    }

    IEnumerator SpeedBoost()
    {
        Debug.Log("SPEED BOOST");
        currentSpeedMultiplier = GameplayRules.CheckpointBoostMultiplier;

        currentGlideSpeed = Mathf.Max(currentGlideSpeed, GameplayRules.GlideStartSpeed) + GameplayRules.CheckpointGlideBoostBurst;

        yield return new WaitForSeconds(GameplayRules.CheckpointBoostDuration);

        currentSpeedMultiplier = 1f;
        speedBoostCoroutine = null;
    }
}
