using System.Collections.Generic;
using UnityEngine;

namespace SpatialDrawing
{
    /// <summary>
    /// Represents a single drawn line in 3D space.
    /// Renders as a true 3D tube mesh using an append-only architecture.
    /// Never calls mesh.Clear() during active drawing.
    /// </summary>
    public class DrawingLine : MonoBehaviour
    {
        // ── Public Properties (preserved for Eraser, Undo/Redo, and other systems) ──
        public List<Vector3> Points => _points;
        public List<Vector3> RawPoints => _rawPoints;
        public Color LineColor => _color;
        public float LineWidth => _width;
        public Bounds CachedBounds { get; private set; }

        // Dummy LineRenderer property kept for any external script that references it
        // It is immediately disabled and never used for rendering
        public LineRenderer Renderer => _dummyRenderer;

        // ── Private State ──
        private Color _color = Color.white;
        private float _width = 0.005f;

        // Points lists
        private List<Vector3> _points = new List<Vector3>();
        private List<Vector3> _rawPoints = new List<Vector3>();

        // Components
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _mesh;
        private LineRenderer _dummyRenderer;

        // World-space One Euro stabilization
        private OneEuroFilter3D _worldFilter;
        private Vector3 _lastStableWorldPos;
        private bool _hasFirstPoint;
        private const float DEAD_ZONE = 0.0003f;

        // Elastic tail state
        private bool _hasElasticTail;
        private Vector3 _lastElasticPosition;

        // ── Tube Mesh Constants ──
        private const int SIDES = 10;
        private const int SPLINE_RESOLUTION = 16; // Fixed — uniform, no overshoot
        private const int MAX_RAW_POINTS = 500;

        // Pre-computed trig lookup (static — computed once for all instances)
        private static float[] _cos;
        private static float[] _sin;
        private static bool _trigReady = false;

        // ── Tube Geometry Buffers (List-based, append-only) ──
        // These lists grow as the stroke grows. Committed entries are never modified.
        private List<Vector3> _verts = new List<Vector3>(4096);
        private List<Vector3> _norms = new List<Vector3>(4096);
        private List<Color> _cols  = new List<Color>(4096);
        private List<int>     _tris  = new List<int>(8192);

        // How many complete rings have been permanently committed to the mesh
        private int _committedRings = 0;
        private int _lastBuiltRawCount = 0;

        // Parallel transport frame — cached at each commit so we never recompute
        private Vector3 _lastUp = Vector3.up;

        // ── Initialization ──

        public void Initialize(Color color, float width, Material lineMaterial)
        {
            _color = color;
            _width = width;

            _worldFilter = new OneEuroFilter3D(0.5f, 1.5f);
            _hasFirstPoint = false;
            _hasElasticTail = false;

            // Lock transform to world origin so local space == world space
            transform.position   = Vector3.zero;
            transform.rotation   = Quaternion.identity;
            transform.localScale = Vector3.one;

            // Build trig lookup table once
            if (!_trigReady)
            {
                _cos = new float[SIDES];
                _sin = new float[SIDES];
                for (int i = 0; i < SIDES; i++)
                {
                    float a = i * Mathf.PI * 2f / SIDES;
                    _cos[i] = Mathf.Cos(a);
                    _sin[i] = Mathf.Sin(a);
                }
                _trigReady = true;
            }

            // Set up MeshFilter + MeshRenderer
            _meshFilter   = gameObject.AddComponent<MeshFilter>();
            _meshRenderer = gameObject.AddComponent<MeshRenderer>();

            // Find correct unlit vertex color material
            _meshRenderer.material = BuildMaterial(lineMaterial);

            // Create the mesh
            _mesh = new Mesh();
            _mesh.name = "TubeMesh";
            _mesh.MarkDynamic();
            _meshFilter.mesh = _mesh;

            // Dummy disabled LineRenderer for external property compatibility
            _dummyRenderer = gameObject.AddComponent<LineRenderer>();
            _dummyRenderer.enabled = false;
        }

        /// <summary>
        /// Populates the line with static saved points and triggers complete spline mesh generation.
        /// </summary>
        public void LoadPoints(List<Vector3> points)
        {
            _rawPoints = new List<Vector3>(points);
            _hasFirstPoint = true;
            if (_rawPoints.Count > 0) _lastStableWorldPos = _rawPoints[0];

            RebuildSplineAndMesh();
            CalculateBounds();
        }

        private Material BuildMaterial(Material originalMaterial)
        {
            if (originalMaterial != null) return new Material(originalMaterial);
            var s = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
            return new Material(s);
        }

        // ── Public API ──

        public void AddPoint(Vector3 worldPoint)
        {
            // Stage 1: Double filter removed to eliminate lag
            Vector3 filtered = worldPoint; 

            // Stage 2: Dead zone — prevent sub-pixel noise from creating new points
            if (_hasFirstPoint)
            {
                float distSq = (filtered - _lastStableWorldPos).sqrMagnitude;
                if (distSq < DEAD_ZONE * DEAD_ZONE)
                    filtered = Vector3.Lerp(_lastStableWorldPos, filtered, 0.06f);
            }

            _lastStableWorldPos = filtered;
            _hasFirstPoint = true;

            // Stage 3: Check if we moved far enough to commit a new raw point
            if (_rawPoints.Count > 0)
            {
                float dist = Vector3.Distance(_rawPoints[_rawPoints.Count - 1], filtered);
                if (dist < _width * 0.3f)
                {
                    // Too close — update elastic tail only, do not commit
                    UpdateElasticTail(filtered);
                    return;
                }
            }

            // Commit the new raw point
            _hasElasticTail = false;
            _rawPoints.Add(filtered);

            // Update rolling bounds to prevent AR frustum culling
            if (_rawPoints.Count == 1) 
                CachedBounds = new Bounds(filtered, Vector3.one * _width * 2f);
            else 
                CachedBounds.Encapsulate(filtered);

            // Decimate if over limit
            if (_rawPoints.Count > MAX_RAW_POINTS)
                DecimateOlderPoints();

            // Rebuild spline and flush to mesh
            RebuildSplineAndMesh();
        }

        public void FinalizeLine()
        {
            // Commit elastic tail as a real point if meaningful
            if (_hasElasticTail && _rawPoints.Count > 0)
            {
                float dist = Vector3.Distance(_rawPoints[_rawPoints.Count - 1], _lastElasticPosition);
                if (dist > DEAD_ZONE)
                    _rawPoints.Add(_lastElasticPosition);
                _hasElasticTail = false;
            }

            RebuildSplineAndMesh();
            CalculateBounds();
        }

        public void SetVisible(bool visible)
        {
            if (_meshRenderer != null)
                _meshRenderer.enabled = visible;
        }

        public void CalculateBounds()
        {
            if (_points.Count == 0) return;

            Vector3 min = _points[0];
            Vector3 max = _points[0];

            for (int i = 1; i < _points.Count; i++)
            {
                min = Vector3.Min(min, _points[i]);
                max = Vector3.Max(max, _points[i]);
            }

            Vector3 center = (min + max) * 0.5f;
            Vector3 size   = max - min;
            CachedBounds   = new Bounds(center, size);

            if (_mesh != null)
                _mesh.bounds = CachedBounds;
        }

        // ── Internal Drawing ──

        public void UpdateElasticTail(Vector3 livePos)
        {
            _lastElasticPosition = livePos;
            _hasElasticTail = true;
            
            // Temporarily expand bounds to include the elastic tail tip
            if (_rawPoints.Count > 0) CachedBounds.Encapsulate(livePos);

            RebuildSplineAndMesh();
        }

        private void DecimateOlderPoints()
        {
            int half = _rawPoints.Count / 2;
            for (int i = half - 1; i >= 1; i -= 2)
                _rawPoints.RemoveAt(i);

            // Reset committed rings so mesh is rebuilt cleanly after decimation
            // This happens rarely (only when stroke exceeds 500 points)
            // so the one-time rebuild cost is acceptable
            _committedRings = 0;
            _lastUp = Vector3.up;
            _verts.Clear();
            _norms.Clear();
            _cols.Clear();
            _tris.Clear();
            _lastBuiltRawCount = 0;
        }

        /// <summary>
        /// Rebuilds the spline points list and updates the tube mesh.
        /// Only appends new rings — never rebuilds already-committed geometry
        /// except after decimation.
        /// </summary>
        private void RebuildSplineAndMesh()
        {
            // Protect _lastUp and buffers from elastic tail pollution
            Vector3 savedUp = _lastUp;
            int savedCommitted = _committedRings;

            // Step 1: Rebuild full spline point list
            _points.Clear();
            BuildSpline(_hasElasticTail ? _lastElasticPosition : (Vector3?)null);
            _lastBuiltRawCount = _rawPoints.Count;

            if (_points.Count < 2) return;

            // Step 2: Determine how many rings we need total
            int totalRingsNeeded = _points.Count;
            int startRing = _committedRings;

            for (int r = startRing; r < totalRingsNeeded; r++)
            {
                AppendRing(r);
            }

            // Step 4: Upload everything to GPU
            UploadMesh();

            // Post-Upload Cleanup for Elastic Tail
            if (_hasElasticTail)
            {
                // We leave the mesh alone (it's already on the GPU).
                // However, we must trim our CPU-side triangle buffer back to the rigidly
                // committed length so the *next* frame's append doesn't duplicate them.
                
                int committedVertsCount = savedCommitted * SIDES;
                if (_verts.Count > committedVertsCount) _verts.RemoveRange(committedVertsCount, _verts.Count - committedVertsCount);
                if (_norms.Count > committedVertsCount) _norms.RemoveRange(committedVertsCount, _norms.Count - committedVertsCount);
                if (_cols.Count > committedVertsCount)  _cols.RemoveRange(committedVertsCount, _cols.Count - committedVertsCount);

                int committedTrisCount = 0;
                // Every ring AFTER the first (index 0) adds SIDES * 6 triangles.
                // So if we have savedCommitted rings, ring 0 has 0 tris. Ring 1 has SIDES*6.
                if (savedCommitted > 1) 
                {
                    committedTrisCount = (savedCommitted - 1) * SIDES * 6;
                }
                
                if (_tris.Count > committedTrisCount) 
                    _tris.RemoveRange(committedTrisCount, _tris.Count - committedTrisCount);

                // Restore counters so next committed addition starts exactly where it left off
                _committedRings = savedCommitted;
                _lastUp = savedUp;
            }
        }

        private void BuildSpline(Vector3? elasticTip)
        {
            // Need at least 2 raw points to build spline
            List<Vector3> src = _rawPoints;
            int srcCount = src.Count;

            if (srcCount == 0) return;

            if (srcCount == 1)
            {
                _points.Add(src[0]);
                if (elasticTip.HasValue && elasticTip.Value != src[0])
                    _points.Add(elasticTip.Value);
                return;
            }

            // Build Catmull-Rom spline through all raw points
            for (int i = 0; i < srcCount - 1; i++)
            {
                Vector3 p0 = i == 0 ? src[0] : src[i - 1];
                Vector3 p1 = src[i];
                Vector3 p2 = src[i + 1];
                Vector3 p3 = (i + 2 < srcCount) ? src[i + 2] : p2;

                _points.Add(p1);

                for (int j = 1; j <= SPLINE_RESOLUTION; j++)
                {
                    float t = j / (float)(SPLINE_RESOLUTION + 1);
                    _points.Add(CatmullRom(t, p0, p1, p2, p3));
                }
            }

            // Add final raw point
            _points.Add(src[srcCount - 1]);

            // Add elastic tip if present
            if (elasticTip.HasValue)
                _points.Add(elasticTip.Value);
        }

        private void AppendRing(int ringIndex)
        {
            Vector3 pos = _points[ringIndex];

            // Calculate forward direction for this ring
            Vector3 forward;
            if (ringIndex < _points.Count - 1)
            {
                // Normal case: look ahead to the next ring
                forward = (_points[ringIndex + 1] - pos).normalized;
            }
            else if (ringIndex > 0)
            {
                // FIX: The final ring must NOT look backward. Looking backward causes 
                // Ring N and Ring N-1 to have the exact same forward vector, flattening 
                // the curve into a rigid cylinder and crushing the Bishop frame.
                // Instead, the final ring aims at the incoming hand position to preserve curvature.
                
                Vector3 targetPath = _hasElasticTail ? _lastElasticPosition : _lastStableWorldPos;
                Vector3 delta = targetPath - pos;
                
                if (delta.sqrMagnitude > 0.000001f)
                    forward = delta.normalized;
                else
                    forward = (pos - _points[ringIndex - 1]).normalized; // Absolute fallback
            }
            else
            {
                // Edge case: literally the first point ever generated
                forward = Vector3.forward;
            }

            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.forward;

            // Bishop frame: parallel transport the up vector
            if (ringIndex == 0)
            {
                // Initialize up vector for first ring
                Vector3 helper = (Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.99f)
                    ? Vector3.right
                    : Vector3.up;
                _lastUp = Vector3.Cross(Vector3.Cross(forward, helper).normalized, forward).normalized;
            }
            else
            {
                // Project previous up onto plane perpendicular to new forward (Bishop Frame)
                float dot = Vector3.Dot(_lastUp, forward);
                Vector3 projected = _lastUp - dot * forward;
                
                // FIX: Epsilon stabilization to prevent frame collapse (0-vector) on sharp U-turns.
                // If projected is almost 0, the curve bent 90 degrees instantly. We maintain previous up.
                if (projected.sqrMagnitude > 0.00001f)
                    _lastUp = projected.normalized;
            }

            Vector3 right  = Vector3.Cross(_lastUp, forward).normalized;
            float   radius = _width * 0.5f;
            int     vBase  = _verts.Count;
            Color c   = _color;

            // Add SIDES vertices for this ring
            for (int i = 0; i < SIDES; i++)
            {
                Vector3 offset = (right * _cos[i] + _lastUp * _sin[i]) * radius;
                _verts.Add(pos + offset);
                _norms.Add(offset.normalized);
                _cols.Add(c);
            }

            // Connect this ring to the previous ring with triangles
            if (ringIndex > 0)
            {
                int prevBase = vBase - SIDES;
                for (int i = 0; i < SIDES; i++)
                {
                    int next = (i + 1) % SIDES;
                    // Triangle 1
                    _tris.Add(prevBase + i);
                    _tris.Add(vBase + i);
                    _tris.Add(vBase + next);
                    // Triangle 2
                    _tris.Add(prevBase + i);
                    _tris.Add(vBase + next);
                    _tris.Add(prevBase + next);
                }
            }

            _committedRings++;
        }

        private void UploadMesh()
        {
            if (_verts.Count == 0) return;

            // GC OPTIMIZATION: Do not clear the mesh every frame.
            // _mesh.Clear(false) destructively discards the existing native array bounds
            // causing Unity to re-allocate memory for the entire mesh every frame.
            // By directly overwriting the vertices/normals/colors/triangles, Unity detects
            // that the arrays are larger than the native buffer size and re-allocates ONLY
            // when the capacity limit is hit, avoiding the 60Hz memory thrill ride.

            _mesh.SetVertices(_verts);
            _mesh.SetNormals(_norms);
            _mesh.SetColors(_cols);
            _mesh.SetTriangles(_tris, 0, false);

            // Set extremely large bounds during active drawing to completely eliminate
            // camera frustum culling issues while the user is moving and drawing.
            // FinalizeLine() will calculate and set the exact tight bounds when the stroke ends.
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
        }

        // ── Math Utilities ──

        private static Vector3 CatmullRom(float t, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }
    }
}
