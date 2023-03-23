using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
public class BaseNObjectMember : MonoBehaviour
{
    public int allocatedIndex = -1; 
    public int memberID = -1; //Index in the memberDataArray
    public BoxCollider b_collider;
    //public NavMeshObstacle obstacle; Integration with Navmesh cuts for procedural Ai obstacle streaming
    public void InitializeMember(Matrix4x4 matrix, int memberID, int allocatedIndex, NObjectRuntimeData runtimeData)
    {
        gameObject.SetActive(true);
        this.memberID = memberID;
        this.allocatedIndex = allocatedIndex;
        transform.position = matrix.GetColumn(3);
        transform.localScale = new Vector3(
                            matrix.GetColumn(0).magnitude,
                            matrix.GetColumn(1).magnitude,
                            matrix.GetColumn(2).magnitude
                            );
        transform.rotation = Quaternion.LookRotation(matrix.GetColumn(2), matrix.GetColumn(1));
        b_collider.center = runtimeData.center;
        b_collider.size = runtimeData.size;
    }

    public void DeAllocateMember()
    {
        memberID = -1;
        allocatedIndex = -1;
        gameObject.SetActive(false);
    }

    public void DestroyMember()
    {
        Destroy(gameObject);
    }
    public bool IsAllocated()
    {
        return memberID != -1;
    }

    //Interaction function with the database (trees)
    public void Damage(float damage, GameObject instigator, BaseDamageType type)
    {
        BaseNObjectManager.Instance.DamageMember(memberID, damage, instigator, type);
    }
}

