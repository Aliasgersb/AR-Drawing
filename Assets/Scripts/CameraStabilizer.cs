using UnityEngine;

namespace SpatialDrawing.CameraFX
{
    /// <summary>
    /// Applies a post-TrackedPoseDriver exponential smoothing filter to the AR Camera.
    /// Eliminates both ARFoundation pose refinement jitter and natural human hand tremor.
    /// When OFF, this component bypasses completely, ensuring zero side-effects.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class CameraStabilizer : MonoBehaviour
    {
        [Tooltip("When disabled, the camera pose is left exactly as ARFoundation delivers it.")]
        public bool isStabilizationEnabled = false;

        // We use an exponential smoothing function (Lerp/Slerp with deltaTime).
        // 18.0f is carefully chosen to eliminate 8-12Hz hand tremor and AR sub-millimeter 
        // jitter, while staying highly responsive to major intentional movements without 
        // introducing "floaty" or "sluggish" lag.
        [SerializeField] private float positionSmoothSpeed = 18f;
        [SerializeField] private float rotationSmoothSpeed = 18f;

        private Vector3 _smoothedPosition;
        private Quaternion _smoothedRotation;
        private bool _isInitialized = false;

        public void SetStabilizationEnabled(bool enabled)
        {
            isStabilizationEnabled = enabled;
            if (!enabled)
            {
                // Reset initialization so the next time it turns ON, it snaps to current pose
                // rather than rubber-banding from an old saved pose.
                _isInitialized = false;
            }
        }

        private void OnEnable()
        {
            // Subscribe to BeforeRender. 
            // The AR TrackedPoseDriver updates in Update() and BeforeRender.
            // By subscribing in OnEnable, we process the pose right before rendering,
            // intercepting the raw ARFoundation output.
            Application.onBeforeRender += OnBeforeRenderHandler;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRenderHandler;
        }

        private void OnBeforeRenderHandler()
        {
            if (!isStabilizationEnabled) return;

            // Read the raw pose delivered by ARFoundation for this frame
            Vector3 rawPosition = transform.localPosition;
            Quaternion rawRotation = transform.localRotation;

            if (!_isInitialized)
            {
                _smoothedPosition = rawPosition;
                _smoothedRotation = rawRotation;
                _isInitialized = true;
                return;
            }

            // Exponential smoothing filter
            _smoothedPosition = Vector3.Lerp(_smoothedPosition, rawPosition, Time.deltaTime * positionSmoothSpeed);
            _smoothedRotation = Quaternion.Slerp(_smoothedRotation, rawRotation, Time.deltaTime * rotationSmoothSpeed);

            // Apply stabilized pose
            transform.localPosition = _smoothedPosition;
            transform.localRotation = _smoothedRotation;
        }
    }
}
