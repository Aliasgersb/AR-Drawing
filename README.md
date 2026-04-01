# AR Drawing

Draw floating 3D strokes in augmented reality using just your bare hand. No stylus. No controller.

---

## Demo

> Add a screenshot or screen-recording GIF here before publishing.
> Drop the file in this folder and replace this line with:
> `![AR Drawing Demo](demo.gif)`

---

## About

Most drawing apps are flat. They trap your creativity on a 2D canvas.

AR Drawing breaks that. Point your phone at any surface, raise your hand, and paint 3D strokes that float in the real world around you — anchored in space, visible through the camera.

---

## Features

- **Touchless hand tracking** — Real-time 21-point hand detection via MediaPipe at 60 fps, with adaptive filtering to keep strokes smooth and jitter-free
- **Gesture-driven interface** — Hold up an open palm to open a radial arc menu; use finger taps to navigate and select tools. No screen touches needed
- **Full drawing toolkit** — Multiple colors, brush sizes, an eraser, undo/redo, and canvas clear, all operated through gestures
- **Save and gallery** — Save drawings as stroke data with an auto-generated thumbnail; browse and reload past creations from a built-in gallery
- **3D inspector** — View any saved drawing in a dedicated screen where you can rotate, zoom, and twist it in 3D, then summon it back into your AR scene

---

## How It Works

AR Drawing handles the complex math quietly so the experience feels like magic:

- **Async camera pipeline** — Frames are processed for hand-tracking on a background thread so the core app never drops below 60fps
- **One Euro Filter** — A custom adaptive algorithm kills hand jitters when you're still, but reacts instantly when you move fast
- **Hysteresis boundaries** — Your pinch gesture has different thresholds for starting and stopping, preventing strokes from flickering if your hand trembles
- **Smart 2D eraser** — Instead of calculating expensive 3D raycasts, the app projects 3D strokes back into flat screen-space to instantly delete whatever your fingers wipe across
- **Offscreen rendering** — When you save, a hidden camera quietly snaps a pristine thumbnail of your 3D art on a pitch-black background

---

## Requirements

To run the app, you need an Android device running Android 7.0 or higher with ARCore support.
Check if your device is supported: [developers.google.com/ar/devices](https://developers.google.com/ar/devices)

---

## Installation

Download the latest `.apk` from the [Releases](../../releases) tab.

1. On your Android device, go to **Settings > Install Unknown Apps** and allow installs from your browser or file manager
2. Open the downloaded `.apk` and tap Install
3. Launch AR Drawing and grant camera permission when prompted

---

## Building from Source

Clone the repo and open the project folder in **Unity 6000.3.10f1**.

```
File > Build Settings > Android > Switch Platform > Build
```

The MediaPipe package is stored locally in `LocalPackages/com.github.homuler.mediapipe` and will resolve automatically on first open. No additional setup needed.

---

## Tech Stack

- Unity 6 (6000.3.10f1)
- AR Foundation 6.3 + ARCore 6.3
- MediaPipe Hand Landmarker 0.16.3
- Universal Render Pipeline (URP) 17.3
- Unity UI Toolkit
- C#, Android

---

## Author

**[Your Name]**
- GitHub: [@YOUR_USERNAME](https://github.com/YOUR_USERNAME)
- LinkedIn: [your-linkedin](https://linkedin.com/in/YOUR_PROFILE)

---

## License

MIT License — see [LICENSE](LICENSE) for details.
