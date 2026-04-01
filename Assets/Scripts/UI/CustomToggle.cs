using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace SpatialDrawing.UI
{
    [RequireComponent(typeof(RectTransform))]
    public class CustomToggle : MonoBehaviour, IPointerClickHandler
    {
        public bool IsOn = false;
        public Image TrackImage;
        public Image HandleImage;
        public RectTransform HandleRect;
        
        [Header("Colors")]
        public Color activeTrackColor = Color.white;
        public Color activeHandleColor = Color.black;
        public Color inactiveTrackColor = new Color(0, 0, 0, 0.4f); // Frost
        public Color inactiveHandleColor = new Color(1, 1, 1, 0.4f);
        
        [Header("Animation")]
        public float animDuration = 0.15f;
        public float handleOffset = 20f; // Distance from center

        private float targetX;

        void Start()
        {
            UpdateVisuals(true);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            IsOn = !IsOn;
            // Play haptic feedback on mobile if possible
#if UNITY_ANDROID || UNITY_IOS
            Handheld.Vibrate();
#endif
            UpdateVisuals(false);
        }

        private void UpdateVisuals(bool instant)
        {
            targetX = IsOn ? handleOffset : -handleOffset;
            
            Color targetTrack = IsOn ? activeTrackColor : inactiveTrackColor;
            Color targetHandle = IsOn ? activeHandleColor : inactiveHandleColor;

            if (instant)
            {
                HandleRect.anchoredPosition = new Vector2(targetX, 0);
                TrackImage.color = targetTrack;
                HandleImage.color = targetHandle;
            }
            else
            {
                StartCoroutine(AnimateToggle(targetX, targetTrack, targetHandle));
            }
        }

        private System.Collections.IEnumerator AnimateToggle(float tX, Color tTrack, Color tHandle)
        {
            float time = 0;
            Vector2 startPos = HandleRect.anchoredPosition;
            Color startTrack = TrackImage.color;
            Color startHandle = HandleImage.color;

            while (time < animDuration)
            {
                time += Time.deltaTime;
                float t = time / animDuration;
                float easedT = t * t * (3f - 2f * t); // Smooth step
                
                HandleRect.anchoredPosition = Vector2.Lerp(startPos, new Vector2(tX, 0), easedT);
                TrackImage.color = Color.Lerp(startTrack, tTrack, easedT);
                HandleImage.color = Color.Lerp(startHandle, tHandle, easedT);
                
                yield return null;
            }
            
            HandleRect.anchoredPosition = new Vector2(tX, 0);
            TrackImage.color = tTrack;
            HandleImage.color = tHandle;
        }
    }
}
