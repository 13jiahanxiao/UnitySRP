using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InstanceColor : MonoBehaviour
{
    static int colorID = Shader.PropertyToID("_Color");
    static MaterialPropertyBlock propertyBlock;

    [SerializeField]
    Color color = Color.white;
 
    private void Awake()
    {
        OnValidate();
    }
    private void OnValidate()
    {
        if (propertyBlock == null)
        {
            propertyBlock = new MaterialPropertyBlock();
        }
        propertyBlock.SetColor(colorID, color);
        GetComponent<MeshRenderer>().SetPropertyBlock(propertyBlock);
    }
}
