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

    [Range(0f, 1f)][SerializeField] private float runStrafeThreshold = 0.1f;

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
    }

    private void Update()
    {
        if (controller == null || !controller.enabled || input == null) return;

        if (movementPaused)
        {
            // while paused keep vertical physics gravity but zero horizontal movement so
            // the player remains in an idle pose while animator can still run
            IsGrounded = controller.isGrounded;

            // zero horizontal velocity
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
        // only allow sprint when input asks for it and stamina permits it
        bool wantsRun = input.RunHeld && vertical > 0f && Mathf.Abs(horizontal) <= runStrafeThreshold;
        // Ask stamina to sprint, but then read back the authoritative state from stamina.
        // This prevents using a stale local `IsRunning` value when stamina depletes later
        // in the frame (execution order differences between components).
        stamina?.SetSprinting(wantsRun && (horizontal != 0f || vertical != 0f), input.RunHeld);
        IsRunning = (stamina != null) ? stamina.IsSprinting : wantsRun;

        // Defensive: if stamina is depleted this frame, ensure sprinting is stopped
        // immediately so the player cannot continue running for another frame due
        // to execution order between components.
        if (stamina != null && stamina.StaminaPercent <= stamina.DepletedPercentThreshold)
        {
            stamina.SetSprinting(false, input.RunHeld);
            IsRunning = false;
        }

        if (IsGrounded)
        {
            coyoteTimer = coyoteTime;
            // Use the stamina-backed run state to determine target speed so sprinting
            // is disabled the moment stamina stops permitting it.
            targetSpeed = IsRunning ? runSpeed : walkSpeed;
            if (horizontal == 0f && vertical == 0f) targetSpeed = 0f;
        }
        else
        {
            coyoteTimer = Mathf.Max(0f, coyoteTimer - Time.deltaTime);
        }

        float accelRate = (targetSpeed > currentSpeed) ? acceleration : deceleration;
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, accelRate * Time.deltaTime);

        // movement math
        Vector3 inputDir = Vector3.zero;
        if (horizontal != 0f || vertical != 0f)
        {
            inputDir = (transform.right * horizontal + transform.forward * vertical).normalized;
        }

        if (IsGrounded)
        {
            Vector3 moveDir = inputDir;
            // preserve momentum when stopping
            if (inputDir == Vector3.zero && currentSpeed > 0.1f)
            {
                Vector3 horizontalVel = new Vector3(velocity.x, 0, velocity.z);
                if (horizontalVel.sqrMagnitude > 0.001f)
                    moveDir = horizontalVel.normalized;
            }

            Vector3 moveVelocity = moveDir * currentSpeed;

            // Prevent sliding down slopes by applying a stronger downward force when grounded
            float groundStickForce = -2f;
            if (inputDir == Vector3.zero)
            {
                groundStickForce = -10f; // Stick harder when not moving
            }

            velocity = new Vector3(moveVelocity.x, velocity.y < 0f ? groundStickForce : velocity.y, moveVelocity.z);
        }
        else
        {
            // physics math
            if (inputDir != Vector3.zero)
            {
                Vector3 horizontalVel = new Vector3(velocity.x, 0f, velocity.z);
                Vector3 targetVel = inputDir * Mathf.Max(horizontalVel.magnitude, walkSpeed * airControl);
                Vector3 newHorizontal = Vector3.MoveTowards(horizontalVel, targetVel, walkSpeed * airControl * Time.deltaTime);
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
            // jump math
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
    /// pause horizontal movement used by dialogue system while choices are shown
    /// vertical physics gravity will continue so the player does not teleport
    /// </summary>
    public void PauseMovement()
    {
        movementPaused = true;
        // stop horizontal velocity immediately
        velocity.x = 0f;
        velocity.z = 0f;
        currentSpeed = 0f;
    }

    /// <summary>
    /// resume movement after a pause
    /// </summary>
    public void ResumeMovement()
    {
        movementPaused = false;
    }
}
