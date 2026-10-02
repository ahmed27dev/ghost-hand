// OWNER: Member 3. STUB - replace with MediaPipe hands + convex hull + stencil mesh.
// CONTRACT: GetHandHull(frame) returns hull points in FRAME PIXEL coordinates
//           (origin top-left), or an empty array when no hand is visible.
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Mediapipe;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Unity;
using UnityEngine;

public class HandOcclusion : MonoBehaviour
{
    private HandLandmarker _handLandmarker;

    private IEnumerator Start()
    {
        yield return Mediapipe.Unity.Sample.AssetLoader.PrepareAssetAsync("hand_landmarker.bytes");

        var options = new HandLandmarkerOptions(
            baseOptions: new Mediapipe.Tasks.Core.BaseOptions(modelAssetPath: "hand_landmarker.bytes"),
            runningMode: Mediapipe.Tasks.Vision.Core.RunningMode.IMAGE,
            numHands: 1
        );
        _handLandmarker = HandLandmarker.CreateFromOptions(options);
    }

    // Agreed team interface: returns hull points in frame pixel coordinates.
    // Returns an empty array (never null) if no hand is detected.
    public Vector2[] GetHandHull(Texture2D frame)
    {
        if (_handLandmarker == null || frame == null)
        {
            return new Vector2[0];
        }

        // Build a MediaPipe Image from the Texture2D (CPU path)
        var textureFrame = new Mediapipe.Unity.Experimental.TextureFrame(frame.width, frame.height, TextureFormat.RGBA32);
        textureFrame.ReadTextureOnCPU(frame, false, false);
        var image = textureFrame.BuildCPUImage();
        textureFrame.Release();

        var result = HandLandmarkerResult.Alloc(1);
        bool detected = _handLandmarker.TryDetect(image, new ImageProcessingOptions(), ref result);

        if (!detected || result.handLandmarks == null || result.handLandmarks.Count == 0)
        {
            return new Vector2[0];
        }

        // Convert normalized (0-1) landmarks to pixel coordinates
        var points = new List<Vector2>();
        foreach (var landmark in result.handLandmarks[0].landmarks)
        {
            float px = landmark.x * frame.width;
            float py = landmark.y * frame.height;
            points.Add(new Vector2(px, py));
        }

        return ComputeConvexHull(points);
    }

    // Standard monotone chain convex hull algorithm
    private Vector2[] ComputeConvexHull(List<Vector2> points)
    {
        if (points.Count < 3)
        {
            return points.ToArray();
        }

        points = points.OrderBy(p => p.x).ThenBy(p => p.y).ToList();

        List<Vector2> lower = new List<Vector2>();
        foreach (var p in points)
        {
            while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
            {
                lower.RemoveAt(lower.Count - 1);
            }
            lower.Add(p);
        }

        List<Vector2> upper = new List<Vector2>();
        for (int i = points.Count - 1; i >= 0; i--)
        {
            var p = points[i];
            while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0)
            {
                upper.RemoveAt(upper.Count - 1);
            }
            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);

        return lower.ToArray();
    }

    private float Cross(Vector2 o, Vector2 a, Vector2 b)
    {
        return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }

    private void OnDestroy()
    {
        _handLandmarker?.Close();
    }
}
