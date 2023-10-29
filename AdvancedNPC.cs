using UdonSharp;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

public class AdvancedNPC : UdonSharpBehaviour
{
    [Header("Player's Weapons Reference")]
    public GameObject[] playerWeapons; // Assign the player's weapons in the editor.

    [Header("Health Properties")]
    public Slider healthBar;
    public Animator npcAnimator;
    public float maxHealth = 100f;
    private float currentHealth;

    [Header("Respawn Properties")]
    public float respawnTime = 180f; // 3 minutes respawn time
    private Vector3 initialPosition;
    private Quaternion initialRotation;
    private bool isRespawning = false;
    private float respawnTimer;

    [Header("Wandering Properties")]
    public float wanderRadius;
    public float wanderTimer;
    private float timer;

    [Header("Combat Properties")]
    public float damagePerSecond = 10f;
    private bool isEngaged = false;
    private float disengageTime = 10f;
    private float disengageTimer;

    [Header("Death Properties")]
    public GameObject deathDrop; // Object to spawn upon death.
    private bool isDead = false;
    private bool startDisappearTimer = false;
    private float disappearTimer = 0f;
    private float timeToDisappear = 3f; // should match your death animation duration

    [Header("Attack Properties")]
    public float attackInterval = 3f; // Time between attacks.
    private float attackTimer;



    private NavMeshAgent agent;
    private UdonBehaviour udonBehaviour;

    // Synchronized health across clients. The master client updates this value, and other clients read from it.
    [UdonSynced(UdonSyncMode.None)]
    private float syncedHealth;

    [UdonSynced(UdonSyncMode.Smooth)] // This will smoothly interpolate the NPC's position between updates.
    private Vector3 syncedPosition;

    [UdonSynced(UdonSyncMode.Smooth)] // This will synchronize rotation.
    private Quaternion syncedRotation;

    [UdonSynced(UdonSyncMode.None)] // This will sync the 'isDead' status across clients. We do not need interpolation for boolean values, so we use UdonSyncMode.None.
    private bool isDeadSynced;

    private bool isMaster; // Flag to check if this client is the master client.

    void Start()
    {
        udonBehaviour = (UdonBehaviour)GetComponent(typeof(UdonBehaviour));
        currentHealth = maxHealth;
        healthBar.maxValue = maxHealth;
        healthBar.value = currentHealth;

        disengageTimer = disengageTime;

        agent = GetComponent<NavMeshAgent>();
        npcAnimator = GetComponentInChildren<Animator>();

        timer = wanderTimer;
        attackTimer = attackInterval; // Initialize the attack timer.
        RandomWander();
        
        // Store the initial spawn location and rotation.
        initialPosition = transform.position;
        initialRotation = transform.rotation;
        // Initial sync check
        isMaster = Networking.IsMaster; // Check if the current player is the master client.
        if (!isMaster)
        {
            currentHealth = syncedHealth; // Non-master clients should sync their health value with the master client.
        }
        // Additional setup for the synced position.
        if (Networking.IsOwner(gameObject))
        {
            syncedPosition = transform.position; // Initialize with the current position.
            syncedRotation = transform.rotation; // Initialize with the current rotation.

            isDeadSynced = isDead; // Initialize with the current state.
        }

    }

    void Update()
    {
        // Check for master client change and handle NPC authority handover.
        if (Networking.IsMaster != isMaster)
        {
            isMaster = Networking.IsMaster;
            OnMasterClientSwitched(); // Handle the necessary logic when master client changes.
        }

        // Handle position synchronization.
        if (Networking.IsOwner(gameObject))
        {
            // The master client (owner) updates the synced position and rotation.
            syncedPosition = transform.position;
            syncedRotation = transform.rotation;

            // The master client (owner) updates the synced 'isDead' state.
            isDeadSynced = isDead;
        }
        else
        {
            // Non-master clients update their NPC's position based on the synced data.
            transform.position = Vector3.Lerp(transform.position, syncedPosition, Time.deltaTime * 10f); // Smooth transition to the synced position.
            transform.rotation = Quaternion.Lerp(transform.rotation, syncedRotation, Time.deltaTime * 10f); // Smoothly update the rotation for non-owners.
            // Non-master clients update their 'isDead' state based on the synced data.
            isDead = isDeadSynced; // Directly set the value as it's a boolean.
        }

        if (isDead||isDeadSynced) return; // If NPC is dead, no further logic should execute.

        timer += Time.deltaTime;
        attackTimer += Time.deltaTime; // Increment the attack timer.

        // Update the animation state based on the NPC movement.
        npcAnimator.SetBool("Move", agent.velocity != Vector3.zero);

        // Wandering logic.
        if (timer >= wanderTimer && !isEngaged)
        {
            Vector3 newPos = RandomNavSphere(transform.position, wanderRadius, -1);
            agent.SetDestination(newPos);
            timer = 0;
        }

        // Disengagement logic.
        if (isEngaged)
        {
            disengageTimer -= Time.deltaTime;
            if (disengageTimer <= 0)
            {
                Disengage();
            }
        }

        // Only the master client can modify NPC health, ensuring it's authoritative.
        if (isMaster)
        {
            // Any logic here is executed only by the master client, such as updating NPC health.
            syncedHealth = currentHealth; // Keep the synced health updated on the master client side.
        }
        else
        {
            // For non-master clients, always sync the health value.
            currentHealth = syncedHealth; // This ensures the health value is consistent across all clients.
            healthBar.value = currentHealth; // Update the health bar display based on the synced health.
        }

        // Attack logic.
        if (isEngaged && attackTimer >= attackInterval)
        {
            npcAnimator.SetTrigger("Attack"); // Assume you have an "Attack" trigger set up in your animator.
            // Here you would put any logic related to the NPC dealing damage to the player.
            attackTimer = 0f; // Reset attack timer after attacking.
        }

        // Handle death and item drop
        if (startDisappearTimer)
        {
            disappearTimer += Time.deltaTime;
            if (disappearTimer >= timeToDisappear)
            {
                if (deathDrop != null && Networking.LocalPlayer.isMaster)
                {
                    VRCInstantiate(deathDrop);
                }
                startDisappearTimer = false;
                Networking.Destroy(gameObject);
            }
        }
        // Respawn logic
        if (isDead && !isRespawning)
        {
            isRespawning = true;
            respawnTimer = respawnTime;
        }

        if (isRespawning)
        {
            respawnTimer -= Time.deltaTime;
            if (respawnTimer <= 0)
            {
                RespawnNPC();
            }
        }

    }
    // This method is called when the master client is switched.
    private void OnMasterClientSwitched()
    {
        if (isMaster)
        {
            // If this client is the new master client, it might need to initialize some data or state.
            // This could also involve taking over certain responsibilities from the previous master client.
            // For instance, if the NPC was in the middle of a specific action or behavior, the new master
            // client might need to continue those actions seamlessly.
        }
        else
        {
            // For non-master clients, you might need to perform some clean-up or state adjustment.
            // This is less common but depends on your specific game's requirements.
        }
    }


    void OnTriggerEnter(Collider other)
    {
        // Check if the collided object is one of the player's weapons
        foreach (GameObject weapon in playerWeapons)
        {
            if (other.gameObject == weapon)
            {
                TakeDamage(damagePerSecond);
                // Trigger a random damage animation
                string damageTrigger = Random.Range(0, 2) == 0 ? "TookDamageA" : "TookDamageB";
                npcAnimator.SetTrigger(damageTrigger);

                if (!isEngaged)
                {
                    Engage();
                }
                else
                {
                    disengageTimer = disengageTime; // Reset the timer if re-engaged in combat
                }
            }
        }
    }

    private Vector3 RandomNavSphere(Vector3 origin, float dist, int layermask)
    {
        Vector3 randDirection = Random.insideUnitSphere * dist;
        randDirection += origin;

        NavMeshHit navHit;
        NavMesh.SamplePosition(randDirection, out navHit, dist, layermask);

        return navHit.position;
    }

    private void RandomWander()
    {
        Vector3 newPos = RandomNavSphere(transform.position, wanderRadius, -1);
        agent.SetDestination(newPos);
    }

    public void TakeDamage(float damageAmount)
    {
        if (!isDead)
        {
            currentHealth -= damageAmount;
            healthBar.value = currentHealth;

            if (currentHealth <= 0)
            {
                Die();
            }
        }
    }

    private void Engage()
    {
        isEngaged = true;
        agent.isStopped = true;
    }

    private void Disengage()
    {
        isEngaged = false;
        agent.isStopped = false;
        RandomWander();
        disengageTimer = disengageTime; // Reset disengagement timer.
    }

    private void Attack()
    {
        // Randomly choose between the two attack animations
        string attackTrigger = Random.Range(0, 2) == 0 ? "AttackA" : "AttackB";
        npcAnimator.SetTrigger(attackTrigger);
    }

    public void RespawnNPC()
    {
        if (Networking.LocalPlayer.isMaster) // Ensure only the master client can respawn the NPC to avoid conflicts.
        {
            udonBehaviour.SendCustomNetworkEvent(NetworkEventTarget.All, "ResetNPC"); // Calls the ResetNPC method on all clients.
        }
    }

    public void ResetNPC()
    {
        // Reset health.
        currentHealth = maxHealth;
        healthBar.value = currentHealth;

        // Reset position.
        transform.position = initialPosition;
        transform.rotation = initialRotation;

        // Reset animation state.
        npcAnimator.Rebind();

        // Reset AI.
        agent.ResetPath();
        isDead = false;
        isRespawning = false;
        isEngaged = false;
        RandomWander();
    }

    private void Die()
    {
        if (!isDead) // Check to prevent re-triggering death.
        {
            isDead = true;

            // Trigger a random death animation
            string deathTrigger = Random.Range(0, 2) == 0 ? "DieA" : "DieB";
            npcAnimator.SetTrigger(deathTrigger);
            agent.isStopped = true;
            startDisappearTimer = true; // Initiate the disappearance sequence.

            // Drop item on death.
            if (deathDrop != null && Networking.LocalPlayer.isMaster)
            {
                // Correct method to instantiate an object in Udon.
                var spawnedObject = VRCInstantiate(deathDrop);
                spawnedObject.transform.position = transform.position;
                spawnedObject.transform.rotation = transform.rotation;
            }
            // Additional die logic for respawn:
            isRespawning = true;

            // Ensure the death animation is triggered across all clients.
            SendCustomNetworkEvent(NetworkEventTarget.All, "TriggerDeathAnimation");
        }
    }

    public void TriggerDeathAnimation()
    {
        // This method should play the death animation and is called on all clients.
        string deathTrigger = Random.Range(0, 2) == 0 ? "DieA" : "DieB";
        npcAnimator.SetTrigger(deathTrigger);
        agent.isStopped = true;
        // ... any other necessary cleanup or state changes.
    }

    public override void OnDeserialization() // This is called after the variable synced through the network is updated.
    {
        if (!isMaster)
        {
            // If this client is not the master, it should accept the synced health value from the master client.
            currentHealth = syncedHealth;
            healthBar.value = currentHealth; // Reflect the changes in the UI as well.

            // Trigger the death animation if the NPC's health drops to zero or below.
            if (currentHealth <= 0 && !isDead)
            {
                Die(); // This will handle the death animation and state.
            }
            // Here, you might also want to handle other visual or state updates that depend on the health value.
            // For example, if the NPC has different visual states depending on its health, update those states here.
        }
        if (!Networking.IsOwner(gameObject))
        {
            // Update the position when the synced variable changes. This helps with sudden changes or corrections in position.
            transform.position = syncedPosition;
            transform.rotation = syncedRotation; // Update the rotation when the synced variable changes.

            // Update the 'isDead' state when the synced variable changes.
            isDead = isDeadSynced; // This helps maintain state consistency across clients.
        }
        // If the NPC's death status has changed, reflect it on all clients.
        if (isDeadSynced != isDead)
        {
            isDead = isDeadSynced;

            // If the NPC is now dead, trigger the death sequence/animations.
            if (isDead)
            {
                Die(); // Handles death animation and state. Ensure that Die() is suitable for calling multiple times.
            }
        }
    }

}
