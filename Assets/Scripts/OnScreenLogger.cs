using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

namespace SpatialDrawing
{
    /// <summary>
    /// Attach this to any active GameObject (e.g. Main Camera).
    /// Instantly creates an on-screen text box showing the last 15 Unity Debug logs.
    /// Perfect for capturing screenshots of device errors or missing UI logs.
    /// </summary>
    public class OnScreenLogger : MonoBehaviour
    {
        private Text _debugText;
        private Canvas _debugCanvas;
        private Queue<string> _logQueue = new Queue<string>();
        private const int MAX_LINES = 15;

        void Awake()
        {
            return; // ── DISCONNECT DEBUG LOGS ──
            // Build the UI exactly as requested: an overlay canvas
            GameObject canvasGo = new GameObject("ScreenLoggerCanvas");
            _debugCanvas = canvasGo.AddComponent<Canvas>();
            _debugCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _debugCanvas.sortingOrder = 999; // Draw over EVERYTHING
            
            canvasGo.AddComponent<CanvasScaler>();

            // Dark background panel
            GameObject bgGo = new GameObject("BgPanel");
            bgGo.transform.SetParent(canvasGo.transform, false);
            Image bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0, 0, 0, 0.7f); // Semi-transparent black
            RectTransform bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0, 0.6f); // Top 40% of screen
            bgRt.anchorMax = new Vector2(1, 1f);
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;

            // Text element
            GameObject txtGo = new GameObject("LogText");
            txtGo.transform.SetParent(bgGo.transform, false);
            _debugText = txtGo.AddComponent<Text>();
            _debugText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _debugText.fontSize = 24;
            _debugText.color = Color.green; // Hacker green for visibility
            _debugText.alignment = TextAnchor.UpperLeft;
            _debugText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _debugText.verticalOverflow = VerticalWrapMode.Truncate;
            
            RectTransform txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(20, 20); // 20px padding
            txtRt.offsetMax = new Vector2(-20, -20);
            
            // Subscribe to Unity's log event
            Application.logMessageReceived += HandleLog;
            
            Debug.Log("[OnScreenLogger] Initialized. Ready to catch errors.");
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= HandleLog;
        }

        private void HandleLog(string logString, string stackTrace, LogType type)
        {
            string color = type == LogType.Error || type == LogType.Exception ? "red" : 
                           type == LogType.Warning ? "yellow" : "white";

            string formattedMsg = $"<color={color}>{logString}</color>";
            
            _logQueue.Enqueue(formattedMsg);
            if (_logQueue.Count > MAX_LINES)
            {
                _logQueue.Dequeue();
            }

            if (_debugText != null)
            {
                _debugText.text = string.Join("\n", _logQueue);
            }
        }

        void Update()
        {
            // Optional: Press L to toggle the log visibility
            if (UnityEngine.InputSystem.Keyboard.current != null && 
                UnityEngine.InputSystem.Keyboard.current.lKey.wasPressedThisFrame)
            {
                _debugCanvas.enabled = !_debugCanvas.enabled;
            }
            
            // If the user presses P (the FloatMenuDebugger key), simulate a button press 
            // incase they haven't attached FloatMenuDebugger yet.
            if (UnityEngine.InputSystem.Keyboard.current != null && 
                UnityEngine.InputSystem.Keyboard.current.pKey.wasPressedThisFrame)
            {
               RunEmergencyCanvasScan();
            }
        }
        
        // This is a copy of the FloatMenuDebugger logic built right into the logger
        private void RunEmergencyCanvasScan()
        {
            Debug.Log("--- ROOT CAUSE PROBE (P KEY) ---");
            Canvas[] allCanvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            bool found = false;
            foreach (Canvas c in allCanvases)
            {
                if (c.name.Contains("FloatingMenuCanvas"))
                {
                    found = true;
                    CanvasGroup cg = c.GetComponent<CanvasGroup>();
                    Debug.Log($"[FOUND] {c.name}");
                    Debug.Log($"RenderMode: {c.renderMode} | Layer: {LayerMask.LayerToName(c.gameObject.layer)}");
                    Debug.Log($"Alpha: {(cg != null ? cg.alpha.ToString("F3") : "N/A")} | Scale: {c.transform.localScale.x:F6}");
                    Debug.Log($"Rotation: {c.transform.eulerAngles}");
                    
                    RectTransform container = c.transform.Find("ItemContainer") as RectTransform;
                    if (container != null && container.childCount > 0)
                    {
                        Transform firstItem = container.GetChild(0);
                        Transform iconChild = firstItem.Find("Icon");
                        
                        var bgImg = firstItem.GetComponent<UnityEngine.UI.Image>();
                        var iconImg = iconChild != null ? iconChild.GetComponent<UnityEngine.UI.Image>() : null;
                        
                        Debug.Log($"BG Color: {(bgImg != null ? bgImg.color.ToString() : "NULL")}");
                        if (iconImg != null)
                        {
                            Debug.Log($"Icon: {iconImg.sprite?.name ?? "NO SPRITE"} | Shader: {iconImg.material?.shader?.name ?? "NULL"}");
                            Debug.Log($"Icon Color: {iconImg.color}");
                        }
                        else Debug.Log("<color=red>No Icon Child found</color>");
                    }
                    else
                    {
                        Debug.Log("<color=red>No items found in Container</color>");
                    }
                }
            }
            if (!found) Debug.Log("<color=red>No FloatingMenuCanvas generated!</color>");
        }
    }
}
