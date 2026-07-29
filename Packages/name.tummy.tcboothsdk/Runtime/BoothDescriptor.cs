using UnityEngine;

[DisallowMultipleComponent]
public class BoothDescriptor : MonoBehaviour
{
    [Tooltip("The display name of the virtual booth.")]
    public string boothName = "My cool booth!";
    
    [Tooltip("The name of the creator.")]
    public string creatorName = "VRChat username";
}