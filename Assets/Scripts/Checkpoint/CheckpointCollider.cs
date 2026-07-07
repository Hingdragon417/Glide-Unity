using UnityEngine;

public class CheckpointCollider : MonoBehaviour
{

    void OnTriggerEnter(Collider other)
    {
        TryActivateCheckpoint(other);
    }

    void OnCollisionEnter(Collision collision)
    {
        TryActivateCheckpoint(collision.collider);
    }

    void TryActivateCheckpoint(Collider other)
    {
        PlayerMovement player = other.GetComponentInParent<PlayerMovement>();

        if (player != null)
        {
            player.ActivateCheckpoint(transform.position);
            Destroy(gameObject);
        }
    }

}
