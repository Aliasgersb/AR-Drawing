using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

using Mediapipe;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Unity;

namespace SpatialDrawing
{
    // ════════════════════════════════════════════════════════════════
    //  ONE EURO FILTER — Adaptive low-pass filter for jitter removal
    //  Smooths heavily when still, responds quickly when moving fast.
    // ════════════════════════════════════════════════════════════════
    internal class OneEuroFilter
    {
        private float _minCutoff;
        private float _beta;
        private float _dCutoff;
        private float _prevValue;
        private float _prevDerivative;
        private float _prevTime;
        private bool _initialized;

        public OneEuroFilter(float minCutoff = 1.0f, float beta = 0.001f, float dCutoff = 1.0f)
        {
            _minCutoff = minCutoff;
            _beta = beta;
            _dCutoff = dCutoff;
            _initialized = false;
        }

        private static float SmoothingFactor(float te, float cutoff)
        {
            float r = 2f * Mathf.PI * cutoff * te;
            return r / (r + 1f);
        }

        public float Filter(float value, float timestamp)
        {
            if (!_initialized)
            {
                _prevValue = value;
                _prevDerivative = 0f;
                _prevTime = timestamp;
                _initialized = true;
                return value;
            }

            float te = timestamp - _prevTime;
            if (te <= 0f) te = 1f / 60f; // fallback to ~60fps
            // THERMAL THROTTLE GUARD: Clamp te to prevent degraded smoothing at low FPS.
            // Without this, when the phone throttles from 60fps→25fps, te doubles,
            // making the derivative estimate noisy and the smoothing unstable.
            // Capping at 1/20s (50ms) ensures consistent filter behavior down to 20fps.
            if (te > 0.05f) te = 0.05f;
            _prevTime = timestamp;

            // Filter the derivative (speed estimate)
            float alphaD = SmoothingFactor(te, _dCutoff);
            float derivative = (value - _prevValue) / te;
            float smoothDerivative = alphaD * derivative + (1f - alphaD) * _prevDerivative;
            _prevDerivative = smoothDerivative;

            // Adaptive cutoff based on speed
            float cutoff = _minCutoff + _beta * Mathf.Abs(smoothDerivative);

            // Filter the value
            float alpha = SmoothingFactor(te, cutoff);
            float result = alpha * value + (1f - alpha) * _prevValue;
            _prevValue = result;

            return result;
        }

        public void Reset()
        {
            _initialized = false;
        }

        public void UpdateParams(float minCutoff, float beta)
        {
            _minCutoff = minCutoff;
            _beta = beta;
        }
    }

    internal class OneEuroFilter3D
    {
        private readonly OneEuroFilter _xFilter;
        private readonly OneEuroFilter _yFilter;
        private readonly OneEuroFilter _zFilter;

        public OneEuroFilter3D(float minCutoff = 1.0f, float beta = 0.001f, float dCutoff = 1.0f)
        {
            _xFilter = new OneEuroFilter(minCutoff, beta, dCutoff);
            _yFilter = new OneEuroFilter(minCutoff, beta, dCutoff);
            _zFilter = new OneEuroFilter(minCutoff, beta, dCutoff);
        }

        public Vector3 Filter(Vector3 value, float timestamp)
        {
            return new Vector3(
                _xFilter.Filter(value.x, timestamp),
                _yFilter.Filter(value.y, timestamp),
                _zFilter.Filter(value.z, timestamp)
            );
        }

        public void Reset()
        {
            _xFilter.Reset();
            _yFilter.Reset();
            _zFilter.Reset();
        }
    }

    internal class OneEuroFilter2D
    {
        private readonly OneEuroFilter _xFilter;
        private readonly OneEuroFilter _yFilter;

        public OneEuroFilter2D(float minCutoff = 1.0f, float beta = 0.001f, float dCutoff = 1.0f)
        {
            _xFilter = new OneEuroFilter(minCutoff, beta, dCutoff);
            _yFilter = new OneEuroFilter(minCutoff, beta, dCutoff);
        }

        public Vector2 Filter(Vector2 value, float timestamp)
        {
            return new Vector2(
                _xFilter.Filter(value.x, timestamp),
                _yFilter.Filter(value.y, timestamp)
            );
        }

        public void Reset()
        {
            _xFilter.Reset();
            _yFilter.Reset();
        }

        public void UpdateParams(float minCutoff, float beta)
        {
            _xFilter.UpdateParams(minCutoff, beta);
            _yFilter.UpdateParams(minCutoff, beta);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  HAND TRACKING MANAGER
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Manages MediaPipe hand landmark detection using the AR camera feed.
    /// Extracts the pinch midpoint and converts it to 3D world space.
    /// Uses One Euro filtering, motion dead zone, and pinch hysteresis
    /// for professional-grade smoothness and stability.
    /// </summary>
    public class HandTrackingManager : MonoBehaviour
    {
        // ── Events ──
        public event Action<Vector3> OnFingertipMoved;
        public event Action OnHandDetected;
        public event Action OnHandLost;

        public bool IsHandDetected { get; private set; }
        public Vector3 FingertipWorldPosition { get; private set; }
        public bool IsPinching { get; private set; }
        public bool IsIndexPinching { get; private set; }
        public bool IsMiddlePinching { get; private set; }

        /// <summary>Set by menu system. When true, posture check is bypassed for reliable taps.</summary>
        public bool IsMenuActive { get; set; }
        
        public bool IsIndexExtended { get; private set; }
        public bool IsMiddleExtended { get; private set; }
        public bool IsRingExtended { get; private set; }
        public bool IsPinkyExtended { get; private set; }
        public bool IsThumbExtendedState { get; private set; }

        /// <summary>World-space positions of all 5 fingertips: Thumb, Index, Middle, Ring, Pinky.</summary>
        public Vector3[] FingertipWorldPositions { get; private set; } = new Vector3[5];

        /// <summary>Screen-space positions of all 5 fingertips (pixels, origin bottom-left).</summary>
        public Vector2[] FingertipScreenPositions { get; private set; } = new Vector2[5];

        // ── Arc Menu Gesture Data ──
        /// <summary>World-space center of the palm (average of wrist + middle MCP).</summary>
        public Vector3 PalmCenter { get; private set; }
        /// <summary>Approximate palm-facing direction (cross product of finger base vectors).</summary>
        public Vector3 PalmNormal { get; private set; }
        /// <summary>Normalized distance between middle fingertip and thumb tip.</summary>
        public float MiddleThumbDistance { get; private set; }
        /// <summary>True when all 5 fingers including thumb are fully extended (open palm).</summary>
        public bool IsAllFingersExtended { get; private set; }
        /// <summary>True if the detected hand is the right hand, false if left hand.</summary>
        public bool IsRightHand { get; private set; }
        /// <summary>True if the true 3D Z-depth confirms the palm is facing the camera, rejecting back-of-hand silhouettes.</summary>
        public bool IsHandFacingCamera { get; private set; }

        // ── Dominant Hand Setting ──
        /// <summary>If true, ONLY the Left hand is processed. If false, ONLY the Right hand is processed.</summary>
        public bool IsLeftHandDominant { get; private set; }
        /// <summary>Fired when the user holds up the non-dominant hand (passes true if left hand was dominant but right was shown, etc.)</summary>
        public event Action<bool> OnWrongHandDetected;
        public event Action OnWrongHandDismissed;

        // Continuous timer for wrong hand warnings to prevent instant flickering
        private float _continuousWrongHandTimer = 0f;
        private bool _isWrongHandAlertActive = false;

        // ── UI Pause Gate ──
        /// <summary>
        /// When true, camera frame processing is suspended to free the main thread for smooth UI rendering.
        /// Set this to true when a full-screen UI (Settings, Gallery) is visible.
        /// </summary>
        public bool IsUIBlocking { get; set; } = false;

        // ── Settings ──
        [Header("Tracking Settings")]
        [SerializeField] private float drawDistance = 0.35f;
        [SerializeField] private int processEveryNFrames = 1; // Enable per-frame processing for smooth 60fps sync



        [Header("Pinch Detection")]
        [Tooltip("Percentage of screen width for a pinch to activate (e.g. 0.12 = 12% of screen).")]
        [SerializeField] private float pinchEnterThreshold = 0.12f;
        [Tooltip("Percentage of screen width for a pinch to deactivate (must be > enter threshold).")]
        [SerializeField] private float pinchExitThreshold = 0.15f;

        // ── References ──
        [Header("AR References")]
        [SerializeField] private ARCameraManager arCameraManager;
        [SerializeField] private Camera arCamera;

        // ── MediaPipe ──
        private HandLandmarker _handLandmarker;
        private HandLandmarkerResult _result;

        private int _frameCount;
        private bool _isInitialized;

        // Reusable texture for frame processing
        private Texture2D _processingTexture;
        private bool _isProcessing; // Async guard

        // Captured image dimensions (for aspect-ratio-corrected coordinate mapping)
        private int _capturedImageWidth;
        private int _capturedImageHeight;

        [Header("Visual Stabilization (Anti-Jitter)")]
        [Tooltip("Minimum cutoff freq when IDLE (UI). Lower = more stable when still.")]
        [SerializeField] private float idleMinCutoff = 1.5f;
        [Tooltip("Speed coefficient when IDLE. Higher = faster response when moving.")]
        [SerializeField] private float idleBeta = 1.5f;

        [Tooltip("Minimum cutoff freq when DRAWING. Lower = crushes micro-jitters.")]
        [SerializeField] private float drawMinCutoff = 0.5f;
        [Tooltip("Speed coefficient when DRAWING. Lower = prevents jagged movement.")]
        [SerializeField] private float drawBeta = 2.0f;
        
        private float _currentMinCutoff;
        private float _currentBeta;

        // ── Visual Overlay Filter Parameters (FIXED — never shared with drawing filter) ──
        // High minCutoff + high beta = extremely responsive, near-zero lag overlay dots.
        // These must NEVER be changed by IsPinching or sensitivity level — doing so
        // is the root cause of every previous fix attempt breaking the drawing system.
        private const float VISUAL_MIN_CUTOFF = 3.5f;
        private const float VISUAL_BETA       = 6.0f;

        // ── Dynamic Sensitivity Settings ──
        private int _sensitivityLevel = 2; // Default to mid (Level 2)

        // ── Hand Tracking Grace Period ──
        // Prevents drawing from stopping when the hand briefly exits the camera frame edge.
        // MediaPipe returns false for partial hand visibility — this timer absorbs those drops.
        private float _handLostGraceTimer;
        private const float HAND_LOST_GRACE_DURATION = 0.28f; // Ext. from 0.15s: absorbs async pipe + edge-frame drops (up to ~17 missed frames at 60fps)

        // ── Smoothing State ──
        private float _currentFrameCaptureTime; // Fix: Track exact frame capture time for prediction sync
        private Vector2 _midScreenPosition;     // Fix: Track midpoint for 3D prediction
        private Vector2 _prevMidScreenPosition;
        private Vector2 _midScreenVelocity;
        private float _currentDynamicDepth;
        // One Euro filter on normalized landmark coordinates (single pass — no cascading)
        private OneEuroFilter2D _landmarkFilter;
        private OneEuroFilter2D[] _visualFilters = new OneEuroFilter2D[5];

        private Vector2[] _prevScreenPositions = new Vector2[5];
        private Vector2[] _screenVelocities = new Vector2[5];
        private float _lastLandmarkUpdateTime;



        // Landmark indices (MediaPipe hand model)
        private const int WRIST = 0;
        private const int THUMB_CMC = 1;
        private const int THUMB_MCP = 2;
        private const int THUMB_IP = 3;
        private const int THUMB_TIP = 4;
        private const int INDEX_MCP = 5;
        private const int INDEX_FINGERTIP = 8;
        private const int MIDDLE_MCP = 9;
        private const int MIDDLE_FINGERTIP = 12;
        private const int RING_FINGERTIP = 16;
        private const int PINKY_MCP = 17;
        private const int PINKY_FINGERTIP = 20;
        private const int INDEX_PIP = 6;
        private const int MIDDLE_PIP = 10;
        private const int RING_PIP = 14;
        private const int PINKY_PIP = 18;
        private static readonly int[] FINGERTIP_INDICES = { THUMB_TIP, INDEX_FINGERTIP, MIDDLE_FINGERTIP, RING_FINGERTIP, PINKY_FINGERTIP };

        void Start()
        {
            if (arCameraManager == null)
                arCameraManager = FindAnyObjectByType<ARCameraManager>();
            if (arCamera == null)
                arCamera = Camera.main;

            // --- AR CAMERA OVERRIDE FOR BEFORE-RENDER SYNC ---
            // 1. Enable Auto-Focus to ensure the real-world camera feed is crystal clear.
            if (arCameraManager != null)
            {
                arCameraManager.autoFocusRequested = false;
            }
            
            // 2. Unlock engine rendering limits to catch 'Before Render' updates as fast as physically possible.
            QualitySettings.vSyncCount = 0; // Prevent intermittent startup frame-rate half-speed locked
            Application.targetFrameRate = 60; // Force 60 FPS permanently on mobile

            // Drawing midpoint filter: lighter smoothing (preserves small precise movements)
            _landmarkFilter = new OneEuroFilter2D(0.4f, 2.0f);

            _currentMinCutoff = idleMinCutoff;
            _currentBeta = idleBeta;

            // Visual overlay filters: initialized with FIXED independent parameters.
            // VISUAL_MIN_CUTOFF / VISUAL_BETA are constants — do not pass _currentMinCutoff here.
            // Using the drawing filter's parameters here is the root cause of overlay lag/vibration.
            for (int i = 0; i < 5; i++)
            {
                _visualFilters[i] = new OneEuroFilter2D(VISUAL_MIN_CUTOFF, VISUAL_BETA);
            }

            StartCoroutine(InitializeMediaPipeAsync());
        }

        private IEnumerator InitializeMediaPipeAsync()
        {
            Debug.Log("[HandTracking] Starting MediaPipe initialization...");

            // 1. Initialize GPU Manager
            yield return GpuManager.Initialize();
            if (!GpuManager.IsInitialized)
            {
                Debug.LogWarning("[HandTracking] GpuManager failed to initialize. Falling back to CPU.");
            }

            // 2. Load Model File (Handling Android APK extraction)
            string modelName = "hand_landmarker.bytes";
            string filePath = Path.Combine(Application.streamingAssetsPath, modelName);
            byte[] modelBytes = null;

            if (filePath.Contains("://") || filePath.Contains(":///"))
            {
                // Android/WebGL: Must use UnityWebRequest
                using (UnityWebRequest request = UnityWebRequest.Get(filePath))
                {
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"[HandTracking] Failed to load model from StreamingAssets at {filePath}: {request.error}");
                        yield break;
                    }
                    modelBytes = request.downloadHandler.data;
                }
            }
            else
            {
                // Editor/PC: Direct file load
                if (!File.Exists(filePath))
                {
                    Debug.LogError($"[HandTracking] Model file not found at {filePath}");
                    yield break;
                }
                modelBytes = File.ReadAllBytes(filePath);
            }

            if (modelBytes == null || modelBytes.Length == 0)
            {
                Debug.LogError("[HandTracking] Loaded model bytes are empty.");
                yield break;
            }

            Debug.Log($"[HandTracking] Successfully loaded model ({modelBytes.Length} bytes). Creating Landmarker...");

            // 3. Create HandLandmarker
            bool useGpu = GpuManager.IsInitialized;
            bool initialized = TryCreateLandmarker(modelBytes,
                useGpu ? Mediapipe.Tasks.Core.BaseOptions.Delegate.GPU : Mediapipe.Tasks.Core.BaseOptions.Delegate.CPU,
                useGpu);

            // Fallback to CPU if GPU creation fails despite Manager being initialized
            if (!initialized && useGpu)
            {
                Debug.LogWarning("[HandTracking] GPU creation failed, falling back to CPU...");
                initialized = TryCreateLandmarker(modelBytes, Mediapipe.Tasks.Core.BaseOptions.Delegate.CPU, false);
            }

            if (initialized)
            {
                _result = HandLandmarkerResult.Alloc(1);
                _isInitialized = true;
                Debug.Log("[HandTracking] MediaPipe fully initialized and ready!");
            }
            else
            {
                Debug.LogError("[HandTracking] Failed to initialize MediaPipe completely.");
            }
        }

        private bool TryCreateLandmarker(byte[] modelBytes,
            Mediapipe.Tasks.Core.BaseOptions.Delegate delegateType, bool useGpu)
        {
            try
            {
                var options = new HandLandmarkerOptions(
                    new Mediapipe.Tasks.Core.BaseOptions(
                        delegateType,
                        modelAssetBuffer: modelBytes
                    ),
                    runningMode: RunningMode.IMAGE,
                    numHands: 1,
                    minHandDetectionConfidence: 0.5f,
                    minHandPresenceConfidence: 0.5f,
                    minTrackingConfidence: 0.5f
                );

                if (useGpu)
                {
                    _handLandmarker = HandLandmarker.CreateFromOptions(
                        options, GpuManager.GpuResources);
                }
                else
                {
                    _handLandmarker = HandLandmarker.CreateFromOptions(options);
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HandTracking] Landmarker creation failed ({delegateType}): {e.Message}");
                return false;
            }
        }



        void Update()
        {
            if (!_isInitialized) return;

            // Determine target smoothing based on context and user sensitivity level
            float targetCutoff = IsPinching ? drawMinCutoff : idleMinCutoff;
            float targetBeta = IsPinching ? drawBeta : idleBeta;

            // Apply the 5-step sensitivity modifiers
            switch (_sensitivityLevel)
            {
                case 0: // Very Low (Heaviest Smoothing)
                    targetCutoff *= 0.2f;
                    targetBeta *= 0.5f;
                    break;
                case 1: // Low (Heavy Smoothing)
                    targetCutoff *= 0.5f;
                    targetBeta *= 0.75f;
                    break;
                case 2: // Medium (Default, no change)
                    break;
                case 3: // High (Light Smoothing)
                    targetCutoff *= 2.0f;
                    targetBeta *= 1.5f;
                    break;
                case 4: // Very High (Raw/No Smoothing)
                    targetCutoff *= 5.0f;
                    targetBeta *= 3.0f;
                    break;
            }

            // OPTIMIZATION (Bug #2 Fix): Dynamic OneEuro Filter Beta Tuning
            // Lerp the cutoff and beta for the visual filters based on pinch state
            _currentMinCutoff = Mathf.Lerp(_currentMinCutoff, targetCutoff, Time.deltaTime * 12f);
            _currentBeta      = Mathf.Lerp(_currentBeta,      targetBeta,   Time.deltaTime * 12f);

            // Update ONLY the drawing (landmark) filter — visual filters use fixed independent params.
            // DO NOT add _visualFilters to this loop. That is the root cause of overlay vibration.
            _landmarkFilter?.UpdateParams(_currentMinCutoff, _currentBeta);

            // Visual overlay filters are initialized once with VISUAL_MIN_CUTOFF / VISUAL_BETA
            // and never updated again. Their parameters are intentionally decoupled from drawing state.

            _frameCount++;
            if (_frameCount % processEveryNFrames != 0) return;

            // ── UI PAUSE GATE ──
            // When a full-screen UI (Settings/Gallery) is visible, the camera's
            // YUV→RGBA conversion + MediaPipe inference runs on the main thread
            // and competes directly with UI rendering. Pausing here gives the UI
            // thread 100% of its budget, delivering smooth 60fps scrolling.
            if (IsUIBlocking) return;

            ProcessFrame();
        }

        private void ProcessFrame()
        {
            // ASYNC GUARD: Don't start a new conversion while one is in-flight.
            // This skips the current frame's camera image, which is fine — the async
            // callback will fire shortly and process the previous frame's image.
            if (_isProcessing) return;

            // Try to get the camera image from AR Foundation
            if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
            {
                return;
            }

            try
            {
                // PERFORMANCE OPTIMIZATION: Downscale to ~640p for MediaPipe processing.
                // MediaPipe internally resizes to 224x224 anyway, so feeding it 1080p+ is wasteful.
                int targetWidth = 640;
                int targetHeight = (cpuImage.height * targetWidth) / cpuImage.width;

                // Create or resize the processing texture to the DOWNSCALED resolution
                if (_processingTexture == null ||
                    _processingTexture.width != targetWidth ||
                    _processingTexture.height != targetHeight)
                {
                    if (_processingTexture != null) Destroy(_processingTexture);
                    _processingTexture = new Texture2D(
                        targetWidth, targetHeight,
                        TextureFormat.RGBA32, false
                    );
                }

                // IMPORTANT: Keep original dimensions for coordinate mapping
                _capturedImageWidth = cpuImage.width;
                _capturedImageHeight = cpuImage.height;

                // Convert XRCpuImage to the downscaled RGBA32 texture
                var conversionParams = new XRCpuImage.ConversionParams(
                    cpuImage, TextureFormat.RGBA32,
                    XRCpuImage.Transformation.None
                );
                // Force target resolution in conversion
                conversionParams.outputDimensions = new Vector2Int(targetWidth, targetHeight);

                // ASYNC CONVERSION: Offloads the heavy YUV→RGBA pixel conversion to a
                // background thread, freeing the main Unity render thread for uninterrupted
                // frame rendering. The callback fires on the main thread when complete.
                _isProcessing = true;
                _currentFrameCaptureTime = Time.time; // Fix: Record exact capture time right before async pipeline
                cpuImage.ConvertAsync(conversionParams, OnAsyncConversionComplete);
            }
            catch (Exception e)
            {
                _isProcessing = false;
                Debug.LogWarning($"[HandTracking] ProcessFrame error: {e.Message}");
            }
            finally
            {
                // Safe to dispose immediately — ConvertAsync copies what it needs internally
                cpuImage.Dispose();
            }
        }

        /// <summary>
        /// Callback invoked on the main thread when the async YUV→RGBA conversion completes.
        /// Copies the converted pixel data into the processing texture and runs MediaPipe detection.
        /// </summary>
        private void OnAsyncConversionComplete(
            XRCpuImage.AsyncConversionStatus status,
            XRCpuImage.ConversionParams conversionParams,
            Unity.Collections.NativeArray<byte> data)
        {
            _isProcessing = false;

            if (status != XRCpuImage.AsyncConversionStatus.Ready)
            {
                return;
            }

            if (_processingTexture == null) return;

            try
            {
                // Copy the async result into the pre-allocated texture buffer
                _processingTexture.GetRawTextureData<byte>().CopyFrom(data);
                _processingTexture.Apply();

                // Run MediaPipe detection on the freshly loaded texture
                RunDetection();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HandTracking] Detection error: {e.Message}");
            }
        }

        /// <summary>
        /// Runs MediaPipe hand landmark detection on _processingTexture and processes
        /// the result (handedness, landmarks, pinch state, palm normal, etc.).
        /// </summary>
        private void RunDetection()
        {
            // CRITICAL GC FIX: MediaPipe's Image struct allocates managed memory that must be manually
            // disposed. Using `using` block ensures it's instantly freed, stopping 60Hz GC spikes.
            using (var mpImage = new Image(ImageFormat.Types.Format.Srgba, _processingTexture))
            {
                // Run hand detection
                bool detected = _handLandmarker.TryDetect(mpImage, null, ref _result);

                if (detected && _result.handLandmarks != null && _result.handLandmarks.Count > 0)
                {
                // Hand found — reset grace timer
                _handLostGraceTimer = 0f;

                // Update Handedness if available
                if (_result.handedness != null && _result.handedness.Count > 0)
                {
                    var categories = _result.handedness[0].categories;
                    if (categories != null && categories.Count > 0)
                    {
                        IsRightHand = categories[0].categoryName == "Right";
                    }
                }

                // ── STRICT DOMINANT HAND FILTERING ──
                // If the user's setting doesn't match the AI's detected hand, reject processing.
                if (IsLeftHandDominant == IsRightHand) 
                {
                    // FIX: Use explicitly captured frame time instead of volatile Time.deltaTime inside async pipe
                    float dt = Time.time - _currentFrameCaptureTime;
                    // Fallback to a sensible max delta if time jumps wildly or callback is instant
                    if (dt <= 0f || dt > 0.1f) dt = 1f / 30f; 

                    // The detected hand does NOT match the requested dominant hand!
                    _continuousWrongHandTimer += dt;
                    if (_continuousWrongHandTimer >= 1.5f)
                    {
                        if (!_isWrongHandAlertActive)
                        {
                            _isWrongHandAlertActive = true;
                            OnWrongHandDetected?.Invoke(IsLeftHandDominant);
                        }
                    }
                    
                    // Force the hand lost logic to run immediately (no grace period)
                    if (IsHandDetected)
                    {
                        IsHandDetected = false;
                        IsPinching = false;
                        IsIndexPinching = false;
                        IsMiddlePinching = false;
                        _landmarkFilter?.Reset();
                        for (int i = 0; i < 5; i++) _visualFilters[i]?.Reset();
                        _handLostGraceTimer = 0f;
                        OnHandLost?.Invoke();
                    }
                    return;
                }
                else
                {
                    // Correct hand is detected — clear wrong hand alert instantly
                    _continuousWrongHandTimer = 0f;
                    if (_isWrongHandAlertActive)
                    {
                        _isWrongHandAlertActive = false;
                        OnWrongHandDismissed?.Invoke();
                    }
                }

                ProcessHandLandmarks();

                if (!IsHandDetected)
                {
                    IsHandDetected = true;
                    OnHandDetected?.Invoke();
#if UNITY_EDITOR
                    Debug.Log($"[HandTracking] Hand detected! ({ (IsRightHand ? "Right" : "Left") })");
#endif
                }
            }
            else
            {
                // No hand detected (or grace period) — clear wrong hand alert instantly
                _continuousWrongHandTimer = 0f;
                if (_isWrongHandAlertActive)
                {
                    _isWrongHandAlertActive = false;
                    OnWrongHandDismissed?.Invoke();
                }

                // GRACE PERIOD: Don't immediately kill tracking.
                // MediaPipe fails when the hand is partially at the frame edge.
                // We allow a short grace window so brief drops don't terminate drawing.
                if (IsHandDetected)
                {
                    float dt = Time.time - _currentFrameCaptureTime;
                    if (dt <= 0f || dt > 0.1f) dt = 1f / 30f;

                    _handLostGraceTimer += dt;

                    if (_handLostGraceTimer >= HAND_LOST_GRACE_DURATION)
                    {
                        // Grace expired — the hand is truly gone
                        IsHandDetected = false;
                        IsPinching = false;
                        IsIndexPinching = false;
                        IsMiddlePinching = false;
                        _landmarkFilter?.Reset();
                        for (int i = 0; i < 5; i++) _visualFilters[i]?.Reset();
                        _handLostGraceTimer = 0f;
                        OnHandLost?.Invoke();
                    }
                }
            }
        }
        }

        // --- CACHED SCREEN MATH FOR ZERO-ALLOC LANDMARK PROJECTION ---
        private float _lastScreenWidth = -1f;
        private float _lastScreenHeight = -1f;
        private int _lastImgWidth = -1;
        private float _cachedCropX;
        private float _cachedCropY;
        private float _cachedVisFracX;
        private float _cachedVisFracY;

        private void RecalculateScreenMath()
        {
            if (_capturedImageWidth <= 0 || _capturedImageHeight <= 0) return;

            float screenW = Screen.width;
            float screenH = Screen.height;

            if (screenW == _lastScreenWidth && screenH == _lastScreenHeight && _capturedImageWidth == _lastImgWidth) return;
            
            _lastScreenWidth = screenW;
            _lastScreenHeight = screenH;
            _lastImgWidth = _capturedImageWidth;

            float imgW = _capturedImageHeight; // landscape height → portrait width
            float imgH = _capturedImageWidth;  // landscape width  → portrait height

            float scale = Mathf.Max(screenW / imgW, screenH / imgH);
            float scaledW = imgW * scale;
            float scaledH = imgH * scale;

            _cachedCropX = (scaledW - screenW) / (2f * scaledW);
            _cachedCropY = (scaledH - screenH) / (2f * scaledH);
            _cachedVisFracX = screenW / scaledW;
            _cachedVisFracY = screenH / scaledH;
        }

        /// <summary>
        /// Converts normalized MediaPipe landmark coordinates (landscape image space)
        /// to correct screen coordinates, accounting for the 90° CW rotation AND
        /// the aspect-ratio crop that AR Foundation applies when displaying the
        /// camera feed (scale-and-crop to fill the screen).
        /// </summary>
        private Vector2 LandmarkToScreenPoint(float landmarkX, float landmarkY)
        {
            // Step A: Rotate 90° CW (landscape sensor → portrait display)
            float rotatedX = 1f - landmarkY;  // landscape Y → portrait X (mirrored)
            float rotatedY = 1f - landmarkX;  // landscape X → portrait Y (mirrored)

            if (_capturedImageWidth > 0 && _capturedImageHeight > 0)
            {
                RecalculateScreenMath();
                
                // Map from image-normalized coords to screen coords using pre-calculated bounds (0 Alloc, 8x faster)
                float screenX = (rotatedX - _cachedCropX) / _cachedVisFracX * _lastScreenWidth;
                float screenY = (rotatedY - _cachedCropY) / _cachedVisFracY * _lastScreenHeight;

                return new Vector2(screenX, screenY);
            }

            // Fallback: no image dimensions yet
            return new Vector2(rotatedX * Screen.width, rotatedY * Screen.height);
        }

        private bool IsFingerExtended(int tipIndex, int pipIndex, float lenientMultiplier = 0.92f)
        {
            var landmarks = _result.handLandmarks[0];
            var wrist = landmarks.landmarks[WRIST];
            var tip = landmarks.landmarks[tipIndex];
            var pip = landmarks.landmarks[pipIndex];

            // OPTIMIZATION: Zero-allocation distance math directly on floats
            float dxTip = tip.x - wrist.x;
            float dyTip = tip.y - wrist.y;
            float tipDistSq = dxTip * dxTip + dyTip * dyTip;

            float dxPip = pip.x - wrist.x;
            float dyPip = pip.y - wrist.y;
            float pipDistSq = dxPip * dxPip + dyPip * dyPip;

            float modifiedPipDist = Mathf.Sqrt(pipDistSq) * lenientMultiplier;
            return tipDistSq > (modifiedPipDist * modifiedPipDist);
        }

        private bool IsThumbExtendedCheck()
        {
            var landmarks = _result.handLandmarks[0];
            var wrist = landmarks.landmarks[WRIST];
            var thumbTipLm = landmarks.landmarks[THUMB_TIP];
            var thumbIp = landmarks.landmarks[THUMB_IP];

            // OPTIMIZATION: Zero-allocation distance math directly on floats
            float dxTip = thumbTipLm.x - wrist.x;
            float dyTip = thumbTipLm.y - wrist.y;
            float tipDistSq = dxTip * dxTip + dyTip * dyTip;

            float dxIp = thumbIp.x - wrist.x;
            float dyIp = thumbIp.y - wrist.y;
            float ipDistSq = dxIp * dxIp + dyIp * dyIp;

            float modifiedIpDist = Mathf.Sqrt(ipDistSq) * 0.90f;
            return tipDistSq > (modifiedIpDist * modifiedIpDist);
        }

        private bool IsFingerCurled2D(int mcpIdx, int pipIdx, int tipIdx)
        {
            var landmarks = _result.handLandmarks[0].landmarks;
            
            // Correct for screen aspect ratio so angles are measured in physical square space
            float aspect = Screen.width / (float)Screen.height;
            
            float mcpX = landmarks[mcpIdx].x * aspect;
            float mcpY = landmarks[mcpIdx].y;
            
            float pipX = landmarks[pipIdx].x * aspect;
            float pipY = landmarks[pipIdx].y;
            
            float tipX = landmarks[tipIdx].x * aspect;
            float tipY = landmarks[tipIdx].y;

            // Zero-allocation vector math
            float bone1X = pipX - mcpX;
            float bone1Y = pipY - mcpY;
            
            float bone2X = tipX - pipX;
            float bone2Y = tipY - pipY;
            
            float bone1SqMag = bone1X * bone1X + bone1Y * bone1Y;
            float bone2SqMag = bone2X * bone2X + bone2Y * bone2Y;
            
            if (bone1SqMag == 0 || bone2SqMag == 0) return false;

            float invMag1 = 1f / Mathf.Sqrt(bone1SqMag);
            float invMag2 = 1f / Mathf.Sqrt(bone2SqMag);

            // Dot product evaluates the 2D angle between the bones.
            // 1 = perfectly straight, 0 = 90 degree bend, -1 = curled back on itself.
            // We use < 0.25f (approx > 75 degree bend) to confirm an unmistakable, deliberate curl.
            float dot = (bone1X * invMag1) * (bone2X * invMag2) + (bone1Y * invMag1) * (bone2Y * invMag2);
            return dot < 0.25f; 
        }

        private void ProcessHandLandmarks()
        {
            var landmarks = _result.handLandmarks[0];

            // Get index fingertip (landmark 8) and thumb tip (landmark 4)
            var indexTip = landmarks.landmarks[INDEX_FINGERTIP];
            var thumbTip = landmarks.landmarks[THUMB_TIP];

            // ── Step 1: Compute raw midpoint (pinch contact point) ──
            float rawMidX = (indexTip.x + thumbTip.x) / 2f;
            float rawMidY = (indexTip.y + thumbTip.y) / 2f;

            // ── Step 2: One Euro filter on normalized landmarks ──
            float t = _currentFrameCaptureTime; // Fix: Use capture time for stable filter interval & lag correction
            Vector2 filteredMid = _landmarkFilter.Filter(new Vector2(rawMidX, rawMidY), t);

            // ── Step 3: Convert to screen coords (with aspect-ratio correction) ──
            Vector2 screenPt = LandmarkToScreenPoint(filteredMid.x, filteredMid.y);
            _midScreenPosition = screenPt; // Fix: Save screen point for velocity

            // ── Step 4: Project to world space ──
            // Scale MediaPipe's relative wrist Z value to approximate real-world meters and apply to the base depth.
            // This grants the pinch midpoint genuine varying depth as the hand moves toward or away from the camera.
            float dynamicDepth = drawDistance + (landmarks.landmarks[WRIST].z * 1.5f);
            _currentDynamicDepth = dynamicDepth; // Fix: Save depth for projection
            FingertipWorldPosition = arCamera.ScreenToWorldPoint(new Vector3(screenPt.x, screenPt.y, dynamicDepth));

            OnFingertipMoved?.Invoke(FingertipWorldPosition);

            // ── Step 5: Compute all 5 fingertip world positions (for visualizer) ──
            // Applying decoupled OneEuro filtering to absorb static optical noise while preserving zero-lag motion
            for (int i = 0; i < 5; i++)
            {
                var tip = landmarks.landmarks[FINGERTIP_INDICES[i]];
                Vector2 rawTip = new Vector2(tip.x, tip.y);
                Vector2 filteredTip = _visualFilters[i] == null ? rawTip : _visualFilters[i].Filter(rawTip, t);
                
                Vector2 tipScreen = LandmarkToScreenPoint(filteredTip.x, filteredTip.y);
                FingertipScreenPositions[i] = tipScreen;
                FingertipWorldPositions[i] = arCamera.ScreenToWorldPoint(new Vector3(tipScreen.x, tipScreen.y, drawDistance));
            }

            // Calculate screen velocities for prediction
            float dt = t - _lastLandmarkUpdateTime;
            if (dt > 0f && dt < 0.1f)
            {
                Vector2 rawMidVel = (screenPt - _prevMidScreenPosition) / dt;
                _midScreenVelocity = Vector2.Lerp(_midScreenVelocity, rawMidVel, 0.45f); // Smooth midpoint velocity
                _prevMidScreenPosition = screenPt;

                for (int i = 0; i < 5; i++)
                {
                    Vector2 rawVel = (FingertipScreenPositions[i] - _prevScreenPositions[i]) / dt;
                    _screenVelocities[i] = Vector2.Lerp(_screenVelocities[i], rawVel, 0.45f); // Smooth fingertips velocity
                    _prevScreenPositions[i] = FingertipScreenPositions[i];
                }
            }
            _lastLandmarkUpdateTime = t;

            // ── Step 6: Palm center & normal (for arc menu positioning) ──
            var wrist = landmarks.landmarks[WRIST];
            var middleMcp = landmarks.landmarks[MIDDLE_MCP];
            var indexMcp = landmarks.landmarks[INDEX_MCP];
            var pinkyMcp = landmarks.landmarks[PINKY_MCP];

            // Palm center: midpoint of wrist and middle MCP, projected to world space
            float palmMidX = (wrist.x + middleMcp.x) / 2f;
            float palmMidY = (wrist.y + middleMcp.y) / 2f;
            Vector2 palmScreen = LandmarkToScreenPoint(palmMidX, palmMidY);
            PalmCenter = arCamera.ScreenToWorldPoint(new Vector3(palmScreen.x, palmScreen.y, drawDistance));

            // Palm normal: cross product of (indexMCP - wrist) × (pinkyMCP - wrist)
            // Uses screen-projected world positions for more stable normal
            Vector2 wristScreen = LandmarkToScreenPoint(wrist.x, wrist.y);
            Vector3 wristWorldPos = arCamera.ScreenToWorldPoint(new Vector3(wristScreen.x, wristScreen.y, drawDistance));

            Vector2 idxMcpScreen = LandmarkToScreenPoint(indexMcp.x, indexMcp.y);
            Vector3 indexMcpWorld = arCamera.ScreenToWorldPoint(new Vector3(idxMcpScreen.x, idxMcpScreen.y, drawDistance));

            Vector2 pinkyMcpScreen = LandmarkToScreenPoint(pinkyMcp.x, pinkyMcp.y);
            Vector3 pinkyMcpWorld = arCamera.ScreenToWorldPoint(new Vector3(pinkyMcpScreen.x, pinkyMcpScreen.y, drawDistance));

            Vector3 v1 = (indexMcpWorld - wristWorldPos).normalized;
            Vector3 v2 = (pinkyMcpWorld - wristWorldPos).normalized;
            Vector3 cross = Vector3.Cross(v1, v2).normalized;
            
            // Cross(Index-Wrist, Pinky-Wrist) — the direction depends on handedness and camera orientation.
            // Empirically verified: for rear-camera AR, the normal must be flipped from the original assumption.
            PalmNormal = IsRightHand ? -cross : cross;


            // ── Step 8: Individual Finger Extended State ──
            IsIndexExtended = IsFingerExtended(INDEX_FINGERTIP, INDEX_PIP);
            IsMiddleExtended = IsFingerExtended(MIDDLE_FINGERTIP, MIDDLE_PIP);
            
            // Ring/pinky naturally curl during gestures — use very lenient threshold
            IsRingExtended = IsFingerExtended(RING_FINGERTIP, RING_PIP, 0.88f);
            IsPinkyExtended = IsFingerExtended(PINKY_FINGERTIP, PINKY_PIP, 0.88f);
            IsThumbExtendedState = IsThumbExtendedCheck();

            // Only perfectly flat hands trigger the Menu Open logic downstream.
            IsAllFingersExtended = IsIndexExtended && IsMiddleExtended && IsRingExtended && IsPinkyExtended && IsThumbExtendedState;

            // ── Step 9: Pinch detection using Filtered 2D Screen Space ──
            // Using FingertipScreenPositions instead of raw landmarks prevents "flickering" 
            // pinch states caused by optical noise.
            Vector2 thumbScreen = FingertipScreenPositions[0];
            Vector2 indexScreen = FingertipScreenPositions[1];
            Vector2 middleScreen = FingertipScreenPositions[2];

            // OPTIMIZATION: Use SqrMagnitude to avoid extremely heavy CPU Square Roots in the main tracking loop.
            float pixelPinchDistSq = (indexScreen - thumbScreen).sqrMagnitude;
            float pixelMiddleDistSq = (middleScreen - thumbScreen).sqrMagnitude;

            // Wide, reliable thresholds (12% enter, 15% exit)
            // We Square the thresholds as well so we can compare Squared vs Squared
            float baseEnter = Screen.width * pinchEnterThreshold;
            float baseEnterSq = baseEnter * baseEnter;
            
            float baseExit = Screen.width * pinchExitThreshold;
            float baseExitSq = baseExit * baseExit;
            
            // Widened Hysteresis: make it very "sticky" to prevent accidental releases.
            float indexEnterSq = baseEnterSq;
            float indexExitSq = baseExitSq; 

            // Middle finger is naturally physically further/longer; require a more relaxed 
            // gap to account for natural movement.
            // Square the modified threshold. (A * 1.35)^2 = A^2 * 1.35^2 = A^2 * 1.8225
            float middleEnterSq = baseEnterSq * 1.8225f; 
            float middleExitSq = baseExitSq * 2.1025f; // (1.45 * 1.45)

            // MiddleThumbDistance is expected externally as normalized real distance, we just do one single SQRT here for external scripts
            MiddleThumbDistance = Mathf.Sqrt(pixelMiddleDistSq) / Screen.width; 

            // When menu is CLOSED: require ring/pinky extended to prevent false drawing pinches.
            // When menu is OPEN: bypass the check — taps must work regardless of finger posture.
            // CRITICAL: When ALREADY PINCHING, bypass completely — curling ring/pinky during a fast
            // stroke is natural biomechanics and must NEVER terminate an active stroke mid-draw.
            bool interactionPostureOk = IsMenuActive || IsIndexPinching || IsMiddlePinching
                                        || IsRingExtended || IsPinkyExtended;

            // ── HYBRID PINCH DETECTION: The mathematical 2D Pose Fix ──
            // Camera foreshortening causes AI to hallucinate the fingertip dot far from the thumb.
            // Z-depth is heavily unreliable. Instead, we compute EXACT 2D BONE ANGLES from the screen.
            // A distinct pinch occurs when ONE specific finger is curled (>75deg bend) while others remain extended.
            
            bool indexCurled2D = IsFingerCurled2D(INDEX_MCP, INDEX_PIP, INDEX_FINGERTIP);
            bool middleCurled2D = IsFingerCurled2D(MIDDLE_MCP, MIDDLE_PIP, MIDDLE_FINGERTIP);

            // Isolate the pinch: ensure we are intentionally pointing a single finger, not just closing a fist.
            bool isIndexPinchPose = indexCurled2D && (IsMiddleExtended || IsRingExtended || IsPinkyExtended);
            bool isMiddlePinchPose = middleCurled2D && (IsIndexExtended || IsRingExtended || IsPinkyExtended);

            // A tap happens if:
            // 1. Primary check: strict 2D pixel distance is very close
            // OR 
            // 2. Backup check: deliberate pinch pose detected, and fingers are within a MUCH wider radius
            float maxGenerousDistSq = (baseEnter * 3.0f) * (baseEnter * 3.0f); // Allows up to ~36% of screen width drift if posed perfectly.

            bool indexScreenPinch = pixelPinchDistSq < indexEnterSq;
            bool indexHybridPinch = indexScreenPinch || (pixelPinchDistSq < maxGenerousDistSq && isIndexPinchPose);

            bool middleScreenPinch = pixelMiddleDistSq < middleEnterSq;
            bool middleHybridPinch = middleScreenPinch || (pixelMiddleDistSq < maxGenerousDistSq && isMiddlePinchPose);

            if (IsIndexPinching)
            {
                // To exit: Must break BOTH the wide distance threshold AND drop the distinct pinch pose.
                float exitGenerousDistSq = indexExitSq * 9.0f; // (indexExit * 3.0f)^2
                if ((pixelPinchDistSq > indexExitSq && !isIndexPinchPose) || pixelPinchDistSq > exitGenerousDistSq || !interactionPostureOk)
                    IsIndexPinching = false;
            }
            else
            {
                if (indexHybridPinch && interactionPostureOk)
                    IsIndexPinching = true;
            }
            
            if (IsMiddlePinching)
            {
                float middleExitGenerousDistSq = middleExitSq * 9.0f; // (middleExit * 3.0f)^2
                if ((pixelMiddleDistSq > middleExitSq && !isMiddlePinchPose) || pixelMiddleDistSq > middleExitGenerousDistSq || !interactionPostureOk)
                    IsMiddlePinching = false;
            }
            else
            {
                if (middleHybridPinch && interactionPostureOk)
                    IsMiddlePinching = true;
            }

            IsPinching = IsIndexPinching; // Expose as generic event primarily for drawing

            // ── Step 10: Z-Depth Palm Orientation Check ──
            // Uses the raw MediaPipe Z-depth to defeat the "back of hand" 2D silhouette bug.
            // 1. Compute the proper 3D cross-product normal in MediaPipe's local space.
            // 2. Check if the middle knuckle is physically pushed forward (lower Z) relative to the wrist's center of mass.
            Vector3 wristRaw = new Vector3(wrist.x, wrist.y, wrist.z);
            Vector3 indexMcpRaw = new Vector3(indexMcp.x, indexMcp.y, indexMcp.z);
            Vector3 pinkyMcpRaw = new Vector3(pinkyMcp.x, pinkyMcp.y, pinkyMcp.z);

            Vector3 rawV1 = (indexMcpRaw - wristRaw).normalized;
            Vector3 rawV2 = (pinkyMcpRaw - wristRaw).normalized;
            Vector3 rawNormal = Vector3.Cross(rawV1, rawV2).normalized;

            // Flip for Left Hand so the normal always points OUT of the palm.
            Vector3 truePalmNormal = IsRightHand ? rawNormal : -rawNormal;

            // In MediaPipe, Z points AWAY from the camera. A negative Z means it's pointing AT the camera.
            // We also enforce the promised knuckle depth rule (allowing a generous margin for pitched hands).
            IsHandFacingCamera = (truePalmNormal.z < 0.15f) && (middleMcp.z < wrist.z + 0.20f);
        }

        public Vector2 GetPredictedScreenPosition(int fingerIndex)
        {
            float timeSinceUpdate = Time.time - _lastLandmarkUpdateTime;
            // Clamp prediction time to 100ms to allow fully absorbing pipeline delays while preventing runaway extrapolation.
            float predictionTime = Mathf.Clamp(timeSinceUpdate, 0f, 0.10f);

            // Fix: Damp prediction if data becomes stale (e.g., during Grace Period frame drops)
            float stalenessDamp = 1f;
            if (timeSinceUpdate > 0.05f)
            {
                stalenessDamp = Mathf.Clamp01(1f - (timeSinceUpdate - 0.05f) * 12f); // Decays to 0 over ~83ms
            }

            return FingertipScreenPositions[fingerIndex] + (_screenVelocities[fingerIndex] * predictionTime * stalenessDamp * 0.25f);
        }

        public Vector3 GetPredictedWorldPosition()
        {
            float timeSinceUpdate = Time.time - _lastLandmarkUpdateTime;
            float predictionTime = Mathf.Clamp(timeSinceUpdate, 0f, 0.10f);

            float stalenessDamp = 1f;
            if (timeSinceUpdate > 0.05f)
            {
                stalenessDamp = Mathf.Clamp01(1f - (timeSinceUpdate - 0.05f) * 12f);
            }

            // Project the predicted middle string position dynamically.
            Vector2 predictedMid = _midScreenPosition + (_midScreenVelocity * predictionTime * stalenessDamp * 0.25f);
            return arCamera.ScreenToWorldPoint(new Vector3(predictedMid.x, predictedMid.y, _currentDynamicDepth));
        }

        // ── Public API for UI ──
        /// <summary>
        /// Sets the hand tracking sensitivity/smoothing level based on a 5-step UI slider.
        /// Level 0: Heaviest Smoothing (Very Low Sensitivity)
        /// Level 2: Default Smoothing (Medium Sensitivity)
        /// Level 4: Raw Tracking (Very High Sensitivity)
        /// </summary>
        public void SetSensitivityLevel(int level)
        {
            _sensitivityLevel = Mathf.Clamp(level, 0, 4);
#if UNITY_EDITOR
            Debug.Log($"[HandTracking] Sensitivity Level set to {_sensitivityLevel}");
#endif
        }

        /// <summary>
        /// Update which hand the system actively tracks and responds to.
        /// </summary>
        public void SetDominantHand(bool isLeft)
        {
            IsLeftHandDominant = isLeft;
#if UNITY_EDITOR
            Debug.Log($"[HandTracking] Dominant hand set to: {(isLeft ? "Left" : "Right")}");
#endif
        }
    
        void OnDestroy()
        {
            _handLandmarker?.Close();
            if (_processingTexture != null) Destroy(_processingTexture);

            // Shutdown GPU Manager when destroying the script
            if (GpuManager.IsInitialized)
            {
                GpuManager.Shutdown();
            }
        }
    }
}
