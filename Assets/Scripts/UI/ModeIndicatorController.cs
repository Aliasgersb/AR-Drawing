using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace SpatialDrawing.UI
{
    /// <summary>
    /// Adds a dynamic Mode Indicator (Draw/Erase) to the UI Toolkit HUD.
    /// Purely additive, creates the UI element entirely in code.
    /// </summary>
    public class ModeIndicatorController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ArcMenuController arcMenu;
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private HandTrackingManager handTracker;

        private VisualElement _indicatorPill;
        private VisualElement _iconElement;
        private Label _textLabel;

        private Texture2D _drawIcon;
        private Texture2D _eraserIcon;
        private Texture2D _wrongHandIcon;

        // Visual State
        private bool _isEraserMode = false;
        private Coroutine _fadeCoroutine;

        // Warning State
        private bool _isWarningActive = false;

        IEnumerator Start()
        {
            // Load Assets
            _drawIcon = Resources.Load<Texture2D>("Icons/draw_48dp_E3E3E3_FILL0_wght400_GRAD0_opsz48");
            _eraserIcon = Resources.Load<Texture2D>("Icons/ink_eraser_48dp_E3E3E3_FILL0_wght400_GRAD0_opsz48");
            _wrongHandIcon = Resources.Load<Texture2D>("Icons/back_hand_48dp_E3E3E3_FILL0_wght400_GRAD0_opsz48");

            if (handTracker == null)
                handTracker = FindAnyObjectByType<HandTrackingManager>();


            if (arcMenu == null)
                arcMenu = FindAnyObjectByType<ArcMenuController>();
            if (uiDocument == null)
                uiDocument = GetComponent<UIDocument>();
            if (uiDocument == null)
                uiDocument = FindAnyObjectByType<UIDocument>();

            if (arcMenu == null || uiDocument == null)
            {
                Debug.LogError("[ModeIndicator] Missing references.");
                yield break;
            }

            // Wait until mainhud-screen is actually visible
            VisualElement mainHUD = null;
            while (true)
            {
                var root = uiDocument.rootVisualElement;
                if (root != null)
                {
                    mainHUD = root.Q("mainhud-screen");
                    if (mainHUD != null && mainHUD.resolvedStyle.display != DisplayStyle.None)
                        break;
                }
                yield return null;
            }

            arcMenu.OnEraserToggled += HandleEraserToggled;
            arcMenu.OnColorChanged += HandleColorChanged;
            arcMenu.OnBrushSizeChanged += HandleBrushSizeChanged;

            if (handTracker != null)
            {
                handTracker.OnWrongHandDetected += ShowWrongHandWarning;
                handTracker.OnWrongHandDismissed += HideWrongHandWarning;
            }

            CreateIndicatorUI();
            UpdateVisuals(false);
        }

        void OnDestroy()
        {
            if (arcMenu != null)
            {
                arcMenu.OnEraserToggled -= HandleEraserToggled;
                arcMenu.OnColorChanged -= HandleColorChanged;
                arcMenu.OnBrushSizeChanged -= HandleBrushSizeChanged;
            }
            if (handTracker != null)
            {
                handTracker.OnWrongHandDetected -= ShowWrongHandWarning;
                handTracker.OnWrongHandDismissed -= HideWrongHandWarning;
            }
        }

        private void CreateIndicatorUI()
        {
            var root = uiDocument.rootVisualElement;
            if (root == null) return;

            // Find MainHUD screen to append to
            var mainHUD = root.Q("mainhud-screen");
            if (mainHUD == null)
            {
                Debug.LogWarning("[ModeIndicator] 'mainhud-screen' not found in UXML template.");
                return;
            }

            // 1. Create Wrapper for centering absolute element
            var centerWrapper = new VisualElement();
            centerWrapper.name = "mode-indicator-wrapper";

            // 2. Create Pill (Outer Container: Handles Clipping & Borders)
            _indicatorPill = new VisualElement();
            _indicatorPill.name = "mode-indicator-pill";
            _indicatorPill.style.overflow = Overflow.Hidden;

            // Set Corners explicitly (shorthands are for USS only)
            _indicatorPill.style.borderTopLeftRadius = new Length(36, LengthUnit.Pixel);
            _indicatorPill.style.borderTopRightRadius = new Length(36, LengthUnit.Pixel);
            _indicatorPill.style.borderBottomLeftRadius = new Length(36, LengthUnit.Pixel);
            _indicatorPill.style.borderBottomRightRadius = new Length(36, LengthUnit.Pixel);

            _indicatorPill.style.borderTopWidth = 1;
            _indicatorPill.style.borderRightWidth = 1;
            _indicatorPill.style.borderBottomWidth = 1;
            _indicatorPill.style.borderLeftWidth = 1;

            Color bColor = new Color(1f, 1f, 1f, 0.15f);
            _indicatorPill.style.borderTopColor = bColor;
            _indicatorPill.style.borderRightColor = bColor;
            _indicatorPill.style.borderBottomColor = bColor;
            _indicatorPill.style.borderLeftColor = bColor;

            // 3. Create Glass Background element
            var glassBg = new VisualElement();
            glassBg.name = "mode-indicator-glass-bg";
            glassBg.AddToClassList("glass-effect");
            glassBg.style.position = Position.Absolute;
            glassBg.style.width = new Length(100, LengthUnit.Percent);
            glassBg.style.height = new Length(100, LengthUnit.Percent);

            // 4. Create Elements
            _iconElement = new VisualElement();
            _iconElement.name = "mode-indicator-icon";

            _textLabel = new Label();
            _textLabel.name = "mode-indicator-text";

            // Assemble layout
            _indicatorPill.Add(glassBg); // Material draws only inside clipped box!
            _indicatorPill.Add(_iconElement);
            _indicatorPill.Add(_textLabel);
            
            centerWrapper.Add(_indicatorPill);
            mainHUD.Add(centerWrapper);

            // Apply Styles
            ApplyStyles(centerWrapper);
        }

        private void ApplyStyles(VisualElement wrapper)
        {
            if (_indicatorPill == null || wrapper == null) return;

            // Wrapper Styling: Full width Absolute bottom-aligned
            var wrapStyle = wrapper.style;
            wrapStyle.position = Position.Absolute;
            wrapStyle.bottom = new Length(120, LengthUnit.Pixel);
            wrapStyle.left = 0;
            wrapStyle.right = 0;
            wrapStyle.alignItems = Align.Center; // Center child pill horizontally
            wrapStyle.justifyContent = Justify.Center;
            wrapper.pickingMode = PickingMode.Ignore;

            // Pill Container Styling
            var style = _indicatorPill.style;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            
            // Liquid glass body — purely transparent on top of the blur material
            style.backgroundColor = new Color(0f, 0f, 0f, 0f);

            // Hairline glass-edge border matching USS per-edge values
            style.borderTopWidth = 1.5f;
            style.borderRightWidth = 1f;
            style.borderBottomWidth = 1f;
            style.borderLeftWidth = 1f;
            style.borderTopColor    = new Color(1f, 1f, 1f, 0.65f);
            style.borderRightColor  = new Color(1f, 1f, 1f, 0.22f);
            style.borderBottomColor = new Color(1f, 1f, 1f, 0.10f);
            style.borderLeftColor   = new Color(1f, 1f, 1f, 0.22f);

            // Padding
            style.paddingLeft = 48;
            style.paddingRight = 50;
            style.paddingTop = 24;
            style.paddingBottom = 24;

            // Interaction
            _indicatorPill.pickingMode = PickingMode.Ignore;
            style.opacity = 1f;

            // Icon Styling (Increased)
            var iconStyle = _iconElement.style;
            iconStyle.width = 64;
            iconStyle.height = 64;
            iconStyle.marginRight = 20;
            iconStyle.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            iconStyle.unityBackgroundImageTintColor = Color.white;

            // Text Styling (Increased)
            var textStyle = _textLabel.style;
            textStyle.color = Color.white;
            textStyle.fontSize = 36;
            textStyle.unityFontStyleAndWeight = FontStyle.Bold;
            textStyle.letterSpacing = 1f;
        }

        private void HandleEraserToggled() => SetMode(true);
        private void HandleColorChanged(Color color) => SetMode(false);
        private void HandleBrushSizeChanged(float size) => SetMode(false);

        private void SetMode(bool isEraser)
        {
            if (_isEraserMode == isEraser) return;
            _isEraserMode = isEraser;

            if (_fadeCoroutine != null)
                StopCoroutine(_fadeCoroutine);

            _fadeCoroutine = StartCoroutine(TransitionMode(isEraser));
        }

        private IEnumerator TransitionMode(bool isEraser)
        {
            if (_indicatorPill == null) yield break;

            float duration = 0.2f;
            float elapsed = 0f;

            // 1. Fade out
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _indicatorPill.style.opacity = Mathf.Lerp(1f, 0f, elapsed / duration);
                yield return null;
            }
            _indicatorPill.style.opacity = 0f;

            // 2. Change content
            UpdateVisuals(isEraser);

            // 3. Fade in
            elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _indicatorPill.style.opacity = Mathf.Lerp(0f, 1f, elapsed / duration);
                yield return null;
            }
            _indicatorPill.style.opacity = 1f;
        }

        private void ShowWrongHandWarning(bool isLeftDominant)
        {
            if (_isWarningActive) return; // Already showing or transitioning

            _isWarningActive = true;
            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = StartCoroutine(TransitionWarningState(isLeftDominant));
        }

        private void HideWrongHandWarning()
        {
            if (!_isWarningActive) return;
            
            _isWarningActive = false;
        }

        private IEnumerator TransitionWarningState(bool isLeftDominant)
        {
             if (_indicatorPill == null) yield break;

            float duration = 0.15f;
            float elapsed = 0f;

            // 1. Fade out current state
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _indicatorPill.style.opacity = Mathf.Lerp(1f, 0f, elapsed / duration);
                yield return null;
            }
            _indicatorPill.style.opacity = 0f;

            // 2. Set Warning visuals
            _indicatorPill.style.backgroundColor = new Color(0.957f, 0.247f, 0.369f, 0.40f); // Bright red/pink alert background
            _iconElement.style.backgroundImage = new StyleBackground(_wrongHandIcon);
            _textLabel.text = isLeftDominant ? "Left Hand set as Dominant" : "Right Hand set as Dominant";
            _indicatorPill.MarkDirtyRepaint();

            // 3. Fade in Warning
            elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _indicatorPill.style.opacity = Mathf.Lerp(0f, 1f, elapsed / duration);
                yield return null;
            }
            _indicatorPill.style.opacity = 1f;

            // 4. Wait indefinitely until dismissed by HandTrackingManager
            while (_isWarningActive)
            {
                yield return null;
            }

            // 5. Restore normal state
            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = StartCoroutine(TransitionMode(_isEraserMode));
        }

#if UNITY_EDITOR
        void Update()
        {
            // Debug keys to test transitions in Play Mode without hand tracking
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.eKey.wasPressedThisFrame)
            {
                Debug.Log("[ModeIndicator] Debug: Toggle Erase");
                SetMode(true);
            }
            else if (keyboard.dKey.wasPressedThisFrame)
            {
                Debug.Log("[ModeIndicator] Debug: Toggle Draw");
                SetMode(false);
            }
        }
#endif

        private void UpdateVisuals(bool isEraser)
        {
            if (_iconElement == null || _textLabel == null) return;

            if (isEraser)
            {
                _iconElement.style.backgroundImage = new StyleBackground(_eraserIcon);
                _textLabel.text = "Eraser";
            }
            else
            {
                _iconElement.style.backgroundImage = new StyleBackground(_drawIcon);
                _textLabel.text = "Draw";
            }
            
            // Restore normal background color (transparent to show pure blur)
            _indicatorPill.style.backgroundColor = new Color(0f, 0f, 0f, 0f);

            // Mark dirty to redraw material mesh if size changes
            _indicatorPill.MarkDirtyRepaint();
        }

    }
}
