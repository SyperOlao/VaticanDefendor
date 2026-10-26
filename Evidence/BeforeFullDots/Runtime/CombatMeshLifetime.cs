using UnityEngine;
public class CombatMeshLifetime : MonoBehaviour
{
    private void OnDestroy()
    {
        MeshFilter Filter=GetComponent<MeshFilter>();
        if(Filter!=null && Filter.sharedMesh!=null)
        {
            Destroy(Filter.sharedMesh);
        }
    }
}
