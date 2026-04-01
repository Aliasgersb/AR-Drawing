using System;
using UnityEngine;

namespace SpatialDrawing
{
    /// <summary>
    /// Detects gestures for the arc menu system:
    /// - Open palm hold (0.4s) → triggers menu open
    /// - Index+thumb tap → cycle menu items
    /// - Middle+thumb tap → confirm selection
    /// - Palm lost / closed hand → dismiss menu (with grace period)
    ///
    /// Reads hand data from HandTrackingManager.
    /// Reads menu state from ArcMenuController.IsOpen (single source of truth).
    /// </summary>
    public class GestureDetector : MonoBehaviour
    {
        // ── Events ──
        public event Action OnPalmMenuTriggered;
        public event Action OnPalmMenuDismissed;
        public event Action OnForceCloseMenu;
        public event Action OnCycleTap;
        public event Action OnConfirmTap;

        // ── References ──
        [Header("References")]
        [SerializeField] private HandTrackingManager handTracker;
        [SerializeField] private ArcMenuController menuController;

        // ── Open Palm Detection ──
        [Header("Open Palm Detection")]
        [Tooltip("How long the open palm must be held to trigger the menu (seconds).")]
        [SerializeField] private float palmHoldDuration = 0.3f;
        [Tooltip("Minimum dot product between palm normal and -camera forward to count as 'facing camera'.")]
        [SerializeField] private float palmFacingThreshold = 0.35f;
        [Tooltip("How long a tracking drop is tolerated before the palm hold timer resets (seconds).")]
        [SerializeField] private float palmDropGrace = 0.1f;

        // ── Tap Detection ──
        [Tooltip("Cooldown between taps to prevent double-triggers (seconds).")]
        [SerializeField] private float tapCooldown = 0.15f;

        // ── Dismissal ──
        [Header("Dismissal")]
        [Tooltip("Grace period before fingers-not-extended triggers a dismiss (seconds).")]
        [SerializeField] private float dismissGracePeriod = 0.2f;
        [Tooltip("If pinching for longer than this while menu is open, force-dismiss (seconds).")]
        [SerializeField] private float sustainedPinchTimeout = 1.0f;

        // ── Internal State ──
        private Camera _mainCamera;

        // ── TEMPORARY ON-SCREEN DEBUG (remove after calibration) ──
        private string _debugText = "";

        // Palm hold
        private float _palmHoldTimer;
        private float _palmDropTimer;
        private bool _palmWasValid;

        // Index+thumb tap
        private bool _indexThumbPinched;
        private float _lastCycleTapTime = -1f;

        // Middle+thumb tap
        private bool _middleThumbPinched;
        private float _lastConfirmTapTime = -1f;

        // Dismissal
        private float _dismissGraceTimer;
        private float _sustainedPinchTimer;
        private bool _isHandClosedDismissBlocked;

        // ── Convenience ──
        /// <summary>True when the arc menu is currently open (reads from ArcMenuController).</summary>
        private bool IsMenuActive => menuController != null && menuController.IsOpen;

        void Start()
        {
            if (handTracker == null)
                handTracker = FindAnyObjectByType<HandTrackingManager>();
            if (menuController == null)
                menuController = FindAnyObjectByType<ArcMenuController>();

            _mainCamera = Camera.main;
        }

        void Update()
        {
            if (handTracker == null || !handTracker.IsHandDetected)
            {
                // Hand lost — dismiss menu if open
                if (_palmWasValid || IsMenuActive)
                {
                    ResetAllState();
                    _isHandClosedDismissBlocked = false;
                    
                    if (IsMenuActive)
                    {
                        OnForceCloseMenu?.Invoke();
#if UNITY_EDITOR
                        Debug.Log("[Gesture] Menu force dismissed — hand lost");
#endif
                    }
                }
                return;
            }

            if (!IsMenuActive)
            {
                // ── Menu is closed — detect open palm to trigger menu ──
                DetectOpenPalm();
            }
            else
            {
                // ── Menu is open — detect navigation gestures ──
                // FIX #1: Process taps BEFORE dismissal so tap in-progress flags
                //         are set before the dismissal logic checks them.
                DetectCycleTap();
                DetectConfirmTap();
                DetectMenuDismissal();
            }
        }

        // ════════════════════════════════════════════════════════
        //  OPEN PALM DETECTION
        // ════════════════════════════════════════════════════════

        private void DetectOpenPalm()
        {
            bool palmValid = IsPalmOpenAndFacing();

            if (palmValid)
            {
                // FIX #4: Reset the drop counter since palm is valid now
                _palmDropTimer = 0f;
                _palmHoldTimer += Time.deltaTime;

                if (_palmHoldTimer >= palmHoldDuration)
                {
                    // Trigger menu!
                    _palmHoldTimer = 0f;
                    ResetTapStates();
                    _dismissGraceTimer = 0f;
                    _sustainedPinchTimer = 0f;
                    OnPalmMenuTriggered?.Invoke();
#if UNITY_EDITOR
                    Debug.Log("[Gesture] Open palm held — menu triggered!");
#endif
                }
            }
            else
            {
                // FIX #4: Don't instantly reset the hold timer.
                // Allow brief tracking drops within the grace window.
                _palmDropTimer += Time.deltaTime;
                if (_palmDropTimer > palmDropGrace)
                {
                    _palmHoldTimer = 0f; // Grace expired → reset
                }
                // While in grace, _palmHoldTimer is frozen (neither incremented nor reset)
            }

            _palmWasValid = palmValid;
        }

        private bool IsPalmOpenAndFacing()
        {
            bool fingersOk = handTracker.IsAllFingersExtended;
            bool zDepthOk = handTracker.IsHandFacingCamera;

            if (_mainCamera == null)
            {
                _debugText = "BLOCKED: camera is null";
                return false;
            }

            Vector3 camForward = _mainCamera.transform.forward;
            float facing = Vector3.Dot(handTracker.PalmNormal, -camForward);
            bool facingOk = facing > palmFacingThreshold;

#if UNITY_EDITOR
            // ── TEMPORARY ON-SCREEN DEBUG ──
            _debugText = $"Fingers: {(fingersOk ? "YES" : "NO")}\n" +
                         $"Z-Depth: {(zDepthOk ? "YES" : "NO")}\n" +
                         $"PalmDot: {facing:F3} (need>{palmFacingThreshold})\n" +
                         $"Facing:  {(facingOk ? "YES" : "NO")}\n" +
                         $"RESULT:  {(fingersOk && zDepthOk && facingOk ? "OPEN" : "BLOCKED")}";
#endif

            if (!fingersOk)
                return false;
            if (!zDepthOk)
                return false;
            return facingOk;
        }

        // ════════════════════════════════════════════════════════
        //  MENU DISMISSAL
        // ════════════════════════════════════════════════════════

        private void DetectMenuDismissal()
        {
            // The menu shouldn't close just because a finger elegantly bent to tap.
            // Use OR to match the pinch gate. A fist closes ALL fingers — if either Ring or
            // Pinky is still extended, the user is interacting, not dismissing.
            bool handOpenEnough = handTracker.IsRingExtended || handTracker.IsPinkyExtended;
            
            // Check rotation
            bool zDepthOk = handTracker.IsHandFacingCamera;
            Vector3 camForward = _mainCamera.transform.forward;
            bool facingOk = Vector3.Dot(handTracker.PalmNormal, -camForward) > palmFacingThreshold;
            bool palmFacing = zDepthOk && facingOk;

            bool activePinch = handTracker.IsIndexPinching || handTracker.IsMiddlePinching;

            // ── FIX #2 & #9: Sustained pinch timeout ──
            if (activePinch)
            {
                _sustainedPinchTimer += Time.deltaTime;
                if (_sustainedPinchTimer > sustainedPinchTimeout)
                {
                    ResetAllState();
                    OnForceCloseMenu?.Invoke();
#if UNITY_EDITOR
                    Debug.Log("[Gesture] Menu force dismissed — sustained pinch timeout");
#endif
                    return;
                }
            }
            else
            {
                _sustainedPinchTimer = 0f;
            }

            // ── Grace period for finger drops, palm rotation & Double-close prevent ──
            if (!handOpenEnough || (!palmFacing && !activePinch))
            {
                if (_isHandClosedDismissBlocked) return;

                // Active pinches suspend the close countdown completely.
                // Because handOpenEnough rests on the ring/pinky, normal taps don't trigger the timer at all!
                if (activePinch)
                {
                    _dismissGraceTimer = 0f;
                    return;
                }

                _dismissGraceTimer += Time.deltaTime;
                if (_dismissGraceTimer > dismissGracePeriod)
                {
                    _isHandClosedDismissBlocked = true;
                    ResetAllState();
                    OnPalmMenuDismissed?.Invoke();
#if UNITY_EDITOR
                    Debug.Log("[Gesture] Menu dismissed — posture invalid (fist or palm rotated)");
#endif
                }
            }
            else
            {
                _dismissGraceTimer = 0f;
                _isHandClosedDismissBlocked = false;
            }
        }

        // ════════════════════════════════════════════════════════
        //  INDEX + THUMB TAP (Cycle)
        // ════════════════════════════════════════════════════════

        private void DetectCycleTap()
        {
            bool pinching = handTracker.IsIndexPinching;

            if (pinching && !_indexThumbPinched)
            {
                _indexThumbPinched = true;

                // Trigger on PINCH START (much more responsive and reliable)
                if (Time.time - _lastCycleTapTime > tapCooldown)
                {
                    _lastCycleTapTime = Time.time;
                    HapticService.Medium(); // Cycle tap gesture feedback
                    OnCycleTap?.Invoke();
#if UNITY_EDITOR
                    Debug.Log("[Gesture] Cycle tap (index+thumb) - TRIGGERED ON START");
#endif
                }
            }
            else if (!pinching && _indexThumbPinched)
            {
                _indexThumbPinched = false;
            }
        }

        // ════════════════════════════════════════════════════════
        //  MIDDLE + THUMB TAP (Confirm)
        // ════════════════════════════════════════════════════════

        private void DetectConfirmTap()
        {
            bool pinching = handTracker.IsMiddlePinching;

            if (pinching && !_middleThumbPinched)
            {
                _middleThumbPinched = true;

                // Trigger on PINCH START
                if (Time.time - _lastConfirmTapTime > tapCooldown)
                {
                    _lastConfirmTapTime = Time.time;
                    HapticService.Strong(); // Confirm tap — most satisfying
                    OnConfirmTap?.Invoke();
#if UNITY_EDITOR
                    Debug.Log("[Gesture] Confirm tap (middle+thumb) - TRIGGERED ON START");
#endif
                }
            }
            else if (!pinching && _middleThumbPinched)
            {
                _middleThumbPinched = false;
            }
        }

        // ════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════

        private void ResetAllState()
        {
            _palmHoldTimer = 0f;
            _palmDropTimer = 0f;
            _palmWasValid = false;
            _dismissGraceTimer = 0f;
            _sustainedPinchTimer = 0f;
            ResetTapStates();
        }

        private void ResetTapStates()
        {
            _indexThumbPinched = false;
            _middleThumbPinched = false;
            _lastCycleTapTime = Time.time;
            _lastConfirmTapTime = Time.time;
        }

        // ════════════════════════════════════════════════════════
        //  TEMPORARY ON-SCREEN DEBUG OVERLAY (remove after calibration)
        // ════════════════════════════════════════════════════════
#if UNITY_EDITOR
        private GUIStyle _debugStyle;

        void OnGUI()
        {
            if (string.IsNullOrEmpty(_debugText)) return;

            if (_debugStyle == null)
            {
                _debugStyle = new GUIStyle(GUI.skin.box);
                _debugStyle.fontSize = Mathf.Max(24, Screen.height / 40);
                _debugStyle.alignment = TextAnchor.UpperLeft;
                _debugStyle.normal.textColor = Color.white;
                _debugStyle.padding = new RectOffset(12, 12, 8, 8);
            }

            float w = Screen.width * 0.9f;
            float h = Screen.height * 0.2f;
            float x = (Screen.width - w) / 2f;
            float y = Screen.height * 0.02f;

            GUI.Box(new Rect(x, y, w, h), _debugText, _debugStyle);
        }
#endif
    }
}
