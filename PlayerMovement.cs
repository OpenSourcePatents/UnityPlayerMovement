using UnityEngine;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInput))]
public class PlayerMovement : MonoBehaviour
{
    // speeds
    [Header("Speed")]
    [SerializeField] private float walkSpeed = 2.0f;
    [SerializeField] private float runSpeed = 4.0f;
    [SerializeField] private float acceleration = 10.0f;
    [SerializeField] private float deceleration = 15.0f;

    // physics and jump
    [Header("Physics & Jump")]
    [SerializeField] private float gravity = -20.0f;
    [SerializeField] private float jumpHeight = 1.2f;
    [Range(0f, 1f)][SerializeField] private float airControl = 0.1f;
    [SerializeField] private float jumpCooldown = 0.2f;
    [SerializeField] private float coyoteTime = 0.1f;
    [SerializeField] private float jumpBufferTime = 0.1f;

    // slope handling
    [Header("Slope & Ground")]
    [SerializeField] private float groundStickMoving = -2f;
    [SerializeField] private float groundStickIdle = -10f;
    [SerializeField] private float maxSlopeAngle = 45f;

    // sprint config
    [Header("Sprint")]
    [Range(0f, 1f)][SerializeField] private float runStrafeThreshold = 0.1f;

    // components
    private CharacterController controller;
    private PlayerInput input;
    private PlayerStamina stamina;

    // runtime state
    private Vector3 velocity;
    private float currentSpeed;
    private float targetSpeed;
    private float jumpTimer;
    private float coyoteTimer;
    private float jumpBufferTimer;

    // pause flag used to temporarily freeze horizontal movement e g during dialogue choices
    private bool movementPaused = false;
    public bool IsMovementPaused
    {
        get => movementPaused;
        private set => movementPaused = value;
    }

    // cached air control speed to avoid redundant multiplications per frame
    private float airControlSpeed;

    // public api
    public Vector3 CurrentVelocity { get; private set; }
    public bool IsGrounded { get; private set; }
    public bool IsRunning { get; private set; }
    public Vector2 CurrentInput { get; private set; }
    public float WalkSpeed => walkSpeed;
    public float RunSpeed => runSpeed;
    public float CurrentSpeed => currentSpeed;

    // unity callbacks
    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        input = GetComponent<PlayerInput>();
        stamina = GetComponent<PlayerStamina>();

        // cache once rather than recomputing every frame
        airControlSpeed = walkSpeed * airControl;
    }

    private void Update()
    {
        if (controller == null || !controller.enabled || input == null) return;

        if (IsMovementPaused)
        {
            // while paused keep vertical physics gravity but zero horizontal movement so
            // the player remains in an idle pose while animator can still run
            IsGrounded = controller.isGrounded;

            velocity.x = 0f;
            velocity.z = 0f;

            // clear current input so HeadBob settles to idle instead of using stale values
            CurrentInput = Vector2.zero;

            // apply gravity so player can fall if in air
            velocity.y += gravity * Time.deltaTime;

            controller.Move(velocity * Time.deltaTime);
            CurrentVelocity = velocity;
            stamina?.SetSprinting(false, false);
            return;
        }

        IsGrounded = controller.isGrounded;

        // Normalize input to prevent faster diagonal movement
        CurrentInput = Vector2.ClampMagnitude(input.Move, 1f);

        float horizontal = CurrentInput.x;
        float vertical = CurrentInput.y;
        bool hasInput = horizontal != 0f || vertical != 0f;

        // only allow sprint when input asks for it and stamina permits it
        bool wantsRun = input.RunHeld && vertical > 0f && Mathf.Abs(horizontal) <= runStrafeThreshold;

        // Delegate sprint authority entirely to stamina. Do not add a secondary
        // depletion check here -- stamina.SetSprinting already handles cutoff
        // internally, so duplicating it causes double-stopping and potential
        // one-frame desync between components.
        stamina?.SetSprinting(wantsRun && hasInput, input.RunHeld);
        IsRunning = (stamina != null) ? stamina.IsSprinting : wantsRun;

        if (IsGrounded)
        {
            coyoteTimer = coyoteTime;
            targetSpeed = IsRunning ? runSpeed : walkSpeed;
            if (!hasInput) targetSpeed = 0f;
        }
        else
        {
            coyoteTimer = Mathf.Max(0f, coyoteTimer - Time.deltaTime);
        }

        float accelRate = (targetSpeed > currentSpeed) ? acceleration : deceleration;
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, accelRate * Time.deltaTime);

        // movement math
        Vector3 inputDir = Vector3.zero;
        if (hasInput)
        {
            inputDir = (transform.right * horizontal + transform.forward * vertical).normalized;
        }

        if (IsGrounded)
        {
            // Slope angle check -- stop movement on slopes steeper than maxSlopeAngle
            // to prevent the player climbing geometry the CharacterController alone
            // would otherwise allow them to slide up or walk on.
            if (IsOnSteepSlope())
            {
                targetSpeed = 0f;
                currentSpeed = 0f;
            }

            Vector3 moveDir = inputDir;
            // preserve momentum when stopping
            if (inputDir == Vector3.zero && currentSpeed > 0.1f)
            {
                Vector3 horizontalVel = new Vector3(velocity.x, 0, velocity.z);
                if (horizontalVel.sqrMagnitude > 0.001f)
                    moveDir = horizontalVel.normalized;
            }

            Vector3 moveVelocity = moveDir * currentSpeed;

            // Use serialized ground stick values instead of magic numbers
            float groundStickForce = hasInput ? groundStickMoving : groundStickIdle;
            velocity = new Vector3(moveVelocity.x, velocity.y < 0f ? groundStickForce : velocity.y, moveVelocity.z);
        }
        else
        {
            // physics math -- use cached airControlSpeed
            if (inputDir != Vector3.zero)
            {
                Vector3 horizontalVel = new Vector3(velocity.x, 0f, velocity.z);
                Vector3 targetVel = inputDir * Mathf.Max(horizontalVel.magnitude, airControlSpeed);
                Vector3 newHorizontal = Vector3.MoveTowards(horizontalVel, targetVel, airControlSpeed * Time.deltaTime);
                velocity.x = newHorizontal.x;
                velocity.z = newHorizontal.z;
            }

            velocity.y += gravity * Time.deltaTime;
        }

        // Head bumping on ceiling
        if ((controller.collisionFlags & CollisionFlags.Above) != 0 && velocity.y > 0f)
        {
            velocity.y = 0f;
        }

        if (jumpTimer > 0f) jumpTimer -= Time.deltaTime;

        if (input.ConsumeJump())
        {
            jumpBufferTimer = jumpBufferTime;
        }
        else
        {
            jumpBufferTimer = Mathf.Max(0f, jumpBufferTimer - Time.deltaTime);
        }

        bool canJump = jumpTimer <= 0f && coyoteTimer > 0f && jumpBufferTimer > 0f;
        if (canJump)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            jumpTimer = jumpCooldown;
            jumpBufferTimer = 0f;
            coyoteTimer = 0f;
        }

        controller.Move(velocity * Time.deltaTime);
        CurrentVelocity = velocity;
    }

    /// <summary>
    /// Returns true if the surface directly below the player exceeds maxSlopeAngle.
    /// Uses a short downward raycast from the controller's base.
    /// </summary>
    private bool IsOnSteepSlope()
    {
        Vector3 origin = transform.position + Vector3.up * 0.1f;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, controller.height * 0.5f + 0.3f))
        {
            float angle = Vector3.Angle(hit.normal, Vector3.up);
            return angle > maxSlopeAngle;
        }
        return false;
    }

    /// <summary>
    /// Pause horizontal movement used by dialogue system while choices are shown.
    /// Vertical physics gravity will continue so the player does not teleport.
    /// </summary>
    public void PauseMovement()
    {
        IsMovementPaused = true;
        velocity.x = 0f;
        velocity.z = 0f;
        currentSpeed = 0f;
    }

    /// <summary>
    /// Resume movement after a pause.
    /// </summary>
    public void ResumeMovement()
    {
        IsMovementPaused = false;
    }
}
