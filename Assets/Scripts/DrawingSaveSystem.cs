using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpatialDrawing
{
    /// <summary>
    /// Serializable structures for saved data.
    /// Used instead of native types for clean JSON output.
    /// </summary>
    [Serializable]
    public class SerializableVector3
    {
        public float x, y, z;
        public SerializableVector3(Vector3 v) { x = v.x; y = v.y; z = v.z; }
        public Vector3 ToVector3() => new Vector3(x, y, z);
    }

    [Serializable]
    public class SerializableColor
    {
        public float r, g, b, a;
        public SerializableColor(Color c) { r = c.r; g = c.g; b = c.b; a = c.a; }
        public Color ToColor() => new Color(r, g, b, a);
    }

    [Serializable]
    public class StrokeSaveData
    {
        public List<SerializableVector3> rawPoints = new();
        public SerializableColor color;
        public float width;
    }

    [Serializable]
    public class DrawingSaveData
    {
        public string id;
        public string timestamp;
        public List<StrokeSaveData> strokes = new();
    }

    /// <summary>
    /// Handles physical IO serialization of drawing points and camera offscreen viewport capturing.
    /// </summary>
    public static class DrawingSaveSystem
    {
        private static string SaveDirectory => Path.Combine(Application.persistentDataPath, "Creations");

        /// <summary>
        /// Saves all lines into a persistent data file container and takes a screenshot snapshot.
        /// </summary>
        public static string SaveDrawing(List<DrawingLine> lines)
        {
            if (!Directory.Exists(SaveDirectory))
                Directory.CreateDirectory(SaveDirectory);

            string creationId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string timestampString = DateTime.Now.ToString("MMM d, yyyy");

            DrawingSaveData data = new DrawingSaveData
            {
                id = creationId,
                timestamp = timestampString
            };

            foreach (var line in lines)
            {
                if (line == null || line.RawPoints.Count == 0) continue;

                var strokeDict = new StrokeSaveData
                {
                    color = new SerializableColor(line.LineColor),
                    width = line.LineWidth
                };

                foreach (var pt in line.RawPoints)
                {
                    strokeDict.rawPoints.Add(new SerializableVector3(pt));
                }

                data.strokes.Add(strokeDict);
            }

            // 1. Serialize Coordinates into JSON
            string json = JsonUtility.ToJson(data, true);
            string dataPath = Path.Combine(SaveDirectory, $"{creationId}.json");
            File.WriteAllText(dataPath, json);

            Debug.Log($"[SaveSystem] Saved raw point mesh to: {dataPath}");

            // 2. Capture Thumbnail Snapshot Offscreen
            TakeThumbnailSnapshot(lines, Path.Combine(SaveDirectory, $"{creationId}.png"));

            return creationId;
        }

        /// <summary>
        /// Renders framing context of lines onto a hidden dark frame-buffer pipeline and triggers IO baked frame writing.
        /// </summary>
        private static void TakeThumbnailSnapshot(List<DrawingLine> lines, string savePath)
        {
            if (lines == null || lines.Count == 0) return;

            // ── EXCLUDE FLOATING MENU ──
            GameObject menuInstance = GameObject.Find("FloatingMenuCanvas_Runtime");
            bool wasMenuActive = menuInstance != null && menuInstance.activeSelf;
            if (wasMenuActive) menuInstance.SetActive(false);

            // ── FIX #11: SUPPRESS ALL SCENE LIGHTS before render ──
            // Without this the snapshot camera picks up scene directional/point lights
            // and the drawing thumbnail looks like a lit blob instead of clean dark-bg art.
            var lightsList = new List<Light>();
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (l.enabled) { l.enabled = false; lightsList.Add(l); }
            }
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;

            // Compute frame bounds
            Bounds fullBounds = lines[0].CachedBounds;
            for (int i = 1; i < lines.Count; i++)
            {
                if (lines[i] != null) fullBounds.Encapsulate(lines[i].CachedBounds);
            }

            // ── FIX #12: Determine layer mask from what the lines actually use ──
            // Default everything layer (0) but read from first available MeshRenderer
            int drawingLayer = 0;
            foreach (var line in lines)
            {
                if (line != null)
                {
                    drawingLayer = line.gameObject.layer;
                    break;
                }
            }
            // Culling mask: include the drawing layer AND Ignore Raycast (so nothing else bleeds in)
            int snapshotMask = (1 << drawingLayer);

            // Setup Snapshot Camera
            GameObject camObj = new GameObject("SnapshotCam");
            Camera snapCam = camObj.AddComponent<Camera>();
            snapCam.backgroundColor  = Color.black;
            snapCam.clearFlags       = CameraClearFlags.SolidColor;
            snapCam.orthographic     = false;
            snapCam.cullingMask      = snapshotMask;

            // Frame Camera around bound-center with padding space
            float radius   = fullBounds.extents.magnitude;
            float distance = (radius > 0.0001f)
                ? radius / Mathf.Sin(snapCam.fieldOfView * 0.5f * Mathf.Deg2Rad)
                : 0.5f;
            distance = Mathf.Max(distance, 0.2f);
            snapCam.transform.position = fullBounds.center - Vector3.forward * (distance * 1.5f);
            snapCam.transform.LookAt(fullBounds.center);

            // Generate Render Texture pipeline
            RenderTexture rt = new RenderTexture(512, 512, 24);
            snapCam.targetTexture = rt;
            snapCam.Render();

            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();

            byte[] bytes = tex.EncodeToPNG();
            File.WriteAllBytes(savePath, bytes);

            // ── Cleanup memory ──
            snapCam.targetTexture = null;
            RenderTexture.active  = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(camObj);

            // ── Restore lights & ambient ──
            foreach (var l in lightsList) if (l != null) l.enabled = true;
            RenderSettings.ambientMode  = prevAmbientMode;
            RenderSettings.ambientLight = prevAmbientLight;

            // Restore menu
            if (wasMenuActive && menuInstance != null) menuInstance.SetActive(true);

            Debug.Log($"[SaveSystem] Captured Thumbnail Snapshot buffer: {savePath}");
        }
    }
}
