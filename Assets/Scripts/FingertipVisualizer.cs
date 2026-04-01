using UnityEngine;

namespace SpatialDrawing
{
    /// <summary>
    /// Renders small glowing dots at all 5 fingertip positions.
    /// Thumb and index dots change color when pinching.
    /// Attach to the same GameObject as HandTrackingManager, or any GameObject in the scene.
    /// </summary>
    public class FingertipVisualizer : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private HandTrackingManager handTracker;

        [Header("Dot Appearance")]
        [Tooltip("Radius of each fingertip dot in world units.")]
        [SerializeField] private float dotRadius = 0.008f;

        [Tooltip("Default opacity when not interacting.")]
        [Range(0f, 1f)]
        [SerializeField] private float defaultOpacity = 0.5f;

        [Tooltip("Opacity when interacting (pinching).")]
        [Range(0f, 1f)]
        [SerializeField] private float pinchOpacity = 1.0f;
        
        [Tooltip("Thickness of the ring.")]
        [SerializeField] private float ringThickness = 0.002f;

        // 5 dots: Thumb, Index, Middle, Ring, Pinky
        private GameObject[] _dots;
        private MeshRenderer[] _renderers;
        private Material _ringMaterial;

        private MaterialPropertyBlock _propBlock;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private bool[] _lastHighlightStates = new bool[5]; // UI Rebuild Guard
        private Camera _mainCamera;
        private Mesh[] _ringMeshes; // Track procedural meshes for explicit cleanup

        void Start()
        {
            _mainCamera = Camera.main;
            if (_mainCamera == null) _mainCamera = GameObject.FindGameObjectWithTag("MainCamera")?.GetComponent<Camera>();

            if (handTracker == null)
                handTracker = FindAnyObjectByType<HandTrackingManager>();

            _propBlock = new MaterialPropertyBlock();

            // Find a URP-compatible shader with fallbacks
            Shader shader = FindCompatibleShader();
            if (shader == null)
            {
                Debug.LogError("[FingertipVisualizer] No compatible shader found! Dots will not be visible.");
                enabled = false;
                return;
            }

            // Create material (Transparent)
            _ringMaterial = CreateTransparentMaterial(shader, Color.white);

            // Create 5 dot spheres
            _dots = new GameObject[5];
            _renderers = new MeshRenderer[5];
            _ringMeshes = new Mesh[5];
            string[] names = { "Dot_Thumb", "Dot_Index", "Dot_Middle", "Dot_Ring", "Dot_Pinky" };

            for (int i = 0; i < 5; i++)
            {
                var dot = CreateRingMesh();
                dot.name = names[i];
                dot.transform.localScale = Vector3.one * dotRadius * 2f;
                dot.transform.SetParent(transform);

                // Remove collider — we don't need physics on dots
                var col = dot.GetComponent<Collider>();
                if (col != null) Destroy(col);

                _renderers[i] = dot.GetComponent<MeshRenderer>();
                _renderers[i].material = _ringMaterial;
                _renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderers[i].receiveShadows = false;

                // Cache the procedural mesh reference for cleanup
                _ringMeshes[i] = dot.GetComponent<MeshFilter>().sharedMesh;

                dot.SetActive(false); // hidden until hand detected
                _dots[i] = dot;
            }

            Debug.Log($"[FingertipVisualizer] Initialized with shader: {shader.name}");
        }

        /// <summary>
        /// Tries multiple shader names to find one that exists in the current render pipeline.
        /// URP projects don't have "Unlit/Color", and built-in projects don't have URP shaders.
        /// </summary>
        private Shader FindCompatibleShader()
        {
            // Try in order: URP Unlit → URP Lit → Built-in Unlit → Sprites/Default (always exists)
            string[] shaderNames =
            {
                "Universal Render Pipeline/Unlit",
                "Universal Render Pipeline/Simple Lit",
                "Unlit/Color",
                "Sprites/Default"
            };

            foreach (var name in shaderNames)
            {
                var s = Shader.Find(name);
                if (s != null)
                {
                    Debug.Log($"[FingertipVisualizer] Using shader: {name}");
                    return s;
                }
            }

            return null;
        }

        /// <summary>
        /// Creates a hollow ring mesh
        /// </summary>
        private GameObject CreateRingMesh()
        {
            GameObject container = new GameObject("Ring");
            MeshFilter mf = container.AddComponent<MeshFilter>();
            MeshRenderer mr = container.AddComponent<MeshRenderer>();

            Mesh mesh = new Mesh();
            int segments = 32;
            
            float outerRadius = 0.5f; // Normalized so it scales properly later
            float innerRadius = outerRadius - (ringThickness / (dotRadius * 2f)); 
            if (innerRadius <= 0f) innerRadius = outerRadius * 0.6f;

            Vector3[] vertices = new Vector3[segments * 2];
            int[] triangles = new int[segments * 6];

            float angleStep = (Mathf.PI * 2f) / segments;

            for (int i = 0; i < segments; i++)
            {
                float angle = i * angleStep;
                float x = Mathf.Cos(angle);
                float y = Mathf.Sin(angle);

                vertices[i * 2] = new Vector3(x * outerRadius, y * outerRadius, 0); // Outer
                vertices[i * 2 + 1] = new Vector3(x * innerRadius, y * innerRadius, 0); // Inner

                int nextI = (i + 1) % segments;
                
                // Triangles (two per segment connecting inner to outer)
                int v0 = i * 2;
                int v1 = i * 2 + 1;
                int v2 = nextI * 2;
                int v3 = nextI * 2 + 1;

                int t = i * 6;
                triangles[t] = v0;
                triangles[t + 1] = v2;
                triangles[t + 2] = v1;

                triangles[t + 3] = v1;
                triangles[t + 4] = v2;
                triangles[t + 5] = v3;
            }

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            mf.mesh = mesh;
            return container;
        }

        /// <summary>
        /// Creates an unlit material with the given color, handling different shader property names.
        /// Ensure surface type is Transparent to support opacity modifications.
        /// </summary>
        private Material CreateTransparentMaterial(Shader shader, Color color)
        {
            var mat = new Material(shader);

            // URP shaders use "_BaseColor", built-in shaders use "_Color"
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);

            // For URP Unlit: set surface type to Transparent (1)
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1); // 1 = Transparent
                // Ensure proper blend modes for transparency
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            // Disable emission to keep it simple and bright
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", color * 0.5f);

            return mat;
        }

        void LateUpdate()
        {
            if (handTracker == null || _dots == null) return;

            bool visible = handTracker.IsHandDetected;

            // Show/hide all dots
            for (int i = 0; i < 5; i++)
            {
                if (_dots[i].activeSelf != visible)
                    _dots[i].SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            // Direct position assignment — all smoothing is handled by the single
            // OneEuroFilter layer in HandTrackingManager. No second smoothing layer needed.
            for (int i = 0; i < 5; i++)
            {
                _dots[i].transform.position = handTracker.FingertipWorldPositions[i];
                
                if (_mainCamera != null)
                {
                    _dots[i].transform.rotation = _mainCamera.transform.rotation;
                }
            }

            // Update Opacity instead of Color: thumb and index change on pinch
            bool pinching = handTracker.IsIndexPinching || handTracker.IsMiddlePinching;

            float targetOpacity = pinching ? pinchOpacity : defaultOpacity;

            // Apply opacity via property block to avoid material duplication
            Color currentColor = Color.white;
            currentColor.a = targetOpacity;

            _propBlock.SetColor(BaseColorId, currentColor);
            _propBlock.SetColor(ColorId, currentColor);

            // Both thumb (0) and index (1) or middle (2) should highlight for interactions
            for (int i = 0; i < 5; i++)
            {
                bool highlightThisFinger = pinching && (i == 0 || (handTracker.IsIndexPinching && i == 1) || (handTracker.IsMiddlePinching && i == 2));
                
                // UI Rebuild Guard: only update properties if state actually changed
                if (highlightThisFinger != _lastHighlightStates[i])
                {
                    _lastHighlightStates[i] = highlightThisFinger;
                    Color c = Color.white;
                    c.a = highlightThisFinger ? pinchOpacity : defaultOpacity;
                    
                    _propBlock.SetColor(BaseColorId, c);
                    _propBlock.SetColor(ColorId, c);
                    _renderers[i].SetPropertyBlock(_propBlock);
                }
            }
        }

        void OnDestroy()
        {
            if (_ringMaterial != null) Destroy(_ringMaterial);
            // Destroy procedural ring meshes to prevent GPU VRAM leaks
            if (_ringMeshes != null)
            {
                for (int i = 0; i < _ringMeshes.Length; i++)
                    if (_ringMeshes[i] != null) Destroy(_ringMeshes[i]);
            }
        }
    }
}
