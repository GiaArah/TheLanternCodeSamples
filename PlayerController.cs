using System;
using System.Collections;
using System.Collections.Generic;
using Cinemachine;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    [Header("Player Control References")] 
    [SerializeField] private PlayerInput movementControls;
    private Rigidbody rb;
    private bool playerControlEnabled = true;
    
    public enum PlayerState
    {
        Idle,
        Walking,
        Jumping,
        Boosting,
        Flying,
    }
    public PlayerState playerState = PlayerState.Idle;
    
    [Header("Camera References")]
    public GameObject mainCamera;
    [SerializeField] private CinemachineFreeLook freeLookCam;
    [SerializeField] private CinemachineVirtualCamera aimCamera;

    [Header("UI References")] 
    private FlightUI flightUI = null;

    [Header("Basic Movement Variables")] 
    [SerializeField] float currentMoveSpeed = 4.0f;
    [SerializeField] float maxMoveSpeed = 8.0f;
    [SerializeField] float accelerationSpeed = 2.0f;
    [SerializeField] private float jumpForce = 10.0f;
    [SerializeField] private GameObject outOfBoundsObject;
    private Vector3 lastGroundedPosition;
    private float horizontalInput;
    private float verticalInput;
    private float playerHeight;
    private bool hasJumped;
    private bool isJumping;

    [Header("Flight Variables")]
    [SerializeField] private float flightBoostTime = 1f;
    [SerializeField] private float flightBoostCooldown = 1f;
    [SerializeField] private int maxNumFlightBoosts = 3;
    [SerializeField] private float flightBoostForce = 8.0f;
    [SerializeField] private float flightHorizontalForce = 2.0f;
    [SerializeField] private float minFlightDownwardForce = 1.0f;
    [SerializeField] private float maxFlightDownwardForce = 2.0f;
    [SerializeField] private float downwardForceAcceleration = 8.0f;
    [SerializeField] private float cameraFlightDownwardForceMax = -1.5f;
    [SerializeField] private float cameraFlightDownwardForceMin = -0.1f;
    private float flightDownwardForce = 2.0f;
    private float flightBoostCooldownTimer = 0.0f;
    private int currentFlightBoosts = 3;
    private int layerMask = 1 << 7; // bit shifting the index of the Environment Layer
    
    [SerializeField] private List<ParticleSystem> flightParticles; 
    [SerializeField] private ParticleSystem flightBoostParticles;

    [Header("Game State Booleans")] 
    public bool isAiming = false;
    private bool isInDialogue = false;
    private bool isInCutscene = false;

    [Header("Lerp Variables")]
    private bool isLerping = false;
    private float lerpRotationTimeVal = 0f;

    [Header("Audio")]
    private float windLerpRTPC = 0.0f;

    [Header("Animation")] 
    [SerializeField] private Animator animator;
    
    void Start()
    {
        InitializePlayer();
        StartCoroutine(InitializeVariables());
    }

    private void InitializePlayer()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        rb = GetComponent<Rigidbody>();
        mainCamera = GameObject.Find("Main Camera");
        playerHeight = GetComponent<CapsuleCollider>().bounds.size.y;
        lastGroundedPosition = transform.position;
    }
    
    private IEnumerator InitializeVariables()
    {
        // the player is initialized before the UI scene, so we need a coroutine to ensure that the variable gets assigned
        GameObject flightUI_parent;
        while ((flightUI_parent = GameObject.FindWithTag("UI_Parent")) == null)
        {
            yield return null;
        }
        flightUI = flightUI_parent.GetComponent<FlightUI>();
        flightUI.InitializeFlightUI();
    }

    private void Update()
    {
        if (!playerControlEnabled || GameManager.Instance.GetGameMode() == GameManager.GameMode.GAMEMODE_2D)
        {
            return;
        }
        UpdateGroundedState();
        CheckBounds();
    }

    void FixedUpdate()
    {
        if (!playerControlEnabled || GameManager.Instance.GetGameMode() == GameManager.GameMode.GAMEMODE_2D)
        {
            return;
        }
        MoveCharacter();
        
        if (playerState == PlayerState.Flying)
        {
            ApplyFlightForces();
        }
        UpdateAnimationState();
    }
    
    /*
    * A method that checks whether the player has touched the ground on the current frame
    */
    private void UpdateGroundedState()
    {
        bool isGrounded = CheckIfGrounded();
        
        // if the player just landed on this frame, reset to the grounded idle state
        if(isGrounded && (IsInFlyingState(playerState) || playerState == PlayerState.Jumping) && !isJumping)
        {
            AudioManager.Instance.StopSound("Play_Wind");
            AudioManager.Instance.PlaySound("Play_Player_Landing", transform);
            AudioManager.Instance.SetMusicState("Grounded");
            
            rb.useGravity = true;
            hasJumped = false;
            lastGroundedPosition = transform.position;
            
            playerState = PlayerState.Idle;
            flightDownwardForce = minFlightDownwardForce;
            UpdateFlightParticles();

            StartCoroutine(flightUI.ResetSliders());
        }

        // if the player is grounded, make sure the player's pitch is reset
        if (IsInGroundedState(playerState) && (!Mathf.Approximately(0.0f, transform.rotation.x)) && !isAiming)
        {
            Quaternion targetRotation = Quaternion.Euler(0f, transform.rotation.y, transform.rotation.z);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, lerpRotationTimeVal);
            lerpRotationTimeVal += Time.deltaTime * currentMoveSpeed/2f;
            if (lerpRotationTimeVal >= 1.0f && Mathf.Approximately(0.0f, transform.rotation.x))
            {
                lerpRotationTimeVal = 0f;
            }
        }
    }
    
    private IEnumerator FlightBoostCooldown()
    {
        flightBoostCooldownTimer = flightBoostCooldown;
        while (flightBoostCooldownTimer > 0.0f)
        {
            flightBoostCooldownTimer -= Time.deltaTime;
            yield return null;
        }

        flightBoostCooldownTimer = 0.0f;
    }
    
    private void CheckBounds()
    {
        // ensure player has not gone below the out of bounds y value
        if (transform.position.y <= outOfBoundsObject.transform.position.y)
        {
            Respawn();
        }
    }
    
    private void MoveCharacter()
    {
        Vector3 moveDirection;
        
        // flight-based movement:
        if (IsInFlyingState(playerState) && !isAiming)
        {
            // get the forward direction y value, clamp to a 0-1 value, and apply an additional force based on value
            float clampedForward = (mainCamera.transform.forward.y + 1f) / 2f;
            float verticalVelocity = Mathf.Lerp(cameraFlightDownwardForceMax, cameraFlightDownwardForceMin,clampedForward); 

            // if camera looks up, minimize downward acceleration by resetting velocity before applying force
            if (clampedForward > 0.5f)
            {
                if (rb.velocity.y < -0.1f)
                {
                    SetVerticalVelocity(-0.1f);
                }
            }
            // if the camera looks down, accelerate towards the ground to land faster
            else if (clampedForward < 0.2f)
            {
                verticalVelocity *= 10f;
            }
            Vector3 downwardForce = new Vector3(0f, verticalVelocity, 0f);
            rb.AddForce(downwardForce);

            // horizontal movement while flying
            Vector3 forwardFromCamera = new Vector3(mainCamera.transform.forward.x, 0.0f, mainCamera.transform.forward.z).normalized;
            moveDirection = forwardFromCamera * verticalInput + mainCamera.transform.right * horizontalInput;

            if (moveDirection != Vector3.zero)
            {
                if (currentMoveSpeed < maxMoveSpeed)
                {
                    currentMoveSpeed += accelerationSpeed * Time.deltaTime;
                }

                Vector3 newVel = (moveDirection * currentMoveSpeed * flightHorizontalForce);
                rb.velocity = new Vector3(newVel.x, rb.velocity.y, newVel.z);
            }
            else
            {
                rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
            }
        }
        // ground-based movement:
        else
        {
            moveDirection = mainCamera.transform.forward * verticalInput + mainCamera.transform.right * horizontalInput;

            // determine based on move direction whether to change state to walking
            if (moveDirection != Vector3.zero && IsInGroundedState(playerState))
            {
                playerState = PlayerState.Walking;
            }
            else if(moveDirection == Vector3.zero && IsInGroundedState(playerState))
            {
                playerState = PlayerState.Idle;
            }
            
            if (currentMoveSpeed < maxMoveSpeed)
            {
                currentMoveSpeed += accelerationSpeed * Time.deltaTime;
            }

            // horizontal movement
            Vector3 newVel = (moveDirection * currentMoveSpeed);
            rb.velocity = new Vector3(newVel.x, rb.velocity.y, newVel.z);
        }
        HandleRotation(moveDirection);
    }
    
    private void HandleRotation(Vector3 moveDirection)
    {
        if (isAiming) return;
        
        float rotationAngleX = 0f;
        float rotationAngleZ = 0f;

        if (IsInFlyingState(playerState))
        {
            // rotate player towards the direction input
            rotationAngleZ = horizontalInput * -30.0f;

            // linearized rotation based on camera angle
            float clampedForward = (mainCamera.transform.forward.y + 1f) / 2f;
            rotationAngleX = Mathf.Lerp(-60f,130f,clampedForward) * -1f;
        }

        // if the player is inputting movement commands or in a flying state, make sure player faces where the camera's forward is
        if (IsInFlyingState(playerState) || moveDirection != Vector3.zero)
        {
            float targetAngle = Mathf.Atan2(moveDirection.x, moveDirection.z) * Mathf.Rad2Deg;
            Quaternion targetRotation = Quaternion.Euler(rotationAngleX, targetAngle, rotationAngleZ);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * currentMoveSpeed);
        }


        // Wwise audio: send rotation value to wind RTPC
        if (IsInFlyingState(playerState))
        {
            float rtpcVal = ((transform.localEulerAngles.x - 30f) / (50f - 30f)) * 100f;
            Mathf.Clamp(rtpcVal, 0f, 100f);
            AudioManager.Instance.SetEffectsRTPC("Flight_Speed", rtpcVal);
        }
    }

    void UpdateAnimationState()
    { 
        animator.SetBool("IsGrounded",(playerState == PlayerState.Idle));
        animator.SetBool("IsFlying", IsInFlyingState(playerState));
        animator.SetBool("IsWalking", (playerState == PlayerState.Walking));
    }
    
    public int GetCurrentBoosts()
    {
        return currentFlightBoosts; 
    }

    public void ResetCurrentBoosts()
    {
        currentFlightBoosts = maxNumFlightBoosts;
    }

    public void SetBoostIndex(int indexVal)
    {
        if (indexVal > maxNumFlightBoosts) return;
        
        currentFlightBoosts = indexVal;
    }

    public void SetIsInDialogue(bool inDialogue)
    {
        isInDialogue = inDialogue;
    }

    public void SetIsInCutscene(bool inCutscene)
    {
        isInCutscene = inCutscene;
    }
    public void SetPlayerControlEnabled(bool isEnabled)
    {
        if (isEnabled)
        {
            if (!isInCutscene && !isInDialogue &&
                GameManager.Instance.GetGameMode() != GameManager.GameMode.GAMEMODE_2D)
            {
                playerControlEnabled = true;
                OnEnable();
            }
        }
        else
        {
            playerControlEnabled = false;
            OnDisable();
        }
    }

    public bool GetIsPlayerControlEnabled()
    {
        return playerControlEnabled;
    }
    
    public CinemachineFreeLook GetMainCamera()
    {
        return freeLookCam;
    }
    
    public CinemachineVirtualCamera GetAimCamera()
    {
        return aimCamera;
    }

    private bool IsInFlyingState(PlayerState state)
    {
        return state == PlayerState.Flying || state == PlayerState.Boosting;
    }
    
    private bool IsInGroundedState(PlayerState state)
    {
        return state == PlayerState.Idle || state == PlayerState.Walking;
    }
    
    private bool CheckIfGrounded()
    {
        bool isGrounded = Physics.Raycast(transform.position, Vector3.down, playerHeight * 0.5f + 0.3f, layerMask);
        return isGrounded;
    }
    
    private void SetVerticalVelocity(float verticalVelocity)
    {
        rb.velocity = new Vector3(rb.velocity.x, verticalVelocity, rb.velocity.z);
    }
    
    private void OnEnable()
    {
        movementControls.actions.Enable();
    }

    /*
     * Unity Input System Methods
     */
    private void OnDisable()
    {
        movementControls.actions.Disable();
    }

    public void OnMove(InputValue value)
    {
        Vector2 inputMovement = value.Get<Vector2>();
        horizontalInput = inputMovement.x;
        verticalInput = inputMovement.y;
    }
    
    public void OnJump(InputValue value)
    {
        // if player hasn't jumped yet and is grounded, jump
        if (IsInGroundedState(playerState))
        {
            StartCoroutine(Jump());
        }
        
        // if player is airborne and has jumped, activate flight boost buff
        else if (hasJumped && !IsInGroundedState(playerState) && playerState != PlayerState.Boosting && (flightBoostCooldownTimer <= 0.0f) && (currentFlightBoosts > 0))
        {
            rb.useGravity = false;

            if (rb.velocity.y < 0f)
            {
                Vector3 resetVelocity = new Vector3(rb.velocity.x, 1.0f, rb.velocity.z);
                rb.velocity = resetVelocity;
            }

            currentFlightBoosts--;
            AudioManager.Instance.SetMusicState("Flying");
            if (!AudioManager.Instance.CheckSoundID("Play_Wind"))
            {
                AudioManager.Instance.PlaySound("Play_Wind", transform);
            }
            
            StartCoroutine(FlightBoost());
            StartCoroutine(flightUI.SlideSlider(0.0f, currentFlightBoosts));
        }
    }

    /*
     * Used when the player starts a 2d puzzle sequence
     * player character is moved to a specified position in front of the puzzle
    */
    public IEnumerator LerpToPuzzle(Transform newPosition)
    {
        SetPlayerControlEnabled(false);
        float lerpStartTime = Time.time;
        float lengthOfLerp = Vector3.Distance(transform.position, newPosition.position);
        isLerping = true;

        while (Vector3.Distance(transform.position, newPosition.position) >= 0.01f)
        {
            float distCovered = (Time.time - lerpStartTime) * currentMoveSpeed;
            float fractionOfJourney = distCovered / lengthOfLerp;
            transform.position = Vector3.Lerp(transform.position, newPosition.position, fractionOfJourney);
            yield return null;
        }
        
        SetPlayerControlEnabled(true);
        playerState = PlayerState.Idle;
        UpdateAnimationState();
        isLerping = false;
    }

    /*
     * Adds a jump force on the player, then after 0.5 seconds revokes jumping state
     */
    private IEnumerator Jump()
    {
        AudioManager.Instance.PlaySound("Play_Player_Jump", transform);
        rb.velocity = Vector3.zero;
        rb.AddForce(transform.up * jumpForce * 2f, ForceMode.Impulse);
        playerState = PlayerState.Jumping;
        isJumping = true;
        hasJumped = true;

        yield return new WaitForSeconds(0.5f);
        isJumping = false;
    }

    /*
     * Increases number of times the player can press space for a boost
     */
    public void IncreaseNumFlightBoosts()
    {
        maxNumFlightBoosts++;
        currentFlightBoosts = maxNumFlightBoosts;
        
        StartCoroutine(flightUI.ResetSliders());
    }

    private void Respawn()
    {
        transform.position = lastGroundedPosition;
    }
    
    /*
     * Apply upward forces to the player while they have the buff for "flightBoostTime" seconds
     */
    private IEnumerator FlightBoost()
    {
        playerState = PlayerState.Boosting;
        AudioManager.Instance.PlaySound("Play_Player_FlightBoost", transform);

        // activate VFX
        UpdateFlightParticles();
        flightBoostParticles.Play();
        
        float timeElapsed = 0.0f;

        // apply vertical velocity
        while (timeElapsed < flightBoostTime)
        {
            float currentForce = Mathf.Lerp(0, flightBoostForce, timeElapsed / flightBoostTime);
            rb.AddForce(Vector3.up * currentForce, ForceMode.Acceleration);

            if (rb.velocity.y > flightBoostForce)
            {
                rb.velocity = new Vector3(rb.velocity.x, flightBoostForce, rb.velocity.z);
            }
            
            timeElapsed += Time.deltaTime;
            yield return null;
        }

        yield return new WaitForSeconds(0.5f);
        playerState = PlayerState.Flying;
        flightBoostParticles.Stop();
        StartCoroutine(FlightBoostCooldown());
    }

    private void UpdateFlightParticles()
    {
        foreach (var particle in flightParticles)
        {
            if (IsInFlyingState(playerState))
            {
                particle.Play();
            }
            else
            {
                particle.Stop();
            }
        }
    }

    private void ApplyFlightForces()
    {
        if (playerState == PlayerState.Boosting || IsInGroundedState(playerState)) return;
        
        // apply a base downward/gravitational force to the player when airborne
        if (flightDownwardForce < maxFlightDownwardForce)
        {
            flightDownwardForce += downwardForceAcceleration * Time.deltaTime;
        }
        Vector3 downwardForce = (Vector3.up * -flightDownwardForce);
        rb.AddForce(downwardForce);
    }
} 
