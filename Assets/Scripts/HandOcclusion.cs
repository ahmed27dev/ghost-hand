// OWNER: Member 3. Finds the hand in the camera frame and returns its convex hull,
// then hides the 3D parts under it (ShowMask).
// Detection: pixel is "hand" if it is skin-coloured (YCrCb) AND its colour differs from the
// reference sheet warped into the frame with H. Clean up -> biggest blob + nearby finger
// pieces -> convex hull. Short hold against flicker.
// Hull output: frame pixels, top-left origin. Never throws: returns an empty array on failure.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using OpenCvSharp;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;

public class HandOcclusion : MonoBehaviour
{
    public enum DebugView { Final, SkinOnly, DifferenceOnly }

    [Header("Detection")]
    public float workScale = 0.5f;          // process at half resolution for speed
    public int diffThreshold = 25;          // how different in colour from the sheet (0-255)
    public int crMin = 130, crMax = 180;    // skin range, YCrCb
    public int cbMin = 70, cbMax = 135;
    public float minAreaFraction = 0.015f;  // the main hand blob must be at least this share of the image
    public bool requireDifference = true;   // skin AND different-from-sheet (recommended)

    [Header("Fingers and flicker")]
    public float minPieceFraction = 0.002f; // smaller pieces (fingers) joined to the hand if close enough
    public int joinDistance = 25;           // pixels (at work size) a piece may be away from the hand
    public int holdFrames = 6;              // keep the last outline this many frames if the hand is missed

    [Header("Hiding parts under the hand")]
    public bool hideUnderHand = true;
    public float maskGrow = 1.08f;          // enlarge the outline a little so finger edges are covered
    public float maskDistance = 1f;         // must be closer to the camera than StepManager.partDepth (2)

    [Header("Debug")]
    public bool showDebugMask = true;       // small window bottom-right: red = detected
    public DebugView debugView = DebugView.Final;

    [Header("Debug (read-only)")]
    public int lastHullPoints;
    public float lastAreaFraction;
    public float lastMs;

    static readonly Vector2[] Empty = new Vector2[0];

    float[] H;
    Mat refBgr, refWhite, kernelSmall, kernelBig;
    Mat rgba, bgr, small, ycc, skin, warped, warpedMask, diff3, diff, diffMask, mask, flipped;
    byte[] raw, debugBytes;
    Texture2D debugTex;
    RawImage debugImg;
    bool warned, maskWarned;

    Vector2[] lastHull = Empty;
    int missFrames;

    // hiding
    RawImage background;
    Camera cam;
    Canvas patchCanvas;
    HandPatch patch;
    readonly Vector2[] patchLocal = new Vector2[256];
    readonly Vector2[] patchUv = new Vector2[256];
    readonly Vector3[] corners = new Vector3[4];

    // Called by ARController every frame before GetHandHull (null when not tracking).
    public void SetHomography(float[] h) { H = h; }

    void Start()
    {
        rgba = new Mat(); bgr = new Mat(); small = new Mat(); ycc = new Mat(); skin = new Mat();
        warped = new Mat(); warpedMask = new Mat(); diff3 = new Mat(); diff = new Mat();
        diffMask = new Mat(); mask = new Mat(); flipped = new Mat();
        kernelSmall = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        kernelBig = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));

        LoadReference();
        if (showDebugMask) BuildDebugView();
        BuildPatch();
    }

    void LoadReference()
    {
        try
        {
            string path = Path.Combine(Application.streamingAssetsPath, "reference.jpg");
            refBgr = Cv2.ImRead(path, ImreadModes.Color);
            if (refBgr.Empty())
            {
                Debug.LogError("HandOcclusion: could not read " + path);
                refBgr.Dispose();
                refBgr = null;
                return;
            }
            Cv2.GaussianBlur(refBgr, refBgr, new Size(5, 5), 0);
            refWhite = new Mat(refBgr.Size(), MatType.CV_8UC1, Scalar.All(255));
            Debug.Log("HandOcclusion: reference loaded " + refBgr.Width + "x" + refBgr.Height);
        }
        catch (Exception e)
        {
            Debug.LogError("HandOcclusion: reference load failed: " + e.Message);
            refBgr = null;
        }
    }

    // ---------------------------------------------------------------- detection

    public Vector2[] GetHandHull(Texture2D frame)
    {
        float t0 = Time.realtimeSinceStartup;
        try
        {
            var hull = Detect(frame);

            // Hold the last outline briefly so parts don't blink when the hand is missed for a frame
            if (hull.Length > 0) { lastHull = hull; missFrames = 0; }
            else if (lastHull.Length > 0 && missFrames < holdFrames && H != null) { missFrames++; hull = lastHull; }
            else lastHull = Empty;

            lastHullPoints = hull.Length;
            return hull;
        }
        catch (Exception e)
        {
            if (!warned) { Debug.LogWarning("HandOcclusion: " + e.Message); warned = true; }
            lastHullPoints = 0;
            return Empty;
        }
        finally
        {
            lastMs = (Time.realtimeSinceStartup - t0) * 1000f;
        }
    }

    Vector2[] Detect(Texture2D frame)
    {
        lastAreaFraction = 0f;
        if (frame == null || frame.width <= 16 || frame.height <= 16) return Empty;
        if (requireDifference && (H == null || H.Length < 9 || refBgr == null)) { ClearDebug(); return Empty; }

        int w = frame.width, h = frame.height, n = w * h * 4;

        // Texture2D (RGBA32, bottom row first) -> Mat BGR, top row first
        var data = frame.GetRawTextureData<byte>();
        if (data.Length < n) return Empty;
        if (raw == null || raw.Length != n) raw = new byte[n];
        NativeArray<byte>.Copy(data, raw, n);

        if (rgba.Rows != h || rgba.Cols != w || rgba.Type() != MatType.CV_8UC4)
        {
            rgba.Dispose();
            rgba = new Mat(h, w, MatType.CV_8UC4);
        }
        Marshal.Copy(raw, 0, rgba.Data, n);
        Cv2.CvtColor(rgba, bgr, ColorConversionCodes.RGBA2BGR);
        Cv2.Flip(bgr, bgr, FlipMode.X);

        // Work at reduced size
        int sw = Mathf.Max(32, Mathf.RoundToInt(w * workScale));
        int sh = Mathf.Max(32, Mathf.RoundToInt(h * workScale));
        double sx = sw / (double)w, sy = sh / (double)h;
        Cv2.Resize(bgr, small, new Size(sw, sh), 0, 0, InterpolationFlags.Area);
        Cv2.GaussianBlur(small, small, new Size(5, 5), 0);

        // 1) Skin colour
        Cv2.CvtColor(small, ycc, ColorConversionCodes.BGR2YCrCb);
        Cv2.InRange(ycc, new Scalar(0, crMin, cbMin), new Scalar(255, crMax, cbMax), skin);

        if (requireDifference)
        {
            // 2) Warp the reference sheet into this (small) frame: Hs = S * H
            using (var Hs = new Mat(3, 3, MatType.CV_64FC1))
            {
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        double k = r == 0 ? sx : r == 1 ? sy : 1.0;
                        Hs.Set<double>(r, c, H[r * 3 + c] * k);
                    }
                Cv2.WarpPerspective(refBgr, warped, Hs, new Size(sw, sh), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                Cv2.WarpPerspective(refWhite, warpedMask, Hs, new Size(sw, sh), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
            }
            Cv2.Erode(warpedMask, warpedMask, kernelBig);   // ignore the sheet border

            // 3) Colour-matched difference: match each channel's average (phone photo vs live camera)
            Scalar mF = Cv2.Mean(small, warpedMask);
            Scalar mR = Cv2.Mean(warped, warpedMask);
            Cv2.Split(warped, out Mat[] wch);
            for (int c = 0; c < 3; c++)
            {
                double k = mR[c] > 1.0 ? mF[c] / mR[c] : 1.0;
                wch[c].ConvertTo(wch[c], MatType.CV_8UC1, k, 0);
            }
            Cv2.Merge(wch, warped);
            foreach (var m in wch) m.Dispose();

            // biggest difference of the three colour channels
            Cv2.Absdiff(small, warped, diff3);
            Cv2.Split(diff3, out Mat[] dch);
            Cv2.Max(dch[0], dch[1], diff);
            Cv2.Max(diff, dch[2], diff);
            foreach (var m in dch) m.Dispose();
            Cv2.Threshold(diff, diffMask, diffThreshold, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(diffMask, warpedMask, diffMask);

            // hand = skin AND different AND on the sheet
            Cv2.BitwiseAnd(skin, diffMask, mask);
        }
        else
        {
            skin.CopyTo(mask);
        }

        // 4) Clean up: remove specks, fill gaps between fingers
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernelSmall);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernelBig);

        Mat view = debugView == DebugView.SkinOnly ? skin
                 : debugView == DebugView.DifferenceOnly && requireDifference ? diffMask
                 : mask;
        UpdateDebug(view, sw, sh);

        // 5) Biggest blob = the hand
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hier,
                         RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) return Empty;

        var areas = new double[contours.Length];
        int best = -1;
        double bestArea = 0;
        for (int i = 0; i < contours.Length; i++)
        {
            areas[i] = Cv2.ContourArea(contours[i]);
            if (areas[i] > bestArea) { bestArea = areas[i]; best = i; }
        }
        double imgArea = sw * (double)sh;
        lastAreaFraction = (float)(bestArea / imgArea);
        if (best < 0 || lastAreaFraction < minAreaFraction) return Empty;

        // 6) Join nearby pieces (fingers detected separately) to the hand
        var handBox = Cv2.BoundingRect(contours[best]);
        int x0 = handBox.X - joinDistance, y0 = handBox.Y - joinDistance;
        int x1 = handBox.X + handBox.Width + joinDistance, y1 = handBox.Y + handBox.Height + joinDistance;

        var pts = new List<Point>(contours[best]);
        for (int i = 0; i < contours.Length; i++)
        {
            if (i == best || areas[i] < minPieceFraction * imgArea) continue;
            var b = Cv2.BoundingRect(contours[i]);
            bool near = b.X <= x1 && b.X + b.Width >= x0 && b.Y <= y1 && b.Y + b.Height >= y0;
            if (near) pts.AddRange(contours[i]);
        }

        Point[] hull = Cv2.ConvexHull(pts);
        var result = new Vector2[hull.Length];
        for (int i = 0; i < hull.Length; i++)
            result[i] = new Vector2((float)(hull[i].X / sx), (float)(hull[i].Y / sy));
        return result;
    }

    // ---------------------------------------------------------------- hiding parts under the hand

    void BuildPatch()
    {
        var ar = FindObjectOfType<ARController>();
        if (ar != null) background = ar.background;
        cam = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();
        if (background == null || cam == null) return;

        var go = new GameObject("HandMaskCanvas");
        patchCanvas = go.AddComponent<Canvas>();
        patchCanvas.renderMode = RenderMode.ScreenSpaceCamera;
        patchCanvas.worldCamera = cam;
        patchCanvas.planeDistance = maskDistance;
        patchCanvas.sortingOrder = 10;                 // draw after (on top of) the 3D parts

        var pgo = new GameObject("HandPatch", typeof(RectTransform), typeof(CanvasRenderer));
        pgo.transform.SetParent(go.transform, false);
        patch = pgo.AddComponent<HandPatch>();
        patch.raycastTarget = false;
        var rt = patch.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    // Called by ARController with the hull from GetHandHull. Empty hull = nothing hidden.
    public void ShowMask(Vector2[] hull)
    {
        if (patch == null) return;
        try
        {
            patchCanvas.planeDistance = maskDistance;
            Texture tex = background != null ? background.texture : null;
            if (!hideUnderHand || hull == null || hull.Length < 3 || tex == null || tex.width <= 16 || tex.height <= 16)
            {
                patch.SetPolygon(patchLocal, patchUv, 0);
                return;
            }

            float w = tex.width, h = tex.height;
            patch.source = tex;
            int n = Mathf.Min(hull.Length, patchLocal.Length);

            Vector2 centre = Vector2.zero;
            for (int i = 0; i < n; i++) centre += hull[i];
            centre /= n;

            for (int i = 0; i < n; i++)
            {
                Vector2 p = centre + (hull[i] - centre) * maskGrow;
                p.x = Mathf.Clamp(p.x, 0f, w);
                p.y = Mathf.Clamp(p.y, 0f, h);

                Vector2 screen = FrameToScreen(p, w, h);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(patch.rectTransform, screen, cam, out patchLocal[i]);
                patchUv[i] = new Vector2(p.x / w, 1f - p.y / h);   // texture uv, bottom-left origin
            }
            patch.SetPolygon(patchLocal, patchUv, n);
        }
        catch (Exception e)
        {
            if (!maskWarned) { Debug.LogWarning("HandOcclusion mask: " + e.Message); maskWarned = true; }
            patch.SetPolygon(patchLocal, patchUv, 0);
        }
    }

    // frame px (top-left origin) -> screen px, through the camera-feed RawImage
    Vector2 FrameToScreen(Vector2 f, float w, float h)
    {
        float u = f.x / w;
        float v = 1f - f.y / h;
        UnityEngine.Rect uv = background.uvRect;
        float s = (u - uv.x) / uv.width;
        float r = (v - uv.y) / uv.height;

        background.rectTransform.GetWorldCorners(corners);
        Canvas c = background.canvas != null ? background.canvas.rootCanvas : null;
        Camera cc = (c == null || c.renderMode == RenderMode.ScreenSpaceOverlay) ? null : c.worldCamera;
        Vector2 bl = RectTransformUtility.WorldToScreenPoint(cc, corners[0]);
        Vector2 tl = RectTransformUtility.WorldToScreenPoint(cc, corners[1]);
        Vector2 br = RectTransformUtility.WorldToScreenPoint(cc, corners[3]);
        return bl + s * (br - bl) + r * (tl - bl);
    }

    // ---------------------------------------------------------------- debug window

    void BuildDebugView()
    {
        var go = new GameObject("HandDebugCanvas");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 90;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);

        var img = new GameObject("HandMask", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        img.transform.SetParent(go.transform, false);
        debugImg = img.GetComponent<RawImage>();
        debugImg.raycastTarget = false;
        var rt = debugImg.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-10f, 100f);   // above the instruction bar
        rt.sizeDelta = new Vector2(240f, 180f);
        debugImg.enabled = false;
    }

    void UpdateDebug(Mat m, int sw, int sh)
    {
        if (!showDebugMask || debugImg == null) return;
        if (debugTex == null || debugTex.width != sw || debugTex.height != sh)
        {
            debugTex = new Texture2D(sw, sh, TextureFormat.R8, false);
            debugBytes = new byte[sw * sh];
            debugImg.texture = debugTex;
        }
        Cv2.Flip(m, flipped, FlipMode.X);                 // Unity textures are bottom row first
        Marshal.Copy(flipped.Data, debugBytes, 0, sw * sh);
        debugTex.LoadRawTextureData(debugBytes);
        debugTex.Apply(false);
        debugImg.enabled = true;
    }

    void ClearDebug()
    {
        if (debugImg != null) debugImg.enabled = false;
    }

    void OnDestroy()
    {
        foreach (var m in new[] { refBgr, refWhite, kernelSmall, kernelBig, rgba, bgr, small, ycc, skin,
                                  warped, warpedMask, diff3, diff, diffMask, mask, flipped })
            if (m != null) m.Dispose();
    }
}