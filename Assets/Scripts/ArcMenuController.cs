using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialDrawing
{
    /// <summary>
    /// State machine managing the arc menu logic.
    /// Fires events that DrawingEngine subscribes to for color/brush/tool changes.
    /// Listens to GestureDetector for menu open/close/cycle/confirm.
    /// </summary>
    public class ArcMenuController : MonoBehaviour
    {
        // ── Public Events (DrawingEngine subscribes to these) ──
        public event Action<Color> OnColorChanged;
        public event Action<float> OnBrushSizeChanged;
        public event Action OnEraserToggled;
        public event Action OnClearCanvas;
        public event Action OnSavePressed;

        /// <summary>Fires when the menu opens or closes. True = open.</summary>
        public event Action<bool> OnMenuStateChanged;
        /// <summary>Fires when the highlighted item index changes.</summary>
        public event Action<int> OnHighlightChanged;
        /// <summary>Fires when switching between main menu and a submenu.</summary>
        public event Action<MenuLevel, int> OnMenuLevelChanged;

        // ── References ──
        [Header("References")]
        [SerializeField] private GestureDetector gestureDetector;

        // ── Menu State ──
        public enum MenuLevel { Main, ColorSub, ThicknessSub }
        public MenuLevel CurrentLevel { get; private set; } = MenuLevel.Main;
        public int HighlightedIndex { get; private set; }
        public bool IsOpen { get; private set; }

        /// <summary>Time.time when the menu was last closed. Used by DrawingEngine for cooldown.</summary>
        public float LastClosedTime { get; private set; } = -1f;

        // ── Main Menu Items ──
        public enum MainMenuItem { Color, Thickness, Eraser, ClearCanvas, Save }
        public static readonly MainMenuItem[] MainItems = {
            MainMenuItem.Color,
            MainMenuItem.Thickness,
            MainMenuItem.Eraser,
            MainMenuItem.ClearCanvas,
            MainMenuItem.Save
        };

        // ── Color Submenu ──
        public static readonly string[] ColorNames = {
            "White", "Cyan", "Orange", "Green", "Purple", "Pink"
        };
        public static readonly Color[] Colors = {
            Color.white,
            new Color(0f, 0.706f, 0.847f),     // Cyan
            new Color(1f, 0.620f, 0.173f),      // Orange
            new Color(0f, 0.898f, 0.627f),      // Green
            new Color(0.659f, 0.333f, 0.969f),  // Purple
            new Color(0.957f, 0.247f, 0.369f),  // Pink
        };

        // ── Thickness Submenu ──
        public static readonly string[] ThicknessNames = { "Small", "Medium", "Large" };
        public static readonly float[] ThicknessSizes = { 0.005f, 0.012f, 0.022f };

        // ── Remembered selections ──
        private int _lastColorIndex = 0;
        private int _lastThicknessIndex = 0;

        // ── Current items count (depends on menu level) ──
        private int CurrentItemCount
        {
            get
            {
                return CurrentLevel switch
                {
                    MenuLevel.Main => MainItems.Length,
                    MenuLevel.ColorSub => Colors.Length,
                    MenuLevel.ThicknessSub => ThicknessSizes.Length,
                    _ => 0
                };
            }
        }

        void Start()
        {
            if (gestureDetector == null)
                gestureDetector = FindAnyObjectByType<GestureDetector>();

            if (gestureDetector != null)
            {
                gestureDetector.OnPalmMenuTriggered += OpenMenu;
                gestureDetector.OnPalmMenuDismissed += DismissOrGoBack;
                gestureDetector.OnForceCloseMenu += ForceClose;
                gestureDetector.OnCycleTap += CycleHighlight;
                gestureDetector.OnConfirmTap += ConfirmSelection;
            }
        }

        // ════════════════════════════════════════════════════════
        //  MENU OPEN / CLOSE
        // ════════════════════════════════════════════════════════

        private void OpenMenu()
        {
            if (IsOpen) return;

            IsOpen = true;
            CurrentLevel = MenuLevel.Main;
            HighlightedIndex = 0;

            OnMenuStateChanged?.Invoke(true);
            OnMenuLevelChanged?.Invoke(MenuLevel.Main, HighlightedIndex);
            OnHighlightChanged?.Invoke(HighlightedIndex);

            Debug.Log("[ArcMenu] Menu opened → Main menu");
        }

        private void CloseMenu()
        {
            if (!IsOpen) return;

            IsOpen = false;
            CurrentLevel = MenuLevel.Main;
            HighlightedIndex = 0;
            LastClosedTime = Time.time;

            OnMenuStateChanged?.Invoke(false);

            Debug.Log("[ArcMenu] Menu closed");
        }

        /// <summary>
        /// Called by the dismiss gesture. If in a submenu, goes back to main.
        /// If already at main level, closes the menu entirely.
        /// </summary>
        private void DismissOrGoBack()
        {
            if (!IsOpen) return;

            if (CurrentLevel != MenuLevel.Main)
            {
                // FIX #5: Go back to main menu instead of closing
                CurrentLevel = MenuLevel.Main;
                HighlightedIndex = 0;
                OnMenuLevelChanged?.Invoke(MenuLevel.Main, HighlightedIndex);
                OnHighlightChanged?.Invoke(HighlightedIndex);
                Debug.Log("[ArcMenu] Back to main menu");
            }
            else
            {
                CloseMenu();
            }
        }

        /// <summary>
        /// Called when tracking is lost or a timeout occurs. Closes the menu completely.
        /// </summary>
        private void ForceClose()
        {
            if (!IsOpen) return;
            CloseMenu();
            Debug.Log("[ArcMenu] Force closed from tracking loss or timeout");
        }

        // ════════════════════════════════════════════════════════
        //  CYCLE (Index+Thumb Tap)
        // ════════════════════════════════════════════════════════

        private void CycleHighlight()
        {
            if (!IsOpen) return;

            HighlightedIndex = (HighlightedIndex + 1) % CurrentItemCount;
            OnHighlightChanged?.Invoke(HighlightedIndex);

            Debug.Log($"[ArcMenu] Highlight → index {HighlightedIndex} in {CurrentLevel}");
        }

        // ════════════════════════════════════════════════════════
        //  CONFIRM (Middle+Thumb Tap)
        // ════════════════════════════════════════════════════════

        private void ConfirmSelection()
        {
            if (!IsOpen) return;

            switch (CurrentLevel)
            {
                case MenuLevel.Main:
                    ConfirmMainMenu();
                    break;
                case MenuLevel.ColorSub:
                    ConfirmColorSubmenu();
                    break;
                case MenuLevel.ThicknessSub:
                    ConfirmThicknessSubmenu();
                    break;
            }
        }

        private void ConfirmMainMenu()
        {
            var selectedItem = MainItems[HighlightedIndex];

            switch (selectedItem)
            {
                case MainMenuItem.Color:
                    // Open color submenu, start at remembered index
                    CurrentLevel = MenuLevel.ColorSub;
                    HighlightedIndex = _lastColorIndex;
                    OnMenuLevelChanged?.Invoke(MenuLevel.ColorSub, HighlightedIndex);
                    OnHighlightChanged?.Invoke(HighlightedIndex);
                    Debug.Log($"[ArcMenu] → Color submenu (starting at {ColorNames[_lastColorIndex]})");
                    break;

                case MainMenuItem.Thickness:
                    // Open thickness submenu, start at remembered index
                    CurrentLevel = MenuLevel.ThicknessSub;
                    HighlightedIndex = _lastThicknessIndex;
                    OnMenuLevelChanged?.Invoke(MenuLevel.ThicknessSub, HighlightedIndex);
                    OnHighlightChanged?.Invoke(HighlightedIndex);
                    Debug.Log($"[ArcMenu] → Thickness submenu (starting at {ThicknessNames[_lastThicknessIndex]})");
                    break;

                case MainMenuItem.Eraser:
                    OnEraserToggled?.Invoke();
                    CloseMenu();
                    Debug.Log("[ArcMenu] Eraser activated → menu closed");
                    break;

                case MainMenuItem.ClearCanvas:
                    OnClearCanvas?.Invoke();
                    CloseMenu();
                    Debug.Log("[ArcMenu] Clear canvas → menu closed");
                    break;

                case MainMenuItem.Save:
                    OnSavePressed?.Invoke();
                    CloseMenu();
                    Debug.Log("[ArcMenu] Save pressed → menu closed");
                    break;
            }
        }

        private void ConfirmColorSubmenu()
        {
            _lastColorIndex = HighlightedIndex;
            Color selectedColor = Colors[HighlightedIndex];
            OnColorChanged?.Invoke(selectedColor);
            CloseMenu();
            Debug.Log($"[ArcMenu] Color selected: {ColorNames[HighlightedIndex]}");
        }

        private void ConfirmThicknessSubmenu()
        {
            _lastThicknessIndex = HighlightedIndex;
            float selectedSize = ThicknessSizes[HighlightedIndex];
            OnBrushSizeChanged?.Invoke(selectedSize);
            CloseMenu();
            Debug.Log($"[ArcMenu] Thickness selected: {ThicknessNames[HighlightedIndex]}");
        }

        // ════════════════════════════════════════════════════════
        //  PUBLIC API
        // ════════════════════════════════════════════════════════

        /// <summary>Get the currently active color.</summary>
        public Color GetCurrentColor() => Colors[_lastColorIndex];
        /// <summary>Get the currently active brush size.</summary>
        public float GetCurrentBrushSize() => ThicknessSizes[_lastThicknessIndex];

        void OnDestroy()
        {
            if (gestureDetector != null)
            {
                gestureDetector.OnPalmMenuTriggered -= OpenMenu;
                gestureDetector.OnPalmMenuDismissed -= DismissOrGoBack;
                gestureDetector.OnForceCloseMenu -= ForceClose;
                gestureDetector.OnCycleTap -= CycleHighlight;
                gestureDetector.OnConfirmTap -= ConfirmSelection;
            }
        }
    }
}
