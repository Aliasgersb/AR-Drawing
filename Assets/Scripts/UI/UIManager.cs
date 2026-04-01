using UnityEngine;
using System.Collections.Generic;

namespace SpatialDrawing.UI
{
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }

        public List<UIState> states;
        private UIState currentState;

        void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        void Start()
        {
            // Find all UIState components if not assigned
            if (states == null || states.Count == 0)
            {
                // UI Manager might be a sibling to the states under Canvas
                states = new List<UIState>(transform.parent.GetComponentsInChildren<UIState>(true));
            }
            ChangeState(UIState.StateType.Onboarding, true);
        }

        public void ChangeState(UIState.StateType newState, bool instant = false)
        {
            if (currentState != null && currentState.Type == newState) return;

            foreach (var state in states)
            {
                if (state.Type == newState)
                {
                    if (currentState != null) currentState.Hide(instant);
                    state.Show(instant);
                    currentState = state;
                    break;
                }
            }
        }
        
        // Expose to buttons
        public void GoToMainHUD() => ChangeState(UIState.StateType.MainHUD);
        public void GoToGallery() => ChangeState(UIState.StateType.Gallery);
        public void GoToSettings() => ChangeState(UIState.StateType.Settings);
        public void GoToOnboarding() => ChangeState(UIState.StateType.Onboarding);
    }
}
