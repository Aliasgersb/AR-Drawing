using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialDrawing
{
    /// <summary>
    /// Displays the gesture-controlled menu as a clean, floating horizontal row of icons.
    /// Manually constructs its own Canvas at runtime to guarantee zero Editor misconfiguration.
    /// </summary>
    public class FloatingMenuUI : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The physical 3D World Space Prefab for the menu")]
        [SerializeField] private GameObject menuPrefab;
        [SerializeField] private HandTrackingManager handTracker;
        [SerializeField] private ArcMenuController menuController;

        [Header("Icons — drag your SVG sprites here")]
        [SerializeField] private Sprite colorIcon;
        [SerializeField] private Sprite thicknessIcon;
        [SerializeField] private Sprite eraserIcon;
        [SerializeField] private Sprite clearIcon;
        [SerializeField] private Sprite saveIcon;

        [Header("Material Override")]
        [Tooltip("Assign a working UI/Default material here if the built-in Unity one renders invisible in URP")]
        [SerializeField] private Material customUIMaterial;

        [Header("Layout & Styling")]
        [Tooltip("Size of each icon in pixels")]
        [SerializeField] private float iconSize = 35f;
        [SerializeField] private Color highlightColor = new Color(0f, 0.85f, 1f, 1f);

        [Header("Animation")]
        [SerializeField] private float positionSmoothSpeed = 8f;
        [SerializeField] private float openAnimDuration = 0.2f;

        private Camera _mainCamera;
        
        // The spawned instance of the menuPrefab
        private GameObject _menuInstance;
        private RectTransform _container;
        private CanvasGroup _canvasGroup;
        private List<FloatingMenuItem> _currentItems = new();

        // State
        private bool _isVisible;
        private bool _isClosing;
        private float _openProgress;
        private float _openVelocity;
        private Vector3 _smoothedPosition;
        private bool _positionInitialized;

        // Track dynamically created materials for explicit cleanup
        private List<Material> _dynamicMaterials = new();

        void Awake()
        {
            if (handTracker == null) handTracker = FindAnyObjectByType<HandTrackingManager>();
            if (menuController == null) menuController = FindAnyObjectByType<ArcMenuController>();
            
            _mainCamera = Camera.main;
            if (_mainCamera == null)
            {
                Debug.LogWarning("[FloatingMenuUI] Camera.main is null! Finding by tag.");
                _mainCamera = GameObject.FindGameObjectWithTag("MainCamera")?.GetComponent<Camera>();
            }

            if (menuPrefab != null)
            {
                SpawnPrefab();
            }
            else
            {
                Debug.LogError("[FloatingMenuUI] STRICT ERROR: Missing menuPrefab! Please assign it in the Inspector.");
            }
        }

        void Start()
        {
            if (saveIcon == null)
            {
                saveIcon = Resources.Load<Sprite>("MenuIcons/SaveIcon");
                if (saveIcon != null) Debug.Log("[FloatingMenuUI] Loaded SaveIcon from Resources.");
            }

            if (menuController != null)
            {
                menuController.OnMenuStateChanged += HandleMenuStateChanged;
                menuController.OnMenuLevelChanged += HandleMenuLevelChanged;
                menuController.OnHighlightChanged += HandleHighlightChanged;
            }
        }

        private void SpawnPrefab()
        {
            _menuInstance = Instantiate(menuPrefab);
            
            // Critical for AR Space: Set to world space 0,0,0 initially
            _menuInstance.transform.SetParent(null); 
            _menuInstance.transform.position = Vector3.zero;

            // Grab references securely
            _canvasGroup = _menuInstance.GetComponentInChildren<CanvasGroup>();
            if (_canvasGroup == null) _canvasGroup = _menuInstance.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;

            // The layout group will exist on a child, or the root if formatted that way
            _container = _menuInstance.transform.Find("ItemContainer") as RectTransform;
            if (_container == null) _container = _menuInstance.GetComponent<RectTransform>();

            // ABSOLUTE FIX: The user's screenshot had PosX=540, PosY=1080 saved into the Prefab.
            // In 0.001 World Scale, that pushes the menu 0.54m Right and +1.08m UP (into the ceiling).
            // We MUST defensively zero out the local geometry offsets.
            RectTransform rootRt = _menuInstance.GetComponent<RectTransform>();
            if (rootRt != null)
            {
                rootRt.localPosition = Vector3.zero;
                rootRt.anchoredPosition = Vector2.zero;
            }
            if (_container != null)
            {
                _container.localPosition = Vector3.zero;
                _container.anchoredPosition = Vector2.zero;

                // Fix horizontal layout group making items bottom-aligned
                var hlg = _container.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                if (hlg != null)
                {
                    hlg.childAlignment = TextAnchor.MiddleCenter;
                }
            }

            // NUCLEAR FIX: Change Layer to Default (0) because cameras ALWAYS see Default.
            // Many AR cameras cull the 'UI' layer by mistake.
            SetLayerRecursive(_menuInstance, 0);

            // NUCLEAR FIX: Ensure the Canvas renders on top within world space
            Canvas c = _menuInstance.GetComponent<Canvas>();
            if (c != null)
            {
                c.sortingOrder = 1000;
                c.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal | AdditionalCanvasShaderChannels.Tangent;
            }

            _menuInstance.SetActive(false);
            // DEFINITIVE FIX: Force name so debug probes find it instantly
            _menuInstance.name = "FloatingMenuCanvas_Runtime";
            
            // DEFINITIVE FIX: Scan the entire Prefab and fix any broken materials (like backgrounds)
            FixMaterialsRecursively(_menuInstance.transform);
            
            Debug.Log("[FloatingMenuUI] NUCLEAR FIX: Layer 0, Sorting 1000, and Material Fix applied.");
        }

        private void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayerRecursive(t.gameObject, layer);
        }

        private void FixMaterialsRecursively(Transform parent)
        {
            var img = parent.GetComponent<Image>();
            if (img != null)
            {
                if (customUIMaterial != null)
                {
                    img.material = customUIMaterial;
                }
                else if (img.material == null || img.material.name == "Default UI Material")
                {
                    // Use UI/Default specifically which is more reliable in some URP builds than the null-fallback
                    Material mat = new Material(Shader.Find("UI/Default"));
                    if (mat.shader != null)
                    {
                        img.material = mat;
                        _dynamicMaterials.Add(mat); // Track for cleanup
                    }
                    else
                    {
                        img.material = Canvas.GetDefaultCanvasMaterial();
                        Destroy(mat); // Immediately destroy the failed material
                    }
                }
            }

            foreach (Transform child in parent)
            {
                FixMaterialsRecursively(child);
            }
        }

        void LateUpdate()
        {
#if UNITY_EDITOR
            if (UnityEngine.InputSystem.Keyboard.current != null &&
                UnityEngine.InputSystem.Keyboard.current.mKey.wasPressedThisFrame)
            {
                ToggleEditorPreview();
            }
#endif

            if (!_isVisible && !_isClosing) return;

            // Animate popup and fade
            float target = _isVisible ? 1f : 0f;
            
            // OPTIMIZATION: Stop redundant Math and UI Canvas Dirtying when idle
            bool isAnimating = Mathf.Abs(_openProgress - target) > 0.001f;

            if (isAnimating)
            {
                _openProgress = Mathf.SmoothDamp(_openProgress, target, ref _openVelocity, openAnimDuration);

                if (_menuInstance != null)
                {
                    float s = Mathf.Clamp01(_openProgress);
                    // Reduce world scale to 0.0007f to avoid "oversized" UI in AR view
                    _menuInstance.transform.localScale = Vector3.one * (0.0007f * s);
                    
                    if (_canvasGroup != null) _canvasGroup.alpha = s;
                }
            }
            else if (_openProgress != target && _menuInstance != null)
            {
                // Snap to final values exactly once
                _openProgress = target;
                float s = Mathf.Clamp01(_openProgress);
                _menuInstance.transform.localScale = Vector3.one * (0.0007f * s);
                if (_canvasGroup != null) _canvasGroup.alpha = s;
            }

            if (_isClosing && _openProgress < 0.01f)
            {
                _isClosing = false;
                _openProgress = 0f;
                ClearItems();
                _menuInstance?.SetActive(false);
                return;
            }

            // Track hand
            if (_isVisible && handTracker != null)
                UpdatePosition();
        }

#if UNITY_EDITOR
        private void ToggleEditorPreview()
        {
            if (!_isVisible)
            {
                _isVisible = true;
                _isClosing = false;
                _openProgress = 0f;

                if (_menuInstance != null)
                {
                    _menuInstance.SetActive(true);
                    
                    // In editor preview, just place it 50cm in front of the camera
                    if (_mainCamera != null)
                    {
                        _smoothedPosition = _mainCamera.transform.position + _mainCamera.transform.forward * 0.5f;
                        _menuInstance.transform.position = _smoothedPosition;
                        
                        // DEFINITIVE FIX: Menu faces CAMERA (Z-Forward points at user), always horizontal
                        Vector3 lookAtDir = _mainCamera.transform.position - _menuInstance.transform.position;
                        if (lookAtDir.sqrMagnitude > 0.001f)
                            _menuInstance.transform.rotation = Quaternion.LookRotation(lookAtDir, Vector3.up);
                    }
                    else
                    {
                        _menuInstance.transform.position = Vector3.zero;
                    }
                    
                    Debug.Log($"[FloatingMenuUI] Set to World Space Editor Override");
                }

                BuildMainMenu();
                _positionInitialized = true;
                Debug.Log("[FloatingMenuUI] Preview OPENED");
            }
            else
            {
                _isVisible = false;
                _isClosing = true;
                Debug.Log("[FloatingMenuUI] Preview CLOSED");
            }
        }
#endif

        private void UpdatePosition()
        {
            if (!handTracker.IsHandDetected || _menuInstance == null || _mainCamera == null) return;

            // 1. Position it directly at the physical 3D palm coordinate
            Vector3 targetWorldPos = handTracker.PalmCenter; // At the exact center of the palm

            if (!_positionInitialized)
            {
                _smoothedPosition = targetWorldPos;
                _positionInitialized = true;
            }
            else
            {
                _smoothedPosition = Vector3.Lerp(_smoothedPosition, targetWorldPos, Time.deltaTime * positionSmoothSpeed);
            }

            _menuInstance.transform.position = _smoothedPosition;

            // 2. Rotate it so the Canvas physically faces the user's camera (billboarding)
            // Pass Vector3.up as the world-up reference so the menu is ALWAYS horizontal
            // regardless of hand tilt or palm angle. Without this, LookRotation derives
            // an arbitrary up-vector from the look direction which allows the menu to tilt.
            Vector3 lookDir = _mainCamera.transform.position - _smoothedPosition;
            if (lookDir.sqrMagnitude > 0.001f)
            {
                _menuInstance.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
            }
        }

        private void HandleMenuStateChanged(bool isOpen)
        {
            if (isOpen)
            {
                _isVisible = true;
                _isClosing = false;
                if (handTracker != null && handTracker.IsHandDetected && _mainCamera != null)
                {
                    _smoothedPosition = handTracker.PalmCenter; // Exactly at the palm center
                    _positionInitialized = true;
                }
                _menuInstance?.SetActive(true);
                _openProgress = 0f;
                BuildMainMenu();
            }
            else
            {
                _isVisible = false;
                _isClosing = true;
                _positionInitialized = false;
            }
        }

        private void HandleMenuLevelChanged(ArcMenuController.MenuLevel level, int highlightIndex)
        {
            ClearItems();
            switch (level)
            {
                case ArcMenuController.MenuLevel.Main: BuildMainMenu(); break;
                case ArcMenuController.MenuLevel.ColorSub: BuildColorSubmenu(); break;
                case ArcMenuController.MenuLevel.ThicknessSub: BuildThicknessSubmenu(); break;
            }
            SetHighlight(highlightIndex);
        }

        private void HandleHighlightChanged(int index) => SetHighlight(index);

        private void BuildMainMenu()
        {
            ClearItems();
            Sprite[] icons = { colorIcon, thicknessIcon, eraserIcon, clearIcon, saveIcon };
            var mainItems = ArcMenuController.MainItems;

            for (int i = 0; i < mainItems.Length; i++)
            {
                // Slightly reduce the size of the MAIN menu icons only (15% reduction)
                var item = CreateItem(iconSize * 0.85f);
                item.MainItemType = mainItems[i];
                if (i < icons.Length && icons[i] != null)
                {
                    item.SetIcon(icons[i]);
                    Debug.Log($"[FloatingMenuUI] Assigned icon sprite: {icons[i].name}");
                }
                else
                {
                    Debug.LogWarning($"[FloatingMenuUI] Missing sprite for index {i}! Please check Inspector.");
                }

                    if (mainItems[i] == ArcMenuController.MainMenuItem.Save)
                    {
                        var iconTr = item.transform.Find("Icon");
                        if (iconTr != null) iconTr.localScale = new Vector3(-1f, 1f, 1f);
                    }

                _currentItems.Add(item);
            }
            SetHighlight(0);
        }

        private void BuildColorSubmenu()
        {
            ClearItems();
            for (int i = 0; i < ArcMenuController.Colors.Length; i++)
            {
                var item = CreateItem(iconSize * 0.5f);
                item.SetColorDot(ArcMenuController.Colors[i]);
                _currentItems.Add(item);
            }
        }

        private void BuildThicknessSubmenu()
        {
            ClearItems();
            for (int i = 0; i < ArcMenuController.ThicknessSizes.Length; i++)
            {
                var item = CreateItem();
                float normalized = (float)i / (ArcMenuController.ThicknessSizes.Length - 1);
                item.SetThicknessDot(normalized);
                _currentItems.Add(item);
            }
        }

        private void SetHighlight(int index)
        {
            for (int i = 0; i < _currentItems.Count; i++)
                _currentItems[i].SetHighlighted(i == index);
        }

        private FloatingMenuItem CreateItem(float? overrideSize = null)
        {
            if (_container == null) return null;

            float size = overrideSize ?? iconSize;

            // Create a clean GameObject for the icon
            GameObject go = new GameObject("MenuItem");
            go.layer = 0; // Force Default layer
            go.transform.SetParent(_container, false); 
            
            // Reapply local scale to 1 within the layout group hierarchy
            go.transform.localScale = Vector3.one;

            var item = go.AddComponent<FloatingMenuItem>();
            item.Initialize(size, highlightColor);
            return item;
        }

        private void ClearItems()
        {
            foreach (var item in _currentItems)
                if (item != null) Destroy(item.gameObject);
            _currentItems.Clear();
        }

        void OnDestroy()
        {
            if (menuController != null)
            {
                menuController.OnMenuStateChanged -= HandleMenuStateChanged;
                menuController.OnMenuLevelChanged -= HandleMenuLevelChanged;
                menuController.OnHighlightChanged -= HandleHighlightChanged;
            }
            // Destroy dynamically created materials to prevent memory leaks
            foreach (var mat in _dynamicMaterials)
            {
                if (mat != null) Destroy(mat);
            }
            _dynamicMaterials.Clear();
            if (_menuInstance != null)
            {
                Destroy(_menuInstance);
            }
        }
    }
}
