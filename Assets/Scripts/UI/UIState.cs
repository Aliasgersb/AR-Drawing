using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpatialDrawing.UI
{
    [RequireComponent(typeof(CanvasGroup))]
    public class UIState : MonoBehaviour
    {
        public enum StateType { Onboarding, MainHUD, Gallery, Settings }
        public StateType Type;
        
        private CanvasGroup canvasGroup;

        void Awake()
        {
            canvasGroup = GetComponent<CanvasGroup>();
            // Start hidden if not onboarding
            if(Type != StateType.Onboarding) Hide(true);
        }

        public void Show(bool instant = false)
        {
            gameObject.SetActive(true);
            if (instant)
            {
                canvasGroup.alpha = 1f;
                canvasGroup.interactable = true;
                canvasGroup.blocksRaycasts = true;
            }
            else
            {
                StartCoroutine(Fade(1f, 0.3f, () => {
                    canvasGroup.interactable = true;
                    canvasGroup.blocksRaycasts = true;
                }));
            }
        }

        public void Hide(bool instant = false)
        {
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
            if (instant)
            {
                canvasGroup.alpha = 0f;
                gameObject.SetActive(false);
            }
            else
            {
                StartCoroutine(Fade(0f, 0.3f, () => gameObject.SetActive(false)));
            }
        }

        private System.Collections.IEnumerator Fade(float targetAlpha, float duration, System.Action onComplete = null)
        {
            float startAlpha = canvasGroup.alpha;
            float time = 0;
            while (time < duration)
            {
                time += Time.deltaTime;
                // FastOutSlowIn custom easing
                float t = time / duration;
                float easedT = t * t * (3f - 2f * t); // SmoothStep
                canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, easedT);
                yield return null;
            }
            canvasGroup.alpha = targetAlpha;
            onComplete?.Invoke();
        }
    }
}
