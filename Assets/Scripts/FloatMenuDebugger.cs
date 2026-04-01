using UnityEngine;
using UnityEngine.UI;

namespace SpatialDrawing
{
    public class FloatMenuDebugger : MonoBehaviour
    {
        void Update()
        {
            if (UnityEngine.InputSystem.Keyboard.current != null &&
                UnityEngine.InputSystem.Keyboard.current.pKey.wasPressedThisFrame)
            {
                Debug.Log("--- P KEY PRESSED: SCANNING SCENE FOR GENERATED CANVAS ---");
                
                Canvas[] allCanvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                
                bool found = false;
                foreach (Canvas c in allCanvases)
                {
                    if (c.name.Contains("FloatingMenuCanvas_Runtime"))
                    {
                        found = true;
                        Debug.Log($"FOUND CANVAS: {c.name}");
                        Debug.Log($" - Active: {c.gameObject.activeInHierarchy}");
                        Debug.Log($" - Position: {c.transform.position}");
                        Debug.Log($" - Scale: {c.transform.localScale}");
                        Debug.Log($" - Render Mode: {c.renderMode}");
                        Debug.Log($" - Event Camera: {(c.worldCamera != null ? c.worldCamera.name : "NULL")}");
                        
                        CanvasGroup cg = c.GetComponent<CanvasGroup>();
                        Debug.Log($" - CanvasGroup Alpha: {(cg != null ? cg.alpha.ToString() : "Missing")}");

                        RectTransform container = c.transform.Find("ItemContainer") as RectTransform;
                        if (container != null)
                        {
                            Debug.Log($" - Container Active: {container.gameObject.activeInHierarchy}");
                            Debug.Log($" - Children Count: {container.childCount}");
                            for (int i=0; i<container.childCount; i++)
                            {
                                Transform child = container.GetChild(i);
                                Image img = child.GetComponent<Image>();
                                Debug.Log($"   - Detail: Child {i} ({child.name}) Active:{child.gameObject.activeInHierarchy} Pos:{child.localPosition} Scale:{child.localScale} Color:{img?.color}");
                            }
                        }
                        else
                        {
                            Debug.Log(" - CRITICAL: 'ItemContainer' not found under Canvas!");
                        }
                    }
                }
                
                if (!found)
                {
                    Debug.Log("CRITICAL: No canvas named 'FloatingMenuCanvas_Runtime' exists in the entire scene! BuildCanvasHierarchy() likely never ran.");
                }
                Debug.Log("----------------------------------------------------------");
            }
        }
    }
}
