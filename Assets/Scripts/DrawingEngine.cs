using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using SpatialDrawing.UI;

namespace SpatialDrawing
{
    /// <summary>
    /// Core drawing engine. Creates 3D lines in AR space based on
    /// fingertip position from the HandTrackingManager.
    /// Supports undo/redo and connects to the UI for color/brush selection.
    /// </summary>
    public class DrawingEngine : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private HandTrackingManager handTracker;
        [SerializeField] private ArcMenuController arcMenu;

        [Header("Drawing Settings")]
        [SerializeField] private Material lineMaterial;

        // ── State ──
        private bool _isDrawing;
        private DrawingLine _currentLine;
        private List<DrawingLine> _lines = new();
        private Stack<DrawingLine> _undoStack = new();

        private Color _currentColor = Color.white;
        private float _currentBrushSize = 0.005f;
        private bool _isEraserMode;

        [Header("Eraser Settings")]
        [Tooltip("Erase radius in screen pixels relative to screen width (e.g., 0.05 = 5% of screen width)")]
        [SerializeField] private float _eraseRadiusScreenPct = 0.08f;
        private GameObject _eraserVisual;

        // FIX #6: Cooldown after menu close to prevent accidental drawing
        private float _menuCloseCooldown;
        private const float MENU_CLOSE_COOLDOWN = 0.3f;

        // ── Cache ──
        private Camera _mainCamera;

        void Start()
        {
            _mainCamera = Camera.main;

            // Find references if not assigned
            if (handTracker == null)
                handTracker = FindAnyObjectByType<HandTrackingManager>();
            if (arcMenu == null)
                arcMenu = FindAnyObjectByType<ArcMenuController>();

            // Create default line material if not assigned
            if (lineMaterial == null)
            {
                lineMaterial = new Material(Shader.Find("Sprites/Default"));
                lineMaterial.color = Color.white;
            }

            // Subscribe to hand tracking events
            if (handTracker != null)
            {
                handTracker.OnFingertipMoved += OnFingertipMoved;
                handTracker.OnHandLost += OnHandLost;
            }

            // Subscribe to Arc Menu events
            if (arcMenu != null)
            {
                // Init with menu defaults
                _currentColor = arcMenu.GetCurrentColor();
                _currentBrushSize = arcMenu.GetCurrentBrushSize();

                arcMenu.OnColorChanged += OnColorChanged;
                arcMenu.OnBrushSizeChanged += OnBrushSizeChanged;
                arcMenu.OnEraserToggled += OnEraserToggled;
                arcMenu.OnClearCanvas += ClearCanvas;
                arcMenu.OnMenuStateChanged += OnMenuStateChanged;
                arcMenu.OnSavePressed += Save;
            }

            // Create High-Performance Eraser Visual (No Physics)
            // Visually, it will act as a 2D screen reticle. We achieve this by placing a sphere
            // very close to the camera (e.g. 0.1m away) so it appears flat. 
            if (_eraserVisual == null)
            {
                _eraserVisual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(_eraserVisual.GetComponent<Collider>()); // Essential: Removes all physics overhead
                
                var material = new Material(Shader.Find("UI/Default"));
                material.color = new Color(0.6f, 0.85f, 1.0f, 0.4f); // Semi-transparent Premium Light Blue
                _eraserVisual.GetComponent<MeshRenderer>().material = material;
                
                _eraserVisual.SetActive(false);
            }
        }

        void Update()
        {
            if (handTracker == null) return;

            // ── Suspend Drawing during Full-Screen UI ──
            if (handTracker.IsUIBlocking)
            {
                if (_isDrawing) FinishLine();
                if (_eraserVisual != null && _eraserVisual.activeSelf) _eraserVisual.SetActive(false);
                return;
            }

        // Don't draw or erase if the arc menu is open
        if (arcMenu != null && arcMenu.IsOpen)
        {
            if (_isDrawing) FinishLine();
            if (_eraserVisual != null && _eraserVisual.activeSelf) _eraserVisual.SetActive(false);
            return;
        }

        // FIX #6: Don't draw during post-close cooldown
        // This prevents accidental strokes from residual pinch gestures
        // that were in-progress when the menu closed.
        if (_menuCloseCooldown > 0f)
        {
            _menuCloseCooldown -= Time.deltaTime;
            if (_isDrawing) FinishLine();
            if (_eraserVisual != null && _eraserVisual.activeSelf) _eraserVisual.SetActive(false);
            return;
        }

            // Manage Eraser Visual (Screen-Space Wipe reticle)
            if (_isEraserMode && handTracker.IsHandDetected && handTracker.IsPinching)
            {
                if (_eraserVisual != null && _mainCamera != null)
                {
                    _eraserVisual.SetActive(true);
                    
                    // We need the 2D pixel position of the pinch
                    Vector2 thumbScreen = handTracker.FingertipScreenPositions[0];
                    Vector2 indexScreen = handTracker.FingertipScreenPositions[1];
                    Vector2 pinchScreenPos = (thumbScreen + indexScreen) / 2f;
                    
                    // Project the visual a fixed, short distance (0.1m) in front of the camera
                    // so it looks like a flat 2D sticker on the glass.
                    float visualDist = 0.1f;
                    Vector3 targetPos = _mainCamera.ScreenToWorldPoint(new Vector3(pinchScreenPos.x, pinchScreenPos.y, visualDist));
                    
                    // Smooth the eraser visual to eliminate jitter from raw frame-by-frame projection
                    if ((_eraserVisual.transform.position - targetPos).sqrMagnitude > 0.01f)
                        _eraserVisual.transform.position = targetPos; // Snap if too far (e.g. just spawned)
                    else
                        _eraserVisual.transform.position = Vector3.Lerp(_eraserVisual.transform.position, targetPos, Time.deltaTime * 20f);
                    
                    // Scale it so that the physical size on screen tightly matches the pixel radius we use for erasing.
                    // Field of view math: height of viewing plane at distance D = 2 * D * tan(FOV/2)
                    float frustumHeight = 2.0f * visualDist * Mathf.Tan(_mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    float frustumWidth = frustumHeight * _mainCamera.aspect;
                    
                    // Width of our eraser relative to the screen width
                    float targetWorldWidth = frustumWidth * (_eraseRadiusScreenPct * 2f);
                    _eraserVisual.transform.localScale = Vector3.one * targetWorldWidth;
                }
            }
        else
        {
            if (_eraserVisual != null && _eraserVisual.activeSelf) _eraserVisual.SetActive(false);
        }

            // Start/stop drawing or erasing based on pinch gesture
            if (handTracker.IsHandDetected && handTracker.IsPinching)
            {
                if (_isEraserMode)
                {
                    EraseNearestLine();
                }
                else if (!_isDrawing)
                {
                    StartNewLine();
                }

                // Live visual tip update at frame rate (60fps)
                if (_isDrawing && _currentLine != null)
                {
                // Use the last measured position for the elastic tail — NOT the velocity prediction.
                // Prediction extrapolates up to 100ms forward using velocity, which overshoots
                // badly during curved/circular motion (velocity direction rotates continuously).
                // The measured position has 1-frame natural latency but is completely stable.
                _currentLine.UpdateElasticTail(handTracker.FingertipWorldPosition);
                }
            }
            else
            {
                if (_isDrawing)
                {
                    FinishLine();
                }
            }
        }

        // ════════════════════════════════════
        //  DRAWING
        // ════════════════════════════════════
        private void StartNewLine()
        {
            _isDrawing = true;
            HapticService.Medium(); // Drawing started — pinch confirmed

            // ── TRUE STATIC WORLD SPACE SYSTEM ──
            var lineObj = new GameObject($"Drawing_{_lines.Count}");
            // No ARAnchor! Pure Unity World Space coordinates!
            
            _currentLine = lineObj.AddComponent<DrawingLine>();
            _currentLine.Initialize(_currentColor, _currentBrushSize, lineMaterial);

            // Clear redo stack when drawing new line
            foreach (var line in _undoStack)
            {
                if (line != null) Destroy(line.gameObject);
            }
            _undoStack.Clear();

#if UNITY_EDITOR
            Debug.Log("[Drawing] Started new drawing (Static World Space)");
#endif
        }

        private void OnFingertipMoved(Vector3 worldPos)
        {
            if (_isDrawing && _currentLine != null)
            {
                _currentLine.AddPoint(worldPos);
            }
        }

        private void FinishLine()
        {
            if (_currentLine != null && _currentLine.Points.Count > 1)
            {
                // ELASTIC TAIL COMMIT: Finalize the line so the elastic tail is committed
                // as a real point. This prevents visual snap-back on pinch release.
                _currentLine.FinalizeLine();

                // OPTIMIZATION (Bug #3 Fix): Pre-calculate the bounds once the line is physically done
                // so the Eraser script doesn't have to force Unity to do it on the fly.
                _currentLine.CalculateBounds();

                _lines.Add(_currentLine);
#if UNITY_EDITOR
                Debug.Log($"[Drawing] Finished line with {_currentLine.Points.Count} points");
#endif
            }
            else if (_currentLine != null)
            {
                // Too short, discard
                Destroy(_currentLine.gameObject);
            }

            _currentLine = null;
            _isDrawing = false;
        }

        private void OnHandLost()
        {
            if (_isDrawing) FinishLine();
        }

        // ════════════════════════════════════
        //  EXTERNAL INJECTION
        // ════════════════════════════════════
        public void InjectSummonedLine(DrawingLine line)
        {
            if (line != null && !_lines.Contains(line))
            {
                line.CalculateBounds();
                _lines.Add(line);
            }
        }

        // ════════════════════════════════════
        //  ERASER
        // ════════════════════════════════════
        private void EraseNearestLine()
        {
            if (_lines.Count == 0 || _mainCamera == null) return;

            // ERASER LOGIC RE-WRITE: The "Screen Wipe" (2D Depth-Independent Eraser)
            // We project the 3D drawing lines back onto the 2D phone screen and check their 
            // 2D pixel distance against our pinch location. If it visually crosses an drawn line, we delete it.

            Vector2 thumbScreen = handTracker.FingertipScreenPositions[0];
            Vector2 indexScreen = handTracker.FingertipScreenPositions[1];
            Vector2 pinchScreenPos = (thumbScreen + indexScreen) / 2f;
            
            // Convert percentage threshold into actual screen pixels
            float eraseRadiusPixels = Screen.width * _eraseRadiusScreenPct;
            float eraseRadiusSq = eraseRadiusPixels * eraseRadiusPixels;

            // GC OPTIMIZATION: Hoist bounds allocation out of the per-line loop
            Vector3 worldPinch = handTracker.FingertipWorldPosition;
            float giantBoxSize = 0.5f; // Half a meter
            Bounds pinchBounds = new Bounds(worldPinch, new Vector3(giantBoxSize, giantBoxSize, giantBoxSize));

            for (int i = _lines.Count - 1; i >= 0; i--)
            {
                var line = _lines[i];
                if (line == null || !line.gameObject.activeInHierarchy) continue;

                // ── BROAD-PHASE OPTIMIZATION (Bug #3 Fix) ──
                // Previously, this used lineRend.bounds which forced Unity to calculate
                // World Space LineRenderer bounding geometry every single frame. This caused massive
                // framerate spikes. We now use the perfectly cached static custom 3D bounds property.
                if (!pinchBounds.Intersects(line.CachedBounds))
                {
                    continue; // Skip this line completely (Massive performance gain)
                }

                // Grab the sparse Local Space anchor points (Bug #4 Fix Optimization: ~4x faster than checking spline points)
                var points = line.RawPoints;
                int count = points.Count;
                bool erased = false;
                Transform lineTrans = line.transform;

                // ERASER STRIDE OPTIMIZATION: For long lines, check every 3rd point
                // instead of every point. Adjacent raw points are close together,
                // so skipping 2 of 3 has negligible accuracy loss but cuts
                // WorldToViewportPoint calls by ~66%.
                int stride = count > 20 ? 3 : 1;

                // Check points with stride for fast overlap deletion.
                for (int p = 0; p < count; p += stride)
                {
                    // 1. Points are already stored in World Space.
                    Vector3 worldPt = points[p];
                    
                    // 2. We only care about lines IN FRONT of the camera. If it's behind us, ignore.
                    Vector3 viewPos = _mainCamera.WorldToViewportPoint(worldPt);
                    if (viewPos.z < 0) continue; 
                    
                    // 3. Convert to pixel space
                    Vector2 screenPt = new Vector2(viewPos.x * Screen.width, viewPos.y * Screen.height);
                    
                    // 4. Compare purely in 2D pixels (Ignored depth entirely!)
                    if ((screenPt - pinchScreenPos).sqrMagnitude < eraseRadiusSq)
                    {
                        Destroy(line.gameObject);
                        _lines.RemoveAt(i);
#if UNITY_EDITOR
                        Debug.Log($"[Drawing] Erased line {i} via 2D Screen Wipe");
#endif
                        erased = true;
                        break;
                    }
                }

                // Make sure to always check the absolute final point in case the stride skipped over a tiny dot
                if (!erased && count > 1)
                {
                    Vector3 worldPt = points[count - 1];
                    Vector3 viewPos = _mainCamera.WorldToViewportPoint(worldPt);
                    if (viewPos.z >= 0)
                    {
                        Vector2 screenPt = new Vector2(viewPos.x * Screen.width, viewPos.y * Screen.height);
                        if ((screenPt - pinchScreenPos).sqrMagnitude < eraseRadiusSq)
                        {
                            Destroy(line.gameObject);
                            _lines.RemoveAt(i);
#if UNITY_EDITOR
                            Debug.Log($"[Drawing] Erased line {i} via 2D Screen Wipe (Last Point)");
#endif
                        }
                    }
                }
            }
        }

        public void ClearCanvas()
        {
            foreach (var line in _lines)
            {
                if (line != null) Destroy(line.gameObject);
            }
            _lines.Clear();

            foreach (var line in _undoStack)
            {
                if (line != null) Destroy(line.gameObject);
            }
            _undoStack.Clear();

#if UNITY_EDITOR
            Debug.Log("[Drawing] Canvas cleared");
#endif
        }

        // ════════════════════════════════════
        //  UNDO / REDO
        // ════════════════════════════════════
        public void Undo()
        {
            if (_lines.Count == 0)
            {
#if UNITY_EDITOR
                Debug.Log("[Drawing] Nothing to undo");
#endif
                return;
            }

            var lastLine = _lines[^1];
            _lines.RemoveAt(_lines.Count - 1);
            lastLine.SetVisible(false);
            _undoStack.Push(lastLine);

#if UNITY_EDITOR
            Debug.Log($"[Drawing] Undo. Lines remaining: {_lines.Count}");
#endif
        }

        public void Redo()
        {
            if (_undoStack.Count == 0)
            {
                Debug.Log("[Drawing] Nothing to redo");
                return;
            }

            var line = _undoStack.Pop();
            line.SetVisible(true);
            _lines.Add(line);

            Debug.Log($"[Drawing] Redo. Lines total: {_lines.Count}");
        }

        // ════════════════════════════════════
        //  COLOR / BRUSH
        // ════════════════════════════════════
        private void OnColorChanged(Color color)
        {
            _currentColor = color;
            _isEraserMode = false;
            Debug.Log($"[Drawing] Color set to {color} (Eraser disabled)");
        }

        private void OnBrushSizeChanged(float size)
        {
            _currentBrushSize = size;
            _isEraserMode = false;
            Debug.Log($"[Drawing] Brush size set to {size} (Eraser disabled)");
        }

        private void OnEraserToggled()
        {
            _isEraserMode = true;
            Debug.Log("[Drawing] Eraser mode activated");
        }

        // FIX #6: Cooldown after menu close
        private void OnMenuStateChanged(bool isOpen)
        {
            // Tell HandTrackingManager to bypass posture check while menu is open
            if (handTracker != null) handTracker.IsMenuActive = isOpen;

            if (!isOpen)
            {
                _menuCloseCooldown = MENU_CLOSE_COOLDOWN;
            }
        }

        // ════════════════════════════════════
        //  SAVE (placeholder)
        // ════════════════════════════════════
        private void Save()
        {
            if (_lines.Count == 0)
            {
                Debug.LogWarning("[Drawing] Cannot save empty canvas!");
                return;
            }

            string id = DrawingSaveSystem.SaveDrawing(_lines);
            Debug.Log($"[Drawing] Saved successfully with ID: {id}");
            HapticService.Medium();
        }

        void OnDestroy()
        {
            if (handTracker != null)
            {
                handTracker.OnFingertipMoved -= OnFingertipMoved;
                handTracker.OnHandLost -= OnHandLost;
            }

            if (arcMenu != null)
            {
                arcMenu.OnColorChanged -= OnColorChanged;
                arcMenu.OnBrushSizeChanged -= OnBrushSizeChanged;
                arcMenu.OnEraserToggled -= OnEraserToggled;
                arcMenu.OnClearCanvas -= ClearCanvas;
                arcMenu.OnMenuStateChanged -= OnMenuStateChanged;
                arcMenu.OnSavePressed -= Save;
            }
        }
    }
}
