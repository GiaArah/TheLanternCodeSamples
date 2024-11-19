using System;
using System.Collections;
using System.Collections.Generic;
using Cinemachine;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;
using USCG.Core.Telemetry;
using Cursor = UnityEngine.Cursor;

public class PlayerMovement : MonoBehaviour
{
    [Header("Player Control References")]
    public GameObject mainCamera;
    [SerializeField] private CinemachineFreeLook freeLookCam;
    [SerializeField] private CinemachineVirtualCamera aimCamera;

    private Rigidbody rb;
    private bool playerControlEnabled = true;

    [Header("UI References")] 
    public FlightUI flightUI = null;

    [Header("Basic Movement Variables")] 
    [SerializeField] float currentMoveSpeed = 4.0f;
    [SerializeField] float maxMoveSpeed = 8.0f;
    [SerializeField] float accelerationSpeed = 2.0f;
    [SerializeField] private float jumpForce = 10.0f;
    [SerializeField] private GameObject outOfBoundsObject;
    private Vector3 lastGroundedPosition;
    private float horizontalInput;
    private float verticalInput;
    private float playerHeight = 0.0f;
    public bool isGrounded = false;
    public bool isJumping = false;

    [Header("Flight Variables")]
    public bool isLaunching = false;
    public bool isFlying = false;
    public float flightBoostTime = 1f;
    public float flightBoostCooldownTimer = 0.0f;
    [SerializeField] private int maxNumFlightBoosts = 3;
    [SerializeField] private int currentFlightBoosts = 3;
    [SerializeField] private float flightBoostCooldown = 1f;
    [SerializeField] private float flightBoostForce = 8.0f;
    [SerializeField] private float flightHorizontalForce = 2.0f;
    [SerializeField] private float minFlightDownwardForce = 1.0f;
    [SerializeField] private float maxFlightDownwardForce = 2.0f;
    [SerializeField] private float flightDownwardForce = 1.0f;
    [SerializeField] private float downwardForceAcceleration = 8.0f;
    private float currentDownwardForceSpeed = 1.0f;
    private int layerMask = 1 << 7; // bit shifting the index of the Environment Layer
    
    [SerializeField] private List<ParticleSystem> flightParticles; 
    [SerializeField] private ParticleSystem flightBoostParticles;

    [Header("Game State Booleans")] 
    public bool isAiming = false;
    private bool isInDialogue = false;
    private bool isInCutscene = false;

    [Header("Lerp Variables")]
    private bool isLerping = false;
    private Transform lerpPosition;
    private float lerpStartTime = 0.0f;
    private float lengthOfLerp = 0.0f;

    [Header("Metrics")]
    private MetricId fallenMetric;
    private MetricId airtimeMetric;
    
    [Header("Audio")]
    private float windLerpRTPC = 0.0f;

    void Start()
    {
        // Telemetry Data
        fallenMetric = TelemetryManager.instance.CreateAccumulatedMetric("Number of Times Fallen");
        airtimeMetric = TelemetryManager.instance.CreateAccumulatedMetric("Time Spent in Flight (ms)");

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
        GetInput();
        CheckJump();
        CheckFlight();
    }

    void FixedUpdate()
    {
        if (!playerControlEnabled || GameManager.Instance.GetGameMode() == GameManager.GameMode.GAMEMODE_2D)
        {
            return;
        }
        MoveCharacter();
        
        if (isFlying)
        {
            ApplyFlightForces();
            TelemetryManager.instance.AccumulateMetric(airtimeMetric, Time.deltaTime);
        }
        CheckBounds();
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
            }
        }
        else
        {
            playerControlEnabled = false;
        }
    }

    public bool GetIsPlayerControlEnabled()
    {
        return playerControlEnabled;
    }
    
    private void GetInput()
    {
        horizontalInput = Input.GetAxisRaw("Horizontal");
        verticalInput = Input.GetAxisRaw("Vertical");
    }


    public IEnumerator LerpToPuzzle(Transform newPosition)
    {
        SetPlayerControlEnabled(false);
        lerpStartTime = Time.time;
        lerpPosition = newPosition;
        lengthOfLerp = Vector3.Distance(transform.position, newPosition.position);
        isLerping = true;
        
        while (Vector3.Distance(transform.position, lerpPosition.position) >= 0.01f)
        {
            float distCovered = (Time.time - lerpStartTime) * currentMoveSpeed;
            float fractionOfJourney = distCovered / lengthOfLerp;
            transform.position = Vector3.Lerp(transform.position, lerpPosition.position, fractionOfJourney);
            yield return null;
        }
        
        SetPlayerControlEnabled(true);
        isLerping = false;
        lerpPosition = null;
    }

    private void CheckJump()
    {
        isGrounded = Physics.Raycast(transform.position, Vector3.down, playerHeight * 0.5f + 0.3f, layerMask);
        if(isGrounded)
        {
            rb.useGravity = true;
            AudioManager.Instance.StopSound("Play_Wind");
        }

        if (isGrounded)
        {
            // if the player has just landed this frame, reset the launching numbers
            if (isFlying)
            {
                AudioManager.Instance.PlaySound("Play_Player_Landing", transform);
                AudioManager.Instance.SetMusicState("Grounded");
                lastGroundedPosition = transform.position;
                
                Quaternion targetRotation = Quaternion.identity;
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * currentMoveSpeed);
                
                StartCoroutine(flightUI.ResetSliders());
            }
            isJumping = false;
            isFlying = false;
            isLaunching = false;
            flightDownwardForce = minFlightDownwardForce;
        }

        // perform jump
        if (isGrounded && !isJumping && !isFlying && !isLaunching && Input.GetKey(KeyCode.Space))
        {
            Jump();
        }
    }

    private void Jump()
    {
        AudioManager.Instance.PlaySound("Play_Player_Jump", transform);
        rb.velocity = Vector3.zero;
        rb.AddForce(transform.up * jumpForce * 2f, ForceMode.Impulse);
        flightBoostCooldownTimer = flightBoostCooldown;
        isJumping = true;
        isGrounded = false;
    }

    public void IncreaseNumFlightBoosts()
    {
        maxNumFlightBoosts++;
        currentFlightBoosts = maxNumFlightBoosts;
        
        StartCoroutine(flightUI.ResetSliders());
    }

    private void CheckBounds()
    {
        if (transform.position.y <= outOfBoundsObject.transform.position.y)
        {
            Respawn();
        }
    }

    private void Respawn()
    {
        TelemetryManager.instance.AccumulateMetric(fallenMetric, 1);
        transform.position = lastGroundedPosition;
    }
    
    private void CheckFlight()
    {
        if (flightBoostCooldownTimer > 0.0f)
        {
            flightBoostCooldownTimer -= Time.deltaTime;
        }
        else
        {
            flightBoostCooldownTimer = 0.0f;
        }
        
        if (!isGrounded && !isLaunching && (flightBoostCooldownTimer <= 0.0f) && (currentFlightBoosts > 0) && Input.GetKey(KeyCode.Space))
        {
            isJumping = false;
            rb.useGravity = false;

            if (rb.velocity.y < 0f)
            {
                Vector3 resetVelocity = new Vector3(rb.velocity.x, 1.0f, rb.velocity.z);
                rb.velocity = resetVelocity;
            }
            flightBoostCooldownTimer = flightBoostCooldown;
            currentFlightBoosts--;
            AudioManager.Instance.SetMusicState("Flying");
            if (!AudioManager.Instance.CheckSoundID("Play_Wind"))
            {
                AudioManager.Instance.PlaySound("Play_Wind", transform);
            }
            if (!isLaunching)
            {
                StartCoroutine(FlightBoost());
                StartCoroutine(flightUI.SlideSlider(0.0f, currentFlightBoosts));
            }
        }
        
        foreach (var particle in flightParticles)
        {
            if (!isGrounded)
            {
                particle.Play();
            }
            else
            {
                particle.Stop();
            }
        }
    }

    private IEnumerator FlightBoost()
    {
        AudioManager.Instance.PlaySound("Play_Player_FlightBoost", transform);
        isLaunching = true;
        float timeElapsed = 0.0f;
        float currentForce = 0.0f;
        flightBoostParticles.Play();
        
        // apply vertical rigidbody velocity over a short time to boost the player high into the air
        while (timeElapsed < flightBoostTime)
        {
            currentForce = Mathf.Lerp(0, flightBoostForce, timeElapsed / flightBoostTime);
            rb.AddForce(Vector3.up * currentForce, ForceMode.Acceleration);

            if (rb.velocity.y > flightBoostForce)
            {
                rb.velocity = new Vector3(rb.velocity.x, flightBoostForce, rb.velocity.z);
            }
            
            timeElapsed += Time.deltaTime;
            yield return null;
        }

        yield return new WaitForSeconds(0.5f);
        isLaunching = false;
        isFlying = true;
        rb.velocity = new Vector3(rb.velocity.x, 3f, rb.velocity.z);
        flightBoostParticles.Stop();
    }

    private void ApplyFlightForces()
    {
        if (isLaunching || isGrounded) return;
        
        Vector3 downwardForce = Vector3.zero;
        
        if (flightDownwardForce < maxFlightDownwardForce)
        {
            currentDownwardForceSpeed += downwardForceAcceleration * Time.deltaTime;
            flightDownwardForce += currentDownwardForceSpeed * Time.deltaTime;
        }
        downwardForce = (Vector3.up * -flightDownwardForce);
        rb.AddForce(downwardForce);
    }

    public void SetVerticalVelocity(float verticalVelocity)
    {
        rb.velocity = new Vector3(rb.velocity.x, verticalVelocity, rb.velocity.z);
    }

    private void HandleRotation(Vector3 moveDirection)
    {
        if (isAiming) return;
        
        float rotationAngleX = 0f;
        float rotationAngleZ = 0f;
        
        if (isFlying || isLaunching)
        {
            // rotate player towards the direction input
            rotationAngleZ = horizontalInput * -30.0f;
            
            // if the player is looking downward/upward enough, apply rotation
            if (mainCamera.transform.forward.y < -0.5f)
            {
                rotationAngleX = 130.0f;
            }
            else if (mainCamera.transform.forward.y > 0.2f)
            {
                rotationAngleX = 30.0f;
            }
            // default rotation for flying
            else
            {
                rotationAngleX = 60.0f;
            }
        }

        if (isFlying || moveDirection != Vector3.zero)
        {
            float targetAngle = Mathf.Atan2(moveDirection.x, moveDirection.z) * Mathf.Rad2Deg;
            Quaternion targetRotation = Quaternion.Euler(rotationAngleX, targetAngle, rotationAngleZ);
            transform.rotation =
                Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * currentMoveSpeed);
        }

        // Wwise audio: send rotation value to wind RTPC
        if (isFlying || isLaunching)
        {
            float rtpcVal = ((transform.localEulerAngles.x - 30f) / (50f - 30f)) * 100f;
            Mathf.Clamp(rtpcVal, 0f, 100f);
            AudioManager.Instance.SetEffectsRTPC("Flight_Speed", rtpcVal);
        }
    }

    private void MoveCharacter()
    {
        Vector3 moveDirection;
        
        // flight-based movement:
        if ((isFlying || isLaunching) && !isAiming)
        {
            // if the player is looking downward enough, apply downward force
            if (mainCamera.transform.forward.y < -0.5f)
            {
                if (rb.velocity.y > 0)
                {
                    SetVerticalVelocity(-0.1f);
                }
                Vector3 downwardForce = new Vector3(0f, downwardForceAcceleration, 0f);
                rb.AddForce(-downwardForce);
            }
            // if the camera is looking up enough, set the downward forces to a minimum
            else if (math.dot(mainCamera.transform.forward, Vector3.up) > 0.2f && rb.velocity.y < 0f)
            {
                SetVerticalVelocity(minFlightDownwardForce * -1f);
            }
            
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

            if (moveDirection != Vector3.zero)
            {
                if (currentMoveSpeed < maxMoveSpeed)
                {
                    currentMoveSpeed += accelerationSpeed * Time.deltaTime;
                }

                Vector3 newVel = (moveDirection * currentMoveSpeed);
                rb.velocity = new Vector3(newVel.x, rb.velocity.y, newVel.z);
            }
            else
            {
                rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
            }
        }
        HandleRotation(moveDirection);
    }
} 