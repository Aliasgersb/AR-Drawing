using UnityEngine;
using UnityEngine.UI;

namespace SpatialDrawing
{
    /// <summary>
    /// Renders fingertip dots as screen-space UI elements positioned directly
    /// from 2D landmark screen coordinates. Eliminates jitter caused by
    /// the 3D ray-cast projection in the world-space visualizer.
    ///
    /// Drop-in replacement for FingertipVisualizer. To revert:
    ///   1. Disable this component
    ///   2. Re-enable FingertipVisualizer
    /// </summary>
    public class ScreenSpaceFingertipVisualizer : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private HandTrackingManager handTracker;

        [Header("Dot Appearance")]
        [Tooltip("Diameter of each dot in screen pixels.")]
        [SerializeField] private float dotDiameter = 28f;

        [Tooltip("Ring thickness as fraction of dot diameter.")]
        [SerializeField] private float ringThicknessFraction = 0.22f;

        [Tooltip("Default opacity when not interacting.")]
        [Range(0f, 1f)]
        [SerializeField] private float defaultOpacity = 0.5f;

        [Tooltip("Opacity when interacting (pinching).")]
        [Range(0f, 1f)]
        [SerializeField] private float pinchOpacity = 1.0f;

        // Runtime objects
        private Canvas _canvas;
        private RectTransform _canvasRect;
        private RectTransform[] _dotRects;
        private Image[] _dotImages;
        private Sprite _ringSprite;
        private bool[] _lastHighlightStates = new bool[5]; // UI Rebuild Guard: tracks state to prevent redundant updates
        private bool _isVisible = false; // Tracking dots are OFF by default (matches Settings default)

        void Start()
        {
            if (handTracker == null)
                handTracker = FindAnyObjectByType<HandTrackingManager>();

            CreateCanvas();
            _ringSprite = CreateRingSprite(128, ringThicknessFraction);

            _dotRects = new RectTransform[5];
            _dotImages = new Image[5];
            string[] names = { "SSDot_Thumb", "SSDot_Index", "SSDot_Middle", "SSDot_Ring", "SSDot_Pinky" };

            for (int i = 0; i < 5; i++)
            {
                var dotObj = new GameObject(names[i]);
                dotObj.transform.SetParent(_canvas.transform, false);

                var img = dotObj.AddComponent<Image>();
                img.sprite = _ringSprite;
                img.type = Image.Type.Simple;
                img.raycastTarget = false;
                img.color = new Color(1f, 1f, 1f, defaultOpacity);

                var rt = dotObj.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(dotDiameter, dotDiameter);
                // Anchor to bottom-left so anchoredPosition = screen pixels
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);

                dotObj.SetActive(false);

                _dotRects[i] = rt;
                _dotImages[i] = img;
            }

            Debug.Log("[ScreenSpaceFingertipVisualizer] Initialized — dots rendered in screen space.");

            // Apply default visibility (OFF until Settings toggle enables it)
            _canvas.gameObject.SetActive(_isVisible);
        }

        /// <summary>
        /// Show or hide all fingertip tracking dots.
        /// Called by the Settings 'Show Tracking Dots' toggle.
        /// </summary>
        public void SetVisible(bool visible)
        {
            _isVisible = visible;
            if (_canvas != null) _canvas.gameObject.SetActive(visible);
        }

        private void CreateCanvas()
        {
            var canvasObj = new GameObject("FingertipOverlayCanvas");
            canvasObj.transform.SetParent(transform, false);

            _canvas = canvasObj.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100; // Render on top of everything

            // CanvasScaler: constant pixel size (1:1 with screen)
            var scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            // OPTIMIZATION (Bug #5 Fix): UI Graphic Raycaster Disabling
            // Unity Canvas objects implicitly run expensive Raycast checks against
            // screen touches even if there are no buttons. Because our dots are pure 
            // visual overlays, destroying this prevents invisible background CPU drain.
            var raycaster = canvasObj.GetComponent<GraphicRaycaster>();
            if (raycaster != null)
            {
                Destroy(raycaster);
            }

            _canvasRect = canvasObj.GetComponent<RectTransform>();
        }

        void LateUpdate()
        {
            if (handTracker == null || _dotRects == null) return;

            bool visible = handTracker.IsHandDetected;

            for (int i = 0; i < 5; i++)
            {
                if (_dotRects[i].gameObject.activeSelf != visible)
                    _dotRects[i].gameObject.SetActive(visible);
            }

            if (!visible) return;

            // Position dots directly from filtered 2D screen coordinates with 60FPS visual smoothing
            for (int i = 0; i < 5; i++)
            {
                Vector2 targetPos = handTracker.GetPredictedScreenPosition(i);
                
                // If the dot just spawned or the hand teleported a huge distance, snap immediately.
                // OPTIMIZATION: Use sqrMagnitude instead of Distance
                float distSq = (_dotRects[i].anchoredPosition - targetPos).sqrMagnitude;
                if (distSq > 90000f) // 300 * 300
                {
                    _dotRects[i].anchoredPosition = targetPos;
                }
                else
                {
                    // 60FPS Smoothing: Interpolate toward the target position bridging the 30Hz tracking updates
                    _dotRects[i].anchoredPosition = Vector2.Lerp(_dotRects[i].anchoredPosition, targetPos, Time.deltaTime * 40f);
                }
            }

            // Highlight thumb + active pinch finger
            bool pinching = handTracker.IsIndexPinching || handTracker.IsMiddlePinching;

            for (int i = 0; i < 5; i++)
            {
                bool highlight = pinching && (i == 0
                    || (handTracker.IsIndexPinching && i == 1)
                    || (handTracker.IsMiddlePinching && i == 2));

                // UI Rebuild Guard: only update color if state actually changed
                // This prevents redundant "marking dirty" of the UI which causes micro-stutters
                if (highlight != _lastHighlightStates[i])
                {
                    _lastHighlightStates[i] = highlight;
                    Color c = Color.white;
                    c.a = highlight ? pinchOpacity : defaultOpacity;
                    _dotImages[i].color = c;
                }
            }
        }

        /// <summary>
        /// Creates a procedural ring sprite (hollow circle) at the given resolution.
        /// </summary>
        private static Sprite CreateRingSprite(int size, float thicknessFraction)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            float center = size / 2f;
            float outerRadius = center - 1f; // 1px margin for anti-aliasing
            float innerRadius = outerRadius * (1f - thicknessFraction);

            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    // Anti-aliased ring
                    float outerAlpha = Mathf.Clamp01(outerRadius - dist + 0.5f);
                    float innerAlpha = Mathf.Clamp01(dist - innerRadius + 0.5f);
                    float alpha = outerAlpha * innerAlpha;

                    byte a = (byte)(alpha * 255);
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();

            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        void OnDestroy()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
            if (_ringSprite != null)
            {
                if (_ringSprite.texture != null) Destroy(_ringSprite.texture);
                Destroy(_ringSprite);
            }
        }
    }
}
