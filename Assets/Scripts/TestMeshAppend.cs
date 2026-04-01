using UnityEngine;
using System.Collections.Generic;

public class TestMeshAppend : MonoBehaviour
{
    void Start()
    {
        Mesh mesh = new Mesh();
        List<Vector3> verts = new List<Vector3> { 
            new Vector3(0,0,0), new Vector3(1,0,0), 
            new Vector3(0,1,0), new Vector3(1,1,0) 
        };

        // Pass 1: Set first 2
        mesh.SetVertices(verts, 0, 2);
        Debug.Log($"Pass 1: vertexCount = {mesh.vertexCount}");

        // Pass 2: Set next 2
        mesh.SetVertices(verts, 2, 2);
        Debug.Log($"Pass 2: vertexCount = {mesh.vertexCount}");
    }
}
