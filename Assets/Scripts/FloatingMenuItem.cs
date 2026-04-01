using UnityEngine;
using UnityEngine.UI;

namespace SpatialDrawing
{
    /// <summary>
    /// Lightweight component for items in the Floating UI row.
    /// Handles its own scale and outline highlighting.
    /// </summary>
    public class FloatingMenuItem : MonoBehaviour
    {
        private Image _iconImage;
        private Image _backgroundImage;
        private Outline _outline;

        private float _currentScale = 1f;
        private float _targetScale = 1f;
        private float _baseSize;

        public ArcMenuController.MainMenuItem? MainItemType { get; set; }

        public void Initialize(float size, Color highlightColor)
        {
            _baseSize = size;
            
            // 1. Create a background 'Box' (this is what made the sub-menu visible!)
            _backgroundImage = GetComponent<Image>();
            if (_backgroundImage == null) _backgroundImage = gameObject.AddComponent<Image>();
            _backgroundImage.color = new Color(0.2f, 0.2f, 0.2f, 0.8f); // Dark translucent box
            _backgroundImage.raycastTarget = false;
            
            // 2. Create the Icon as a CHILD of this box
            GameObject iconGo = new GameObject("Icon");
            iconGo.layer = 0; // Nuclear fix: Default layer
            iconGo.transform.SetParent(this.transform, false);
            _iconImage = iconGo.AddComponent<Image>();
            _iconImage.raycastTarget = false;
            _iconImage.preserveAspect = true;
            
            var iconRt = iconGo.GetComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0.15f, 0.15f); // 15% padding
            iconRt.anchorMax = new Vector2(0.85f, 0.85f);
            iconRt.offsetMin = Vector2.zero;
            iconRt.offsetMax = Vector2.zero;
            
            // FINAL FIX: Push icon SLIGHTLY FORWARD on Z.
            // Pos Z moves it toward the camera because of our LookRotation setup.
            iconRt.localPosition = new Vector3(0, 0, 0.005f); 
            
            // Ensure no transparency issues
            _iconImage.raycastTarget = false;
            _iconImage.useSpriteMesh = false;
            _iconImage.color = Color.white;
            
            // 3. Force sizes
            var rt = GetComponent<RectTransform>();
            if (rt == null) rt = gameObject.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);

            var le = GetComponent<LayoutElement>();
            if (le == null) le = gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = size;
            le.preferredHeight = size;

            // Outline is optional, we'll look for it
            _outline = GetComponent<Outline>();
            if (_outline == null)
            {
                _outline = gameObject.AddComponent<Outline>();
                _outline.effectColor = highlightColor;
                _outline.effectDistance = new Vector2(2, 2);
                _outline.enabled = false;
            }

            // DEFINITIVE FIX: Use NULL material. 
            // In Unity UI, material=null forces the use of the optimized, stencil-aware 
            // internal UI material. This is crucial for URP World Space layering.
            _backgroundImage.material = null;
            _iconImage.material = null;
        }



        void Update()
        {
            // OPTIMIZATION: Stop Idle Math & UI Dirtying
            // If we've essentially reached the target scale, stop recalculating and stop forcing the Transform to update.
            if (Mathf.Abs(_currentScale - _targetScale) < 0.001f)
            {
                if (_currentScale != _targetScale)
                {
                    _currentScale = _targetScale;
                    transform.localScale = Vector3.one * _currentScale;
                }
                return; // Sleep this script's Update logic
            }

            _currentScale = Mathf.Lerp(_currentScale, _targetScale, Time.deltaTime * 12f);
            transform.localScale = Vector3.one * _currentScale;
        }

        public void SetIcon(Sprite sprite)
        {
            if (_iconImage == null) return;
            
            _iconImage.gameObject.SetActive(true);
            
            // FINAL FIX: restoring full visibility with the working shader
            _iconImage.color = Color.white; 
            
            if (sprite != null)
            {
                _iconImage.sprite = sprite;
            }
        }

        public void SetColorDot(Color color)
        {
            if (_backgroundImage == null) return;
            _backgroundImage.color = color;
            if (_iconImage != null) _iconImage.gameObject.SetActive(false); // Hide icon for color dots
        }

        public void SetThicknessDot(float normalized)
        {
            if (_backgroundImage == null) return;
            if (_iconImage != null) _iconImage.gameObject.SetActive(false); // Hide icon
            
            _backgroundImage.sprite = null;
            _backgroundImage.color = Color.white;
            
            float scale = Mathf.Lerp(0.3f, 0.9f, normalized);
            var rt = GetComponent<RectTransform>();
            if (rt != null) rt.sizeDelta = new Vector2(_baseSize * scale, _baseSize * scale);
        }

        public void SetHighlighted(bool highlighted)
        {
            _targetScale = highlighted ? 1.35f : 1.0f;
            if (_outline) _outline.enabled = highlighted;
        }
    }
}
