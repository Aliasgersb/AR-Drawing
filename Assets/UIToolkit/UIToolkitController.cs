using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using UnityEngine.XR.ARFoundation;
using System.IO;

namespace SpatialDrawing.UI
{
    /// <summary>
    /// Controls the entire UI Toolkit interface for the Spatial Drawing app.
    /// Attach this to the same GameObject as the UIDocument component.
    /// Updated for Figma-matched UI layout.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class UIToolkitController : MonoBehaviour
    {
        // ── Public Events (other systems listen to these) ──
        public System.Action<Color> OnColorChanged;
        public System.Action<float> OnBrushSizeChanged;
        public System.Action OnUndoPressed;
        public System.Action OnRedoPressed;
        public System.Action OnSavePressed;

        // ── Internal State ──
        private VisualElement root;
        private VisualElement onboardingScreen;
        private VisualElement mainHUDScreen;
        private VisualElement galleryScreen;
        private VisualElement settingsScreen;
        private VisualElement inspectorScreen;

        private VisualElement selectedColorDot;
        private VisualElement selectedBrushDot;
        private VisualElement selectedFilterPill;

        private Color currentColor = Color.white;
        private float currentBrushSize = 0.005f;

        // Color name → actual Color
        private readonly Dictionary<string, Color> colorMap = new()
        {
            { "color-white",  Color.white },
            { "color-cyan",   new Color(0f, 0.706f, 0.847f) },
            { "color-orange", new Color(1f, 0.620f, 0.173f) },
            { "color-green",  new Color(0f, 0.898f, 0.627f) },
            { "color-purple", new Color(0.659f, 0.333f, 0.969f) },
            { "color-pink",   new Color(0.957f, 0.247f, 0.369f) },
        };

        // Brush name → thickness in world units
        private readonly Dictionary<string, float> brushMap = new()
        {
            { "brush-small",  0.005f },
            { "brush-medium", 0.012f },
            { "brush-large",  0.022f },
        };

        // ── PlayerPrefs Keys ──
        private const string PREF_SENSITIVITY    = "setting_sensitivity";    // int 0–4
        private const string PREF_DOMINANT_HAND  = "setting_dominant_hand";  // int 0=Right 1=Left
        private const string PREF_HAPTICS        = "setting_haptics";        // int 0/1
        private const string PREF_TRACKING_DOTS  = "setting_tracking_dots";  // int 0/1
        private const string PREF_FLASHLIGHT     = "setting_flashlight";     // int 0/1
        private const string PREF_STABILIZATION  = "setting_stabilization";  // int 0/1

        // Single source of truth for the user's flashlight intent
        private bool _isFlashlightIntentOn = false;

        // Cached UI elements needed for LoadSettings
        private VisualElement _sensitivityFill;
        private VisualElement _sensitivityThumb;
        private Label _sensitivityDesc;
        private VisualElement _segLeft;
        private VisualElement _segRight;
        private VisualElement _toggleHaptics;
        private VisualElement _toggleTrackingDots;
        private VisualElement _toggleFlashlight;
        private VisualElement _toggleStabilization;

        // ════════════════════════════════════
        //  INITIALIZATION
        // ════════════════════════════════════
        void OnEnable()
        {
            var doc = GetComponent<UIDocument>();
            root = doc.rootVisualElement;

            if (FindAnyObjectByType<OnScreenLogger>() == null)
            {
                gameObject.AddComponent<OnScreenLogger>();
            }

            // Cache screens
            onboardingScreen = root.Q("onboarding-screen");
            mainHUDScreen    = root.Q("mainhud-screen");
            galleryScreen    = root.Q("gallery-screen");
            settingsScreen   = root.Q("settings-screen");
            inspectorScreen  = root.Q("inspector-screen");

            // Cache settings UI elements needed for load
            _sensitivityFill  = root.Q("sensitivity-fill");
            _sensitivityThumb = root.Q("sensitivity-thumb");
            _sensitivityDesc  = root.Q<Label>("sensitivity-desc");
            _segLeft          = root.Q("seg-left");
            _segRight         = root.Q("seg-right");
            _toggleHaptics      = root.Q("toggle-haptics");
            _toggleTrackingDots = root.Q("toggle-tracking-dots");
            _toggleFlashlight   = root.Q("toggle-flashlight");
            _toggleStabilization = root.Q("toggle-stabilization");

            // Wire buttons
            SetupOnboarding();
            SetupMainHUD();
            SetupGallery();
            SetupSettings();
            SetupInspector();

            // Restore all saved settings on startup
            LoadSettings();

            // Start on Onboarding
            ShowScreen(onboardingScreen);
        }

        // ════════════════════════════════════
        //  SCREEN NAVIGATION
        // ════════════════════════════════════
        private void ShowScreen(VisualElement target)
        {
            onboardingScreen.style.display = DisplayStyle.None;
            mainHUDScreen.style.display    = DisplayStyle.None;
            galleryScreen.style.display    = DisplayStyle.None;
            settingsScreen.style.display   = DisplayStyle.None;
            if (inspectorScreen != null) inspectorScreen.style.display = DisplayStyle.None;

            target.style.display = DisplayStyle.Flex;

            // Re-evaluate physical flashlight state on screen transition
            UpdatePhysicalFlashlightState();

            // Pause camera frame processing when any full-screen UI is visible.
            // This prevents the expensive YUV→RGBA + MediaPipe inference from
            // competing with UI rendering and causing laggy scrolling.
            bool isHeavyUI = (target != mainHUDScreen);
            var handTracker = Object.FindAnyObjectByType<HandTrackingManager>();
            if (handTracker != null) handTracker.IsUIBlocking = isHeavyUI;

            // ── EXCLUDE 3D RAYCAST BLOCKING ──
            GameObject menuInstance = GameObject.Find("FloatingMenuCanvas_Runtime");
            if (menuInstance != null)
            {
                menuInstance.SetActive(!isHeavyUI);
            }

            if (target == galleryScreen) PopulateSavedDrawings();
        }

        // ════════════════════════════════════
        //  ONBOARDING
        // ════════════════════════════════════
        private void SetupOnboarding()
        {
            var btnStart = root.Q("btn-start");
            btnStart?.RegisterCallback<ClickEvent>(evt =>
            {
                ShowScreen(mainHUDScreen); // Go straight to AR Drawing
                Debug.Log("[UI] Start Creating → MainHUD (AR Screen)");
            });
        }

        // ════════════════════════════════════
        //  MAIN HUD
        // ════════════════════════════════════
        private void SetupMainHUD()
        {
            // Navigation
            root.Q("btn-gallery")?.RegisterCallback<ClickEvent>(evt => ShowScreen(galleryScreen));
            root.Q("btn-settings")?.RegisterCallback<ClickEvent>(evt => ShowScreen(settingsScreen));

            // Color dots
            foreach (var kvp in colorMap)
            {
                string dotName = kvp.Key;
                Color color = kvp.Value;
                var dot = root.Q(dotName);
                if (dot == null) continue;

                if (dotName == "color-white")
                    selectedColorDot = dot;

                dot.RegisterCallback<ClickEvent>(evt =>
                {
                    SelectColor(dot, color);
                });
            }

            // Brush size dots
            foreach (var kvp in brushMap)
            {
                string dotName = kvp.Key;
                float size = kvp.Value;
                var dot = root.Q(dotName);
                if (dot == null) continue;

                if (dotName == "brush-small")
                    selectedBrushDot = dot;

                dot.RegisterCallback<ClickEvent>(evt =>
                {
                    SelectBrush(dot, size);
                });
            }

            // Tools
            root.Q("btn-undo")?.RegisterCallback<ClickEvent>(evt =>
            {
                OnUndoPressed?.Invoke();
                Debug.Log("[UI] Undo pressed");
            });

            root.Q("btn-redo")?.RegisterCallback<ClickEvent>(evt =>
            {
                OnRedoPressed?.Invoke();
                Debug.Log("[UI] Redo pressed");
            });

            root.Q("btn-save")?.RegisterCallback<ClickEvent>(evt =>
            {
                OnSavePressed?.Invoke();
                Debug.Log("[UI] Save pressed");
            });
        }

        // ════════════════════════════════════
        //  GALLERY
        // ════════════════════════════════════
        private void SetupGallery()
        {
            // Back button
            root.Q("btn-gallery-back")?.RegisterCallback<ClickEvent>(evt =>
            {
                // FAIL-SAFE: Ensure inspector is closed if we somehow leave the gallery while an inspected drawing is active
                var inspector = Object.FindAnyObjectByType<DrawingInspector>();
                if (inspector != null && inspector.IsInspecting) inspector.CloseInspector();

                ShowScreen(mainHUDScreen);
                Debug.Log("[UI] Gallery → Back to MainHUD");
            });

            // New Drawing FAB → go to MainHUD (start drawing)
            root.Q("btn-new-drawing")?.RegisterCallback<ClickEvent>(evt =>
            {
                ShowScreen(mainHUDScreen);
                Debug.Log("[UI] New Drawing → MainHUD");
            });

            // Filter pills
            var filterAll = root.Q("filter-all");
            var filterRecent = root.Q("filter-recent");
            var filterFavorites = root.Q("filter-favorites");

            selectedFilterPill = filterAll; // Default active

            filterAll?.RegisterCallback<ClickEvent>(evt => SelectFilter(filterAll, "All Works"));
            filterRecent?.RegisterCallback<ClickEvent>(evt => SelectFilter(filterRecent, "Recent"));
            filterFavorites?.RegisterCallback<ClickEvent>(evt => SelectFilter(filterFavorites, "Favorites"));
        }

        private void SelectFilter(VisualElement pill, string filterName)
        {
            selectedFilterPill?.RemoveFromClassList("filter-pill--active");
            pill.AddToClassList("filter-pill--active");
            selectedFilterPill = pill;
            Debug.Log($"[UI] Filter: {filterName}");
        }

        private void SetupInspector()
        {
            root.Q("btn-inspector-back")?.RegisterCallback<ClickEvent>(evt =>
            {
                // FIX #6: Close inspector FIRST before navigating to gallery.
                // ShowScreen(galleryScreen) calls PopulateSavedDrawings() which iterates
                // all DrawingLine objects. If inspector linesis not yet destroyed the
                // gallery populate pass includes them, causing logic errors.
                var inspector = Object.FindAnyObjectByType<DrawingInspector>();
                if (inspector != null) inspector.CloseInspector();

                ShowScreen(galleryScreen);
                Debug.Log("[UI] Inspector → Back to Gallery");
            });

            // NEW: Summon to AR button
            root.Q("btn-summon-ar")?.RegisterCallback<ClickEvent>(evt =>
            {
                var inspector = Object.FindAnyObjectByType<DrawingInspector>();
                if (inspector != null) inspector.SummonToAR();

                // Go back to the main AR view
                ShowScreen(mainHUDScreen);
                Debug.Log("[UI] Inspector → Summoned to AR, switched to MainHUD");
            });
        }

        // ════════════════════════════════════
        //  SETTINGS
        // ════════════════════════════════════
        private void SetupSettings()
        {
            // Back button
            root.Q("btn-settings-back")?.RegisterCallback<ClickEvent>(evt =>
            {
                HapticService.Light(); // Back button click
                ShowScreen(mainHUDScreen);
                Debug.Log("[UI] Settings → Back to MainHUD");
            });

            // Custom toggles (click to toggle on/off class)
            SetupCustomToggle("toggle-haptics", "Haptic Feedback");
            SetupCustomToggle("toggle-tracking-dots", "Show Tracking Dots");
            SetupCustomToggle("toggle-flashlight", "Flashlight / Torch");
            SetupCustomToggle("toggle-stabilization", "Camera Feed Stabilization");

            // Segmented controls (Dominant Hand)
            SetupSegmentedPair("seg-left", "seg-right", "Dominant Hand");

            // Sensitivity Slider (5-step snapping)
            SetupSensitivitySlider();

            // How It Works — gesture guide modal
            var gestureModalOverlay = root.Q("gesture-modal-overlay");
            root.Q("btn-how-it-works")?.RegisterCallback<ClickEvent>(evt =>
            {
                HapticService.Light();
                if (gestureModalOverlay != null)
                    gestureModalOverlay.style.display = DisplayStyle.Flex;
                Debug.Log("[UI] Gesture guide modal opened");
            });

            root.Q("btn-gesture-modal-close")?.RegisterCallback<ClickEvent>(evt =>
            {
                HapticService.Light();
                if (gestureModalOverlay != null)
                    gestureModalOverlay.style.display = DisplayStyle.None;
                Debug.Log("[UI] Gesture guide modal closed");
            });

            // Tap backdrop to dismiss modal too
            gestureModalOverlay?.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target == gestureModalOverlay)
                {
                    gestureModalOverlay.style.display = DisplayStyle.None;
                }
            });
        }

        private void SetupCustomToggle(string name, string label)
        {
            var track = root.Q(name);
            if (track == null) return;

            track.RegisterCallback<ClickEvent>(evt =>
            {
                bool isOn = track.ClassListContains("settings__toggle-track--on");
                if (isOn)
                    track.RemoveFromClassList("settings__toggle-track--on");
                else
                    track.AddToClassList("settings__toggle-track--on");

                bool newValue = !isOn;

                // Wire each toggle to its feature
                if (name == "toggle-haptics")
                    HapticService.SetEnabled(newValue);

                if (name == "toggle-tracking-dots")
                    SetTrackingDotsVisible(newValue);

                if (name == "toggle-flashlight")
                    SetFlashlightEnabled(newValue);

                if (name == "toggle-stabilization")
                    SetStabilizationEnabled(newValue);

                // Save immediately
                SaveSettings();

                // Light haptic feedback on toggle (will only fire if haptics are still on)
                HapticService.Light();

                Debug.Log($"[Settings] {label}: {newValue}");
            });
        }

        private void SetTrackingDotsVisible(bool visible)
        {
            var visualizer = Object.FindAnyObjectByType<ScreenSpaceFingertipVisualizer>();
            if (visualizer != null)
                visualizer.SetVisible(visible);
        }

        private void SetFlashlightEnabled(bool enabled)
        {
            // 1. Update authoritative intent state
            _isFlashlightIntentOn = enabled;

            // 2. Reflect intent in the UI toggle (just in case this was called programmatically)
            if (_toggleFlashlight != null)
            {
                if (enabled) _toggleFlashlight.AddToClassList("settings__toggle-track--on");
                else         _toggleFlashlight.RemoveFromClassList("settings__toggle-track--on");
            }

            // 3. Re-evaluate physical hardware state
            UpdatePhysicalFlashlightState();
        }

        private void SetStabilizationEnabled(bool enabled)
        {
            var camera = Camera.main;
            if (camera != null)
            {
                var stabilizer = camera.GetComponent<SpatialDrawing.CameraFX.CameraStabilizer>();
                if (stabilizer == null)
                {
                    stabilizer = camera.gameObject.AddComponent<SpatialDrawing.CameraFX.CameraStabilizer>();
                }
                stabilizer.SetStabilizationEnabled(enabled);
            }
            else
            {
                Debug.LogWarning("[Settings] Main Camera not found for stabilization toggle.");
            }
        }

        private void UpdatePhysicalFlashlightState()
        {
            // Evaluates the single source of truth vs the current screen state
            bool isMainHUD = (mainHUDScreen != null && mainHUDScreen.style.display == DisplayStyle.Flex);
            bool shouldPhysicalBeOn = _isFlashlightIntentOn && isMainHUD;

            ApplyPhysicalFlashlight(shouldPhysicalBeOn);
        }

        private void ApplyPhysicalFlashlight(bool turnOn)
        {
#if UNITY_ANDROID || UNITY_IOS
            try
            {
                // ARCore/ARKit holds an EXCLUSIVE hardware lock on the camera during AR sessions.
                // Raw Android Java reflection (getSystemService("camera")) will fail on most modern
                // devices because the camera is "in use" by another process (ARCore).
                // The ONLY reliable way to toggle the torch in AR is to go through ARFoundation.

                var arCamManager = Object.FindAnyObjectByType<UnityEngine.XR.ARFoundation.ARCameraManager>();
                if (arCamManager != null && arCamManager.subsystem != null)
                {
                    arCamManager.subsystem.requestedCameraTorchMode = turnOn 
                        ? UnityEngine.XR.ARSubsystems.XRCameraTorchMode.On 
                        : UnityEngine.XR.ARSubsystems.XRCameraTorchMode.Off;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Flashlight] ARFoundation torch toggle failed: {e.Message}");
            }
#else
            Debug.Log($"[Flashlight] Physical Torch {(turnOn ? "ON" : "OFF")} (simulated in Editor)");
#endif
        }

        // ════════════════════════════════════
        //  SEGMENTED CONTROLS LOGIC
        // ════════════════════════════════════

        private void SetupSegmentedPair(string name1, string name2, string settingName)
        {
            var seg1 = root.Q(name1);
            var seg2 = root.Q(name2);

            if (seg1 == null || seg2 == null) return;

            void Activate(VisualElement active, VisualElement inactive, bool isLeft)
            {
                HapticService.Light(); // Segment tap
                active.AddToClassList("settings__segment--active");
                inactive.RemoveFromClassList("settings__segment--active");

                // Route to correct manager method if this is the Dominant Hand segmented control
                if (settingName == "Dominant Hand")
                {
                    var handTracker = Object.FindAnyObjectByType<HandTrackingManager>();
                    if (handTracker != null) handTracker.SetDominantHand(isLeft);
                    SaveSettings();
                }
            }

            // Bind clicks. name1="Left", name2="Right"
            seg1.RegisterCallback<ClickEvent>(evt => Activate(seg1, seg2, true));
            seg2.RegisterCallback<ClickEvent>(evt => Activate(seg2, seg1, false));
        }

        // ════════════════════════════════════
        //  SENSITIVITY SLIDER (5-STEP)
        // ════════════════════════════════════
        private void SetupSensitivitySlider()
        {
            var track = root.Q("sensitivity-track");
            var fill = root.Q("sensitivity-fill");
            var thumb = root.Q("sensitivity-thumb");

            if (track == null || fill == null || thumb == null) return;

            // Optional description label
            var descLabel = root.Q<Label>("sensitivity-desc");
            string[] descTexts = { "Max Stable", "Stable", "Balanced", "Responsive", "Raw" };

            // Helper to update text
            void UpdateDescText(int step)
            {
                if (descLabel != null) descLabel.text = descTexts[Mathf.Clamp(step, 0, 4)];
            }

            // Initialize to Level 2 (50%)
            SetSliderVisuals(fill, thumb, 0.5f);
            UpdateDescText(2);

            bool isDragging = false;

            track.RegisterCallback<PointerDownEvent>(evt =>
            {
                isDragging = true;
                track.CapturePointer(evt.pointerId);
                UpdateSliderFromPointer(evt.localPosition, track, fill, thumb);
            });

            track.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (isDragging)
                {
                    UpdateSliderFromPointer(evt.localPosition, track, fill, thumb);
                }
            });

            track.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (isDragging)
                {
                    isDragging = false;
                    track.ReleasePointer(evt.pointerId);
                }
            });

            track.RegisterCallback<PointerCaptureOutEvent>(evt => 
            {
                isDragging = false;
            });
        }

        private void UpdateSliderFromPointer(Vector2 localPointerPos, VisualElement track, VisualElement fill, VisualElement thumb)
        {
            float width = track.layout.width;
            if (width <= 0) return;

            // Calculate raw percentage [0, 1]
            float rawPercent = Mathf.Clamp01(localPointerPos.x / width);

            // Calculate closest of the 5 steps: 0, 1, 2, 3, 4
            int step = Mathf.RoundToInt(rawPercent * 4f);
            
            // Convert step back to snapped percentage: 0%, 25%, 50%, 75%, 100%
            float snappedPercent = step / 4f;

            // Update UI visuals
            SetSliderVisuals(fill, thumb, snappedPercent);

            // Update Description Text
            var descLabel = track.parent.parent.Q<Label>("sensitivity-desc"); // Re-query just in case
            if (descLabel != null)
            {
                string[] descTexts = { "Max Stable", "Stable", "Balanced", "Responsive", "Raw" };
                descLabel.text = descTexts[step];
            }

            // Notify HandTrackingManager
            var handTracker = Object.FindAnyObjectByType<HandTrackingManager>();
            if (handTracker != null)
            {
                handTracker.SetSensitivityLevel(step);
            }

            // Persist
            SaveSettings();
        }

        private void SetSliderVisuals(VisualElement fill, VisualElement thumb, float percent)
        {
            fill.style.width = Length.Percent(percent * 100f);
            // Center the thumb over the fill edge
            thumb.style.left = Length.Percent(percent * 100f);
            // Note: In USS, the thumb should ideally have translate: -50% 0; to center properly on the 100% point.
        }

        // ════════════════════════════════════
        //  SETTINGS PERSISTENCE
        // ════════════════════════════════════

        private void SaveSettings()
        {
            // Sensitivity (read current step from fill visual)
            float fillWidth = _sensitivityFill?.style.width.value.value ?? 50f;
            int sensitivityStep = Mathf.RoundToInt((fillWidth / 100f) * 4f);
            PlayerPrefs.SetInt(PREF_SENSITIVITY, sensitivityStep);

            // Dominant Hand (0=Right, 1=Left)
            bool leftActive = _segLeft?.ClassListContains("settings__segment--active") ?? false;
            PlayerPrefs.SetInt(PREF_DOMINANT_HAND, leftActive ? 1 : 0);

            // Haptics
            bool hapticsOn = _toggleHaptics?.ClassListContains("settings__toggle-track--on") ?? true;
            PlayerPrefs.SetInt(PREF_HAPTICS, hapticsOn ? 1 : 0);

            // Show Tracking Dots
            bool dotsOn = _toggleTrackingDots?.ClassListContains("settings__toggle-track--on") ?? false;
            PlayerPrefs.SetInt(PREF_TRACKING_DOTS, dotsOn ? 1 : 0);

            // Flashlight
            // Only update PlayerPrefs, do NOT read UI state
            PlayerPrefs.SetInt(PREF_FLASHLIGHT, _isFlashlightIntentOn ? 1 : 0);

            // Stabilization
            bool stabOn = _toggleStabilization?.ClassListContains("settings__toggle-track--on") ?? false;
            PlayerPrefs.SetInt(PREF_STABILIZATION, stabOn ? 1 : 0);

            PlayerPrefs.Save();
        }

        private void LoadSettings()
        {
            // ── SENSITIVITY ──
            int sensitivity = PlayerPrefs.GetInt(PREF_SENSITIVITY, 2); // default: Balanced
            float sensitivityPercent = sensitivity / 4f;
            if (_sensitivityFill != null && _sensitivityThumb != null)
                SetSliderVisuals(_sensitivityFill, _sensitivityThumb, sensitivityPercent);
            if (_sensitivityDesc != null)
            {
                string[] descTexts = { "Max Stable", "Stable", "Balanced", "Responsive", "Raw" };
                _sensitivityDesc.text = descTexts[Mathf.Clamp(sensitivity, 0, 4)];
            }
            var ht = Object.FindAnyObjectByType<HandTrackingManager>();
            if (ht != null) ht.SetSensitivityLevel(sensitivity);

            // ── DOMINANT HAND ──
            int dominantHand = PlayerPrefs.GetInt(PREF_DOMINANT_HAND, 0); // default: Right
            bool isLeft = dominantHand == 1;
            if (isLeft)
            {
                _segLeft?.AddToClassList("settings__segment--active");
                _segRight?.RemoveFromClassList("settings__segment--active");
            }
            else
            {
                _segRight?.AddToClassList("settings__segment--active");
                _segLeft?.RemoveFromClassList("settings__segment--active");
            }
            if (ht != null) ht.SetDominantHand(isLeft);

            // ── HAPTICS ──
            int hapticsVal = PlayerPrefs.GetInt(PREF_HAPTICS, 1); // default: ON
            bool hapticsOn = hapticsVal == 1;
            if (hapticsOn) _toggleHaptics?.AddToClassList("settings__toggle-track--on");
            else            _toggleHaptics?.RemoveFromClassList("settings__toggle-track--on");
            HapticService.SetEnabled(hapticsOn);

            // ── TRACKING DOTS ──
            int dotsVal = PlayerPrefs.GetInt(PREF_TRACKING_DOTS, 0); // default: OFF
            bool dotsOn = dotsVal == 1;
            if (dotsOn) _toggleTrackingDots?.AddToClassList("settings__toggle-track--on");
            else         _toggleTrackingDots?.RemoveFromClassList("settings__toggle-track--on");
            // Delay apply so ScreenSpaceFingertipVisualizer has time to Start()
            StartCoroutine(ApplyTrackingDotsDelayed(dotsOn));

            // ── FLASHLIGHT ──
            int flashVal = PlayerPrefs.GetInt(PREF_FLASHLIGHT, 0); // default: OFF
            SetFlashlightEnabled(flashVal == 1); // This updates intent + UI + hardware state correctly

            // ── STABILIZATION ──
            int stabVal = PlayerPrefs.GetInt(PREF_STABILIZATION, 0); // default: OFF
            bool stabOn = stabVal == 1;
            if (stabOn) _toggleStabilization?.AddToClassList("settings__toggle-track--on");
            else         _toggleStabilization?.RemoveFromClassList("settings__toggle-track--on");
            StartCoroutine(ApplyStabilizationDelayed(stabOn));

            Debug.Log($"[Settings] Loaded — Sensitivity:{sensitivity} Hand:{(isLeft?"Left":"Right")} Haptics:{hapticsOn} Dots:{dotsOn} Flash:{_isFlashlightIntentOn} Stab:{stabOn}");
        }

        private System.Collections.IEnumerator ApplyStabilizationDelayed(bool enabled)
        {
            yield return null;
            SetStabilizationEnabled(enabled);
        }

        private System.Collections.IEnumerator ApplyTrackingDotsDelayed(bool visible)
        {
            // Wait one frame for all MonoBehaviours (including ScreenSpaceFingertipVisualizer) to Start()
            yield return null;
            SetTrackingDotsVisible(visible);
        }

        // ════════════════════════════════════
        //  COLOR & BRUSH SELECTION
        // ════════════════════════════════════
        private void SelectColor(VisualElement dot, Color color)
        {
            selectedColorDot?.RemoveFromClassList("color-dot--selected");
            dot.AddToClassList("color-dot--selected");
            selectedColorDot = dot;
            currentColor = color;
            OnColorChanged?.Invoke(color);
            Debug.Log($"[UI] Color changed to {color}");
        }

        private void SelectBrush(VisualElement dot, float size)
        {
            selectedBrushDot?.RemoveFromClassList("brush-dot--selected");
            dot.AddToClassList("brush-dot--selected");
            selectedBrushDot = dot;
            currentBrushSize = size;
            OnBrushSizeChanged?.Invoke(size);
            Debug.Log($"[UI] Brush size changed to {size}");
        }

        // ════════════════════════════════════
        //  PUBLIC API
        // ════════════════════════════════════
        public Color  GetCurrentColor()     => currentColor;
        public float  GetCurrentBrushSize() => currentBrushSize;
        public void   GoToMainHUD()         => ShowScreen(mainHUDScreen);
        public void   GoToGallery()         => ShowScreen(galleryScreen);
        public void   GoToSettings()        => ShowScreen(settingsScreen);
        public void   GoToOnboarding()      => ShowScreen(onboardingScreen);
        public void   GoToInspector()       => ShowScreen(inspectorScreen);

        private List<Texture2D> _loadedTextures = new List<Texture2D>();

        private void PopulateSavedDrawings()
        {
            var scroll = root.Q<VisualElement>("gallery-scroll");
            if (scroll == null) return;
            scroll.pickingMode = PickingMode.Position;

            VisualElement grid = scroll.Q(className: "gallery__grid");
            if (grid == null) return;
            grid.pickingMode = PickingMode.Position;

            // FIX #10: Use DestroyImmediate for deterministic cleanup.
            // Destroy() only schedules for cleanup — if PopulateSavedDrawings runs before
            // the GC frame the textures remain bound to now-removed Background Images.
            foreach (var t in _loadedTextures)
            {
                if (t != null) DestroyImmediate(t);
            }
            _loadedTextures.Clear();

            grid.Clear();

            string dir = Path.Combine(Application.persistentDataPath, "Creations");

#if UNITY_EDITOR
            // ── CREATE MOCK DATA FOR EDITOR TESTING ──
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string mockJson = Path.Combine(dir, "MockDrawing.json");
            string mockPng = Path.Combine(dir, "MockDrawing.png");
            if (!File.Exists(mockJson))
            {
                var mockData = new DrawingSaveData { id = "MockDrawing", timestamp = "Editor Test Drawing" };
                File.WriteAllText(mockJson, JsonUtility.ToJson(mockData));
                
                Texture2D t = new Texture2D(256, 256);
                for(int x=0; x<256; x++) for(int y=0; y<256; y++) t.SetPixel(x, y, (x+y)%32 < 16 ? Color.magenta : Color.black);
                t.Apply();
                File.WriteAllBytes(mockPng, t.EncodeToPNG());
                UnityEngine.Object.Destroy(t);
            }
#endif

            if (!Directory.Exists(dir)) return;

            string[] files = System.IO.Directory.GetFiles(dir, "*.json");

            // FIX #9: Show empty state when no drawings saved
            if (files.Length == 0)
            {
                Label emptyLabel = new Label("No drawings saved yet.\nTap + to start creating!");
                emptyLabel.style.color = new StyleColor(new Color(1f, 1f, 1f, 0.5f));
                emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                emptyLabel.style.fontSize = 18;
                emptyLabel.style.whiteSpace = WhiteSpace.Normal;
                emptyLabel.style.marginTop = 60;
                emptyLabel.style.marginLeft = 20;
                emptyLabel.style.marginRight = 20;
                grid.Add(emptyLabel);
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    string id = System.IO.Path.GetFileNameWithoutExtension(file);
                    string json = System.IO.File.ReadAllText(file);
                    DrawingSaveData SaveData = JsonUtility.FromJson<DrawingSaveData>(json);

                    CreateCard(grid, id, SaveData?.timestamp ?? "Drawing");
                }
                catch (System.Exception e) { Debug.LogWarning($"[UI] Failed to load card {file}: {e.Message}"); }
            }
        }

        private void CreateCard(VisualElement grid, string id, string timestamp)
        {
            // ── CARD: Button element ──
            // Button is the ONLY UI Toolkit element guaranteed to fire tap events reliably
            // on Android inside a ScrollView.
            Button card = new Button();
            card.AddToClassList("gallery__card");
            card.style.minHeight = 200;
            card.pickingMode = PickingMode.Position;

            // Strip all default Button chrome so the card looks identical to before.
            card.style.backgroundColor = Color.clear;
            card.style.borderTopWidth = 0;
            card.style.borderBottomWidth = 0;
            card.style.borderLeftWidth = 0;
            card.style.borderRightWidth = 0;
            card.style.paddingLeft = 0;
            card.style.paddingRight = 0;
            card.style.paddingTop = 0;
            card.style.paddingBottom = 0;

            VisualElement image = new VisualElement();
            image.AddToClassList("gallery__card-image");
            image.style.backgroundColor = Color.black;
            // CLEAN HIT-TEST: Ignore picking on the image so it falls through to the card Button
            image.pickingMode = PickingMode.Ignore;

            string pngPath = Path.Combine(Application.persistentDataPath, "Creations", $"{id}.png");
            if (System.IO.File.Exists(pngPath))
            {
                byte[] bytes = System.IO.File.ReadAllBytes(pngPath);
                Texture2D tex = new Texture2D(2, 2);
                tex.LoadImage(bytes);
                _loadedTextures.Add(tex);
                image.style.backgroundImage = new StyleBackground(tex);
            }

            Label title = new Label(timestamp);
            title.AddToClassList("gallery__card-title");
            // CLEAN HIT-TEST: Ignore picking on the text so it falls through to the card Button
            title.pickingMode = PickingMode.Ignore;

            Label date = new Label("Saved");
            date.AddToClassList("gallery__card-date");
            // CLEAN HIT-TEST: Ignore picking on the text so it falls through to the card Button
            date.pickingMode = PickingMode.Ignore;

            card.Add(image);
            card.Add(title);
            card.Add(date);

            // ── DELETE BUTTON ──
            Button deleteBtn = new Button();
            deleteBtn.AddToClassList("gallery__card-delete");
            deleteBtn.pickingMode = PickingMode.Position;

            // Clear default button backgrounds and borders
            deleteBtn.style.borderTopWidth = 0;
            deleteBtn.style.borderBottomWidth = 0;
            deleteBtn.style.borderLeftWidth = 0;
            deleteBtn.style.borderRightWidth = 0;
            deleteBtn.style.paddingLeft = 0;
            deleteBtn.style.paddingRight = 0;
            deleteBtn.style.paddingTop = 0;
            deleteBtn.style.paddingBottom = 0;

            Label deleteIcon = new Label("✕");
            deleteIcon.AddToClassList("gallery__card-delete-icon");
            deleteIcon.pickingMode = PickingMode.Ignore;
            deleteBtn.Add(deleteIcon);

            string dir = Path.Combine(Application.persistentDataPath, "Creations");

            deleteBtn.clicked += () =>
            {
                string jsonPath = Path.Combine(dir, $"{id}.json");
                string pngPath2 = Path.Combine(dir, $"{id}.png");

                if (System.IO.File.Exists(jsonPath)) System.IO.File.Delete(jsonPath);
                if (System.IO.File.Exists(pngPath2)) System.IO.File.Delete(pngPath2);

                // Delay so we are not inside an active click propagation when rebuilding UI
                deleteBtn.schedule.Execute(() => PopulateSavedDrawings()).ExecuteLater(50);
            };

            // Stop propagation on deleteBtn so card.clicked is NEVER called when deleting.
            deleteBtn.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            deleteBtn.RegisterCallback<PointerUpEvent>(evt   => evt.StopPropagation());
            deleteBtn.RegisterCallback<ClickEvent>(evt       => evt.StopPropagation());

            card.Add(deleteBtn);

            // ── CARD TAP — Button.clicked is the sole click mechanism ──
            card.clicked += () =>
            {
                var inspector = Object.FindAnyObjectByType<DrawingInspector>();
                if (inspector != null)
                {
                    inspector.OpenInspector(id);
                    ShowScreen(inspectorScreen);
                }
            };

            grid.Add(card);
        }
    }
}
