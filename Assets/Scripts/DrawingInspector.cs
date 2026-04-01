using UnityEngine;
using System.Collections.Generic;
using System.IO;
using UnityEngine.UIElements;

namespace SpatialDrawing
{
    /// <summary>
    /// Handles rebuilding coordinates off the load stream and adding interactive dragging triggers rotation.
    /// Production-level implementation with full state restore and memory management.
    /// </summary>
    public class DrawingInspector : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Material lineMaterial;

        private GameObject _container;
        private GameObject _backdrop; // Black quad behind the drawing
        private bool _isInspecting = false;
        public bool IsInspecting => _isInspecting;

        // ── Camera & Render State (per-camera save/restore) ──
        private struct CameraState
        {
            public Camera cam;
            public CameraClearFlags flags;
            public Color bgColor;
            public int cullingMask;
            public bool arBgEnabled;
        }
        private List<CameraState> _savedCameraStates = new List<CameraState>();

        // ── Ambient & Lights ──
        private UnityEngine.Rendering.AmbientMode _originalAmbientMode;
        private Color _originalAmbientColor;
        private List<Light> _disabledLights = new List<Light>();

        // ── World-line tracking (only restore what we hid) ──
        // We store instance IDs so we never accidentally re-activate new lines added after open.
        private HashSet<int> _hiddenLineInstanceIds = new HashSet<int>();

        // ── Gesture Input ──
        private Vector2 _lastTouchPos;
        private float   _lastPinchDist;
        private bool    _hasLastTouch;
        private bool    _hasLastPinch;

        // ── Inertia ──
        // x = yaw around cam.up, y = pitch around cam.right, z = roll around cam.forward
        // All in degrees/second. Applied after finger lift and exponentially damped to zero.
        private Vector3 _angularVelocity = Vector3.zero;
        private const float _inertiaDamping   = 0.92f;   // Multiply per frame — controls how fast spin fades
        private const float _inertiaThreshold = 0.05f;   // deg/s below which we stop completely
        // Velocity sampler: running weighted average over last few frames
        private Vector2 _frameDelta = Vector2.zero;       // Accumulated normalised delta this frame

        // ── Two-finger Twist State ──
        private float _lastTwistAngle;   // Previous frame's finger-pair angle in degrees
        private bool  _hasTwistAngle;    // True once we have at least one prior angle sample

        // ── Display State (LOCKED TO CAMERA) ──
        // Container is parented to the camera so it stays fixed on screen
        // even when the phone moves physically.
        private Vector3 _containerLocalPos;

        public void OpenInspector(string id)
        {
            if (_isInspecting) CloseInspector();
            _isInspecting = true;

            // ── 1. Save & Override ALL camera states ──
            _savedCameraStates.Clear();
            foreach (Camera c in Camera.allCameras)
            {
                var arBg = c.GetComponent<UnityEngine.XR.ARFoundation.ARCameraBackground>();
                _savedCameraStates.Add(new CameraState
                {
                    cam         = c,
                    flags       = c.clearFlags,
                    bgColor     = c.backgroundColor,
                    cullingMask = c.cullingMask,
                    arBgEnabled = arBg != null && arBg.enabled
                });

                // Do NOT disable arBg on Android! Disabling it at runtime leaves 
                // a stale internal command buffer which results in a bright yellow screen.
                // We will instead spawn a physical black 3D quad to block the camera feed.
            }

            // ── SPAWN 3D BLACK BACKDROP ──
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                _backdrop = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _backdrop.name = "InspectorBlackBackdrop";
                _backdrop.transform.SetParent(mainCam.transform, false);
                _backdrop.transform.localPosition = new Vector3(0, 0, 15f); // Deep behind the drawing
                _backdrop.transform.localScale = new Vector3(100f, 100f, 1f); // Massive screen blocker
                
                var renderer = _backdrop.GetComponent<MeshRenderer>();

                // Shader.Find() returns null on Android if the shader is not listed in
                // Project Settings → Graphics → Always Included Shaders.
                // Try a chain of known-safe shaders, then fall back to a copy of lineMaterial.
                Shader backdropShader = Shader.Find("Unlit/Color")
                                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                                     ?? Shader.Find("Mobile/Diffuse")
                                     ?? Shader.Find("Sprites/Default");

                Material mat;
                if (backdropShader != null)
                {
                    mat = new Material(backdropShader);
                    mat.color = Color.black;
                }
                else if (lineMaterial != null)
                {
                    // Last resort: clone the line material and paint it solid black.
                    // lineMaterial is guaranteed to exist because it was already assigned
                    // and working in the scene before the inspector opened.
                    mat = new Material(lineMaterial);
                    mat.color = Color.black;
                    Debug.LogWarning("[Inspector] Backdrop shader not found — using lineMaterial clone.");
                }
                else
                {
                    Debug.LogError("[Inspector] No shader available for backdrop. Backdrop skipped.");
                    mat = null;
                }

                if (mat != null)
                    renderer.material = mat;
                
                Destroy(_backdrop.GetComponent<Collider>());
            }

            // ── 2. Force black ambient & suppress all scene lights ──
            _originalAmbientMode  = RenderSettings.ambientMode;
            _originalAmbientColor = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;

            _disabledLights.Clear();
            var allLights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var l in allLights)
            {
                if (l.enabled)
                {
                    l.enabled = false;
                    _disabledLights.Add(l);
                }
            }

            // ── 3. Load JSON save data ──
            string path = Path.Combine(Application.persistentDataPath, "Creations", $"{id}.json");
            if (!File.Exists(path))
            {
                Debug.LogError($"[Inspector] Drawing not found: {path}");
                _isInspecting = false;
                RestoreCamerasAndLights(); // Undo the camera state changes
                return;
            }

            DrawingSaveData data = null;
            try
            {
                string json = File.ReadAllText(path);
                data = JsonUtility.FromJson<DrawingSaveData>(json);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Inspector] JSON parse failed for {id}: {e.Message}");
                _isInspecting = false;
                RestoreCamerasAndLights();
                return;
            }

            if (data == null || data.strokes == null || data.strokes.Count == 0)
            {
                Debug.LogWarning($"[Inspector] Drawing {id} has no strokes.");
                _isInspecting = false;
                RestoreCamerasAndLights();
                return;
            }

            // ── 4. Hide existing AR world drawings — record instance IDs we hide ──
            _hiddenLineInstanceIds.Clear();
            var allWorldLines = Object.FindObjectsByType<DrawingLine>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var line in allWorldLines)
            {
                // Only hide active lines that aren't inside our container
                if (line.gameObject.activeSelf)
                {
                    line.gameObject.SetActive(false);
                    _hiddenLineInstanceIds.Add(line.gameObject.GetInstanceID());
                }
            }

            // ── 5. Create pivot container at world origin ──
            _container = new GameObject("InspectedDrawing");
            _container.transform.position = Vector3.zero;
            _container.transform.rotation = Quaternion.identity;

            Bounds fullBounds = new Bounds();
            bool boundsSet = false;

            foreach (var stroke in data.strokes)
            {
                if (stroke == null || stroke.rawPoints == null || stroke.rawPoints.Count < 2) continue;

                GameObject lineObj = new GameObject("Line");
                lineObj.transform.SetParent(_container.transform, false);

                var drawingLine = lineObj.AddComponent<DrawingLine>();

                List<Vector3> pts = new List<Vector3>(stroke.rawPoints.Count);
                foreach (var p in stroke.rawPoints)
                {
                    if (p != null) pts.Add(p.ToVector3());
                }

                Color c = stroke.color != null ? stroke.color.ToColor() : Color.white;
                drawingLine.Initialize(c, stroke.width, lineMaterial);
                drawingLine.LoadPoints(pts);

                if (!boundsSet) { fullBounds = drawingLine.CachedBounds; boundsSet = true; }
                else fullBounds.Encapsulate(drawingLine.CachedBounds);
            }

            // ── 6. Lock drawing to camera view ──
            Camera cam = Camera.main;
            if (boundsSet && cam != null)
            {
                // Parent to camera — this ensures "no matter how much I move my phone, nothing should happen"
                // The drawing will be perfectly glued to the screen.
                _container.transform.SetParent(cam.transform, false);

                // Offset all children so the drawing itself is centered at its own bounds origin
                foreach (Transform child in _container.transform)
                    child.localPosition -= fullBounds.center;

                // Compute ideal distance using FOV framing
                float radius   = fullBounds.extents.magnitude;
                float fovRad   = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
                float distance = (fovRad > 0.001f) ? (radius / Mathf.Sin(fovRad)) : 1.0f;
                distance       = Mathf.Clamp(distance * 1.5f, 0.5f, 5f); // Padding for comfort

                // Place container at a local offset in front of camera
                _container.transform.localPosition = new Vector3(0, 0, distance);
                _container.transform.localRotation = Quaternion.identity;
                _containerLocalPos = _container.transform.localPosition;
            }

            // ── 7. Navigate UI ──
            var uiController = Object.FindAnyObjectByType<SpatialDrawing.UI.UIToolkitController>();
            if (uiController != null) uiController.GoToInspector();
        }

        public void SummonToAR()
        {
            if (_container != null)
            {
                var drawingEngine = Object.FindAnyObjectByType<DrawingEngine>();

                // Unparent all lines so they persist in the AR scene
                // Grab index 0 repeatedly to preserve chronological order for the Undo stack
                while (_container.transform.childCount > 0)
                {
                    Transform child = _container.transform.GetChild(0);
                    child.SetParent(null, true);

                    // Inject into the active drawing engine so it can be erased exactly like a normal line
                    var line = child.GetComponent<DrawingLine>();
                    if (drawingEngine != null && line != null)
                    {
                        drawingEngine.InjectSummonedLine(line);
                    }
                }
            }

            // Close the inspector normally to restore state and returning hidden models
            CloseInspector();
        }

        public void CloseInspector()
        {
            _isInspecting = false;

            // Destroy the temporary inspector drawing container
            if (_container != null)
            {
                Destroy(_container);
                _container = null;
            }

            // Destroy the black backdrop
            if (_backdrop != null)
            {
                Destroy(_backdrop);
                _backdrop = null;
            }

            // Restore ONLY the AR world lines we explicitly hid at open time.
            // This prevents activating lines that were inactive before the inspector was opened,
            // and prevents activating any newly created lines.
            if (_hiddenLineInstanceIds.Count > 0)
            {
                var allLines = Object.FindObjectsByType<DrawingLine>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var line in allLines)
                {
                    if (_hiddenLineInstanceIds.Contains(line.gameObject.GetInstanceID()))
                        line.gameObject.SetActive(true);
                }
                _hiddenLineInstanceIds.Clear();
            }

            RestoreCamerasAndLights();

            _hasLastTouch = false;
            _hasLastPinch = false;
        }

        private void RestoreCamerasAndLights()
        {
            // Restore every camera to its exact pre-inspection state
            foreach (var state in _savedCameraStates)
            {
                if (state.cam == null) continue;
                state.cam.clearFlags     = state.flags;
                state.cam.backgroundColor = state.bgColor;
                state.cam.cullingMask    = state.cullingMask;

                var arBg = state.cam.GetComponent<UnityEngine.XR.ARFoundation.ARCameraBackground>();
                if (arBg != null) arBg.enabled = state.arBgEnabled;
            }
            _savedCameraStates.Clear();

            // Restore ambient
            RenderSettings.ambientMode  = _originalAmbientMode;
            RenderSettings.ambientLight = _originalAmbientColor;

            // Restore every light we suppressed
            foreach (var l in _disabledLights)
            {
                if (l != null) l.enabled = true;
            }
            _disabledLights.Clear();
        }

        void Update()
        {
            if (!_isInspecting || _container == null) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            bool isDragging = false;
            Vector2 delta   = Vector2.zero;

            // ── Count active touches ──
            int activeTouchCount = 0;
            UnityEngine.InputSystem.Controls.TouchControl t0 = null;
            UnityEngine.InputSystem.Controls.TouchControl t1 = null;

            if (UnityEngine.InputSystem.Touchscreen.current != null)
            {
                foreach (var touch in UnityEngine.InputSystem.Touchscreen.current.touches)
                {
                    if (touch.isInProgress)
                    {
                        if (activeTouchCount == 0) t0 = touch;
                        else if (activeTouchCount == 1) t1 = touch;
                        activeTouchCount++;
                    }
                }
            }

            // ── 1-finger Rotation (or Mouse) ──
            // Any new touch immediately cancels inertia so the user takes back control instantly.
            if (activeTouchCount == 1 && t0 != null)
            {
                Vector2 pos = t0.position.ReadValue();
                if (_hasLastTouch)
                {
                    isDragging = true;
                    delta = pos - _lastTouchPos;
                }
                else
                {
                    // Finger just landed — kill any ongoing inertia immediately.
                    _angularVelocity = Vector3.zero;
                }
                _lastTouchPos = pos;
                _hasLastTouch = true;
            }
            else if (UnityEngine.InputSystem.Mouse.current != null &&
                     UnityEngine.InputSystem.Mouse.current.leftButton.isPressed)
            {
                if (!_hasLastTouch)
                {
                    // Mouse button just pressed — kill inertia.
                    _angularVelocity = Vector3.zero;
                }
                isDragging    = true;
                delta         = UnityEngine.InputSystem.Mouse.current.delta.ReadValue();
                _hasLastTouch = true;
            }
            else
            {
                _hasLastTouch = false;
            }

            // ── Apply 1-finger / Mouse Rotation ──
            if (isDragging && delta.sqrMagnitude > 0.01f)
            {
                // Normalise by screen height so speed is DPI-independent.
                float normX = delta.x / Screen.height;
                float normY = delta.y / Screen.height;

                // Sensitivity tuned for crisp, near 1-to-1 finger tracking.
                const float sensitivity = 200f;

                float yawDeg   = -normX * sensitivity;   // horizontal swipe → yaw
                float pitchDeg =  normY * sensitivity;   // vertical swipe   → pitch

                // FIX: Always rotate around the CAMERA'S own axes using Space.World.
                // Because the axes are evaluated in world space from the camera transform,
                // the rotation always matches the finger direction regardless of how the
                // object is currently oriented — this eliminates gimbal lock entirely.
                _container.transform.Rotate(cam.transform.up,    yawDeg,   Space.World);
                _container.transform.Rotate(cam.transform.right, pitchDeg, Space.World);

                // Record current frame's normalised velocity for inertia hand-off.
                // We store in deg/s (divide by deltaTime to convert from deg/frame).
                if (Time.deltaTime > 0f)
                {
                    Vector3 frameVel = new Vector3(yawDeg, pitchDeg, 0f) / Time.deltaTime;
                    // Weighted blend: 70% new frame, 30% previous — smooths jitter
                    // while still capturing the most recent movement intent.
                    // Z (roll) is only written by the twist gesture below, so preserve it here.
                    _angularVelocity = Vector3.Lerp(
                        new Vector3(_angularVelocity.x, _angularVelocity.y, _angularVelocity.z),
                        frameVel + new Vector3(0, 0, _angularVelocity.z),   // keep z intact
                        0.7f);
                }
                _frameDelta = delta;
            }
            else if (!isDragging)
            {
                // ── Inertia: spin continues after finger lift ──
                if (_angularVelocity.sqrMagnitude > _inertiaThreshold * _inertiaThreshold)
                {
                    float yawDeg   = _angularVelocity.x * Time.deltaTime;
                    float pitchDeg = _angularVelocity.y * Time.deltaTime;

                    // Same camera-relative axes as the live rotation — guarantees
                    // inertia direction matches the finger direction the user felt.
                    _container.transform.Rotate(cam.transform.up,    yawDeg,   Space.World);
                    _container.transform.Rotate(cam.transform.right, pitchDeg, Space.World);
                    // Note: twist (roll) does NOT participate in inertia — it stops immediately.

                    // Dampen this frame — exponential decay towards zero.
                    _angularVelocity *= _inertiaDamping;
                }
                else
                {
                    // Below threshold — zero out completely so we don't drift forever.
                    _angularVelocity = Vector3.zero;
                }
            }

            // ── Mouse Scroll Zoom (unchanged) ──
            if (UnityEngine.InputSystem.Mouse.current != null)
            {
                Vector2 scroll = UnityEngine.InputSystem.Mouse.current.scroll.ReadValue();
                if (Mathf.Abs(scroll.y) > 0.01f)
                {
                    float zoomStep    = scroll.y * 0.001f;
                    float currentDist = _container.transform.localPosition.z;
                    float newDist     = Mathf.Clamp(currentDist - zoomStep * currentDist, 0.2f, 10f);
                    _container.transform.localPosition = new Vector3(0, 0, newDist);
                    _containerLocalPos = _container.transform.localPosition;
                }
            }

            // ── 2-finger Pinch Zoom + Twist Rotation ──
            // Both gestures are read simultaneously from the same two touch points:
            //   • Distance change  → zoom  (existing behaviour, unchanged)
            //   • Angle change     → roll  (new: clockwise twist = clockwise spin around cam.forward)
            if (activeTouchCount == 2 && t0 != null && t1 != null)
            {
                Vector2 pos0    = t0.position.ReadValue();
                Vector2 pos1    = t1.position.ReadValue();
                float   currMag = (pos0 - pos1).magnitude;

                // Angle of the finger-to-finger vector relative to screen X axis.
                Vector2 fingerVec  = pos1 - pos0;
                float   currAngle  = Mathf.Atan2(fingerVec.y, fingerVec.x) * Mathf.Rad2Deg;

                if (_hasLastPinch)
                {
                    // ── Zoom (distance) ──
                    float normDiff        = (currMag - _lastPinchDist) / Screen.height;
                    float zoomSensitivity = 2.5f;
                    float currentDist     = _container.transform.localPosition.z;
                    float newDist         = Mathf.Clamp(currentDist - normDiff * zoomSensitivity, 0.2f, 10f);
                    _container.transform.localPosition = new Vector3(0, 0, newDist);
                    _containerLocalPos = _container.transform.localPosition;

                    // ── Twist (angle delta) ──
                    if (_hasTwistAngle)
                    {
                        float angleDelta = Mathf.DeltaAngle(_lastTwistAngle, currAngle);
                        // angleDelta > 0 = CCW (positive math convention) = we want CCW screen rotation.
                        // cam.forward points at the user; rotating around it with a positive angle
                        // spins the object counter-clockwise as seen from the camera, matching finger intent.
                        const float twistSensitivity = 1.0f;  // 1-to-1: finger angle = drawing angle
                        float rollDeg = angleDelta * twistSensitivity;

                        _container.transform.Rotate(cam.transform.forward, rollDeg, Space.World);
                        // Twist stops immediately when fingers lift — no inertia for roll.
                    }
                }

                _lastPinchDist = currMag;
                _hasLastPinch  = true;
                _hasTwistAngle = true;
                _lastTwistAngle = currAngle;
                _hasLastTouch  = false;
            }
            else
            {
                _hasLastPinch  = false;
                _hasTwistAngle = false;
            }
        }
    }
}
