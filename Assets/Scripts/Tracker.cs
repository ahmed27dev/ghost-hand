// OWNER: Member 1 - tracking core.
// CONTRACT: GetHomography(frame) -> float[9] row-major 3x3 mapping
//           StreamingAssets/reference.jpg pixels -> current frame pixels, or null if not found.
//
// How it works:
//   DETECT  : ORB features on the frame, matched against the reference image with a
//             brute-force Hamming matcher + Lowe's ratio test, then RANSAC homography.
//   TRACK   : once locked, Lucas-Kanade optical flow moves the points frame to frame,
//             which is much cheaper than re-matching every frame.
//   RECOVER : if too few points survive, or the homography looks broken, go back to DETECT.
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using OpenCvSharp;
using UnityEngine;

public class Tracker : MonoBehaviour
{
    [Header("Detection")]
    public int maxFeatures = 800;
    public float ratioThreshold = 0.75f;     // Lowe's ratio: lower = stricter, fewer bad matches
    public int minMatches = 15;              // below this, no homography is attempted
    public float ransacThreshold = 3.0f;     // pixels

    [Header("Tracking")]
    public int minTrackedPoints = 12;        // below this, re-detect
    public int redetectEveryNFrames = 60;    // periodic refresh even when tracking is fine
    public float smoothing = 0.5f;           // 0 = no smoothing, 0.9 = very smooth but laggy

    [Header("Debug (read-only)")]
    public string state = "INIT";
    public int lastMatchCount;
    public int trackedPoints;
    public float lastMs;

    // reference data
    Mat refGray;
    KeyPoint[] refKeys;
    Mat refDesc;
    float refScale = 1f;                     // original reference px -> processed reference px

    // detectors
    ORB orb;
    BFMatcher matcher;

    // tracking state
    Mat prevGray;
    Point2f[] prevPts;                       // current positions in the frame
    Point2f[] refPts;                        // matching positions in the processed reference
    int framesSinceDetect;
    float[] smoothedH;

    const int ProcessWidth = 640;            // reference is scaled to this width for speed

    void Start()
    {
        orb = ORB.Create(maxFeatures);
        matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
        LoadReference();
    }

    void LoadReference()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "reference.jpg");
        if (!File.Exists(path))
        {
            Debug.LogError("Tracker: reference.jpg not found at " + path);
            enabled = false;
            return;
        }

        byte[] bytes = File.ReadAllBytes(path);
        using (Mat raw = Cv2.ImDecode(bytes, ImreadModes.Grayscale))
        {
            if (raw.Empty())
            {
                Debug.LogError("Tracker: could not decode reference.jpg");
                enabled = false;
                return;
            }

            refScale = ProcessWidth / (float)raw.Width;
            refGray = new Mat();
            Cv2.Resize(raw, refGray, new Size(ProcessWidth, Mathf.RoundToInt(raw.Height * refScale)));
        }

        refDesc = new Mat();
        orb.DetectAndCompute(refGray, null, out refKeys, refDesc);
        Debug.Log($"Tracker: reference loaded {refGray.Width}x{refGray.Height}, {refKeys.Length} keypoints");

        if (refKeys.Length < 100)
            Debug.LogWarning("Tracker: reference has few keypoints - use a more detailed target image.");
    }

    public float[] GetHomography(Texture2D frame)
    {
        if (refDesc == null || refDesc.Empty()) return null;

        float t0 = Time.realtimeSinceStartup;
        float[] result = null;

        using (Mat gray = TextureToGray(frame))
        {
            bool haveTrack = prevGray != null && prevPts != null && prevPts.Length >= minTrackedPoints;

            if (haveTrack && framesSinceDetect < redetectEveryNFrames)
                result = TrackStep(gray);

            if (result == null)
                result = DetectStep(gray);

            prevGray?.Dispose();
            prevGray = gray.Clone();
        }

        lastMs = Mathf.Lerp(lastMs, (Time.realtimeSinceStartup - t0) * 1000f, 0.2f);
        return result;
    }

    // --- DETECT: full ORB match against the reference ---
    float[] DetectStep(Mat gray)
    {
        state = "DETECT";
        framesSinceDetect = 0;

        KeyPoint[] keys;
        using (Mat desc = new Mat())
        {
            orb.DetectAndCompute(gray, null, out keys, desc);
            if (desc.Empty() || keys.Length < minMatches) { Reset(); return null; }

            DMatch[][] knn = matcher.KnnMatch(desc, refDesc, 2);
            var framePts = new List<Point2f>();
            var referencePts = new List<Point2f>();

            foreach (var pair in knn)
            {
                if (pair.Length < 2) continue;
                // Lowe's ratio test - throws away ambiguous matches, which is what
                // protects us from the repeated breadboard hole pattern.
                if (pair[0].Distance < ratioThreshold * pair[1].Distance)
                {
                    framePts.Add(keys[pair[0].QueryIdx].Pt);
                    referencePts.Add(refKeys[pair[0].TrainIdx].Pt);
                }
            }

            lastMatchCount = framePts.Count;
            if (framePts.Count < minMatches) { Reset(); return null; }

            float[] H = ComputeHomography(referencePts, framePts, out Point2f[] inRef, out Point2f[] inFrame);
            if (H == null) { Reset(); return null; }

            refPts = inRef;
            prevPts = inFrame;
            trackedPoints = prevPts.Length;
            state = "LOCKED";
            return Smooth(H);
        }
    }

    // --- TRACK: Lucas-Kanade from the previous frame ---
    float[] TrackStep(Mat gray)
    {
        state = "TRACK";
        framesSinceDetect++;

        Point2f[] nextPts = new Point2f[prevPts.Length];
        byte[] status;
        float[] err;
        Cv2.CalcOpticalFlowPyrLK(prevGray, gray, prevPts, ref nextPts, out status, out err,
            new Size(21, 21), 3,
            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.Count, 30, 0.01));

        var keptRef = new List<Point2f>();
        var keptCur = new List<Point2f>();
        for (int i = 0; i < status.Length; i++)
        {
            if (status[i] == 1 && err[i] < 30f)
            {
                keptRef.Add(refPts[i]);
                keptCur.Add(nextPts[i]);
            }
        }

        trackedPoints = keptCur.Count;
        if (keptCur.Count < minTrackedPoints) return null;   // caller falls back to DETECT

        float[] H = ComputeHomography(keptRef, keptCur, out Point2f[] inRef, out Point2f[] inCur);
        if (H == null) return null;

        refPts = inRef;
        prevPts = inCur;
        return Smooth(H);
    }

    // RANSAC homography + inlier extraction. srcRef is in PROCESSED reference pixels.
    float[] ComputeHomography(IList<Point2f> srcRef, IList<Point2f> dstFrame,
                              out Point2f[] inlierRef, out Point2f[] inlierFrame)
    {
        inlierRef = null;
        inlierFrame = null;

        var src = new Point2d[srcRef.Count];
        var dst = new Point2d[dstFrame.Count];
        for (int i = 0; i < srcRef.Count; i++)
        {
            src[i] = new Point2d(srcRef[i].X, srcRef[i].Y);
            dst[i] = new Point2d(dstFrame[i].X, dstFrame[i].Y);
        }

        using (Mat mask = new Mat())
        using (Mat h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, ransacThreshold, mask))
        {
            if (h.Empty() || h.Rows != 3) return null;

            var keepRef = new List<Point2f>();
            var keepFrame = new List<Point2f>();
            for (int i = 0; i < mask.Rows; i++)
            {
                if (mask.At<byte>(i, 0) == 1)
                {
                    keepRef.Add(srcRef[i]);
                    keepFrame.Add(dstFrame[i]);
                }
            }
            if (keepRef.Count < minTrackedPoints) return null;

            inlierRef = keepRef.ToArray();
            inlierFrame = keepFrame.ToArray();

            // h maps PROCESSED reference -> frame. Fold in the scale so the
            // returned matrix maps ORIGINAL reference.jpg pixels -> frame.
            double s = refScale;
            var H = new float[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    double v = h.At<double>(r, c);
                    if (c < 2) v *= s;          // columns 0,1 scale the x,y input
                    H[r * 3 + c] = (float)v;
                }

            // normalise so H[8] == 1
            if (Mathf.Abs(H[8]) > 1e-9f)
                for (int i = 0; i < 9; i++) H[i] /= H[8];

            return IsSane(H) ? H : null;
        }
    }

    // Reject degenerate homographies (mirrored, collapsed or wildly skewed).
    bool IsSane(float[] H)
    {
        float det = H[0] * H[4] - H[1] * H[3];
        if (float.IsNaN(det) || Mathf.Abs(det) < 1e-4f || Mathf.Abs(det) > 1e4f) return false;
        if (Mathf.Abs(H[6]) > 0.01f || Mathf.Abs(H[7]) > 0.01f) return false;   // extreme perspective
        return true;
    }

    float[] Smooth(float[] H)
    {
        if (smoothedH == null || smoothing <= 0f) { smoothedH = (float[])H.Clone(); return H; }
        for (int i = 0; i < 9; i++)
            smoothedH[i] = Mathf.Lerp(H[i], smoothedH[i], smoothing);
        return (float[])smoothedH.Clone();
    }

    void Reset()
    {
        state = "SEARCHING";
        prevPts = null;
        refPts = null;
        trackedPoints = 0;
        smoothedH = null;
    }

    // Unity textures are bottom-up, OpenCV is top-down, hence the flip.
    static Mat TextureToGray(Texture2D tex)
    {
        byte[] raw = tex.GetRawTextureData();
        using (Mat rgba = new Mat(tex.height, tex.width, MatType.CV_8UC4))
        {
            Marshal.Copy(raw, 0, rgba.Data, raw.Length);
            Mat gray = new Mat();
            Cv2.CvtColor(rgba, gray, ColorConversionCodes.RGBA2GRAY);
            Cv2.Flip(gray, gray, FlipMode.X);
            return gray;
        }
    }

    void OnDestroy()
    {
        refGray?.Dispose();
        refDesc?.Dispose();
        prevGray?.Dispose();
        orb?.Dispose();
        matcher?.Dispose();
    }
}