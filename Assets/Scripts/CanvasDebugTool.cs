using UnityEngine;
using UnityEngine.UI;

namespace SpatialDrawing
{
    public class CanvasDebugTool : MonoBehaviour
    {
        public Canvas arcCanvas;

        void Update()
        {
            if (UnityEngine.InputSystem.Keyboard.current != null &&
                UnityEngine.InputSystem.Keyboard.current.oKey.wasPressedThisFrame)
            {
                Debug.Log("--- Canvas Debug (O Key) ---");
                if (arcCanvas == null) {
                    Debug.Log("arcCanvas reference is NULL");
                    return;
                }
                
                Debug.Log($"Canvas Active: {arcCanvas.gameObject.activeInHierarchy}");
                Debug.Log($"Canvas Scale: {arcCanvas.transform.localScale}");
                Debug.Log($"Canvas Position: {arcCanvas.transform.position}");
                Debug.Log($"Canvas World Camera: {arcCanvas.worldCamera?.name ?? "NULL"}");
                
                CanvasGroup cg = arcCanvas.GetComponent<CanvasGroup>();
                Debug.Log($"CanvasGroup Alpha: {(cg != null ? cg.alpha.ToString() : "No Component")}");
                
                Debug.Log($"Child Count: {arcCanvas.transform.childCount}");
                for (int i=0; i<arcCanvas.transform.childCount; i++) {
                    var child = arcCanvas.transform.GetChild(i);
                    var img = child.GetComponent<Image>();
                    Debug.Log($"- Child {i}: {child.name} | Active: {child.gameObject.activeInHierarchy} | Pos: {child.localPosition} | Scale: {child.localScale} | Img Sprite: {img?.sprite?.name ?? "None"}");
                }
            }
        }
    }
}
