// MEMBER 1 - visual test for Tracker.cs. Draws the reference outline and the tracked
// points onto the live feed, so you can see whether the homography is correct.
// Use: new scene, Create Empty, add this component (it adds Tracker itself). Press Play.
using System.Runtime.InteropServices;
using OpenCvSharp;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Tracker))]
public class TrackerTest : MonoBehaviour
{
    public int camIndex = 1;
    public int width = 640;
    public int height = 480;

    Tracker tracker;
    WebCamTexture cam;
    Texture2D frameTex, displayTex;
    RawImage display;
    Text hud;

    void Start()
    {
        tracker = GetComponent<Tracker>();
        BuildUI();

        var devices = WebCamTexture.devices;
        if (devices.Length == 0) { Debug.LogError("No camera"); enabled = false; return; }
        int i = Mathf.Clamp(camIndex, 0, devices.Length - 1);
        Debug.Log("Camera: " + devices[i].name);
        cam = new WebCamTexture(devices[i].name, width, height, 30);
        cam.Play();
    }

    void Update()
    {
        if (cam == null || !cam.didUpdateThisFrame) return;

        if (frameTex == null || frameTex.width != cam.width || frameTex.height != cam.height)
        {
            frameTex = new Texture2D(cam.width, cam.height, TextureFormat.RGBA32, false);
            displayTex = new Texture2D(cam.width, cam.height, TextureFormat.RGBA32, false);
            display.texture = displayTex;
        }
        frameTex.SetPixels32(cam.GetPixels32());
        frameTex.Apply(false);

        float[] H = tracker.GetHomography(frameTex);

        using (Mat rgba = ToMat(frameTex))
        {
            if (H != null)
            {
                // Draw the reference image's four corners, warped into the frame.
                // If tracking is correct this quad sits exactly on the printed sheet.
                Vector2 size = ReferenceSize();
                Vector2[] corners = {
                    new Vector2(0, 0), new Vector2(size.x, 0),
                    new Vector2(size.x, size.y), new Vector2(0, size.y)
                };
                Point[] pts = new Point[4];
                for (int i = 0; i < 4; i++)
                {
                    Vector2 w = Warp(H, corners[i]);
                    pts[i] = new Point((int)w.x, (int)w.y);
                }
                Cv2.Polylines(rgba, new[] { pts }, true, new Scalar(0, 255, 0, 255), 3);

                // centre cross
                Vector2 c = Warp(H, new Vector2(size.x * 0.5f, size.y * 0.5f));
                Cv2.DrawMarker(rgba, new Point((int)c.x, (int)c.y), new Scalar(255, 0, 255, 255),
                    MarkerTypes.Cross, 24, 2);
            }

            ToTexture(rgba, displayTex);
        }

        hud.text = string.Format("{0} | matches {1} | tracked {2} | {3:0.0} ms | {4:0} FPS",
            tracker.state, tracker.lastMatchCount, tracker.trackedPoints, tracker.lastMs,
            1f / Mathf.Max(Time.unscaledDeltaTime, 1e-5f));

        if (Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.Escape)) { cam.Stop(); enabled = false; }
    }

    Vector2 refSizeCache = Vector2.zero;
    Vector2 ReferenceSize()
    {
        if (refSizeCache == Vector2.zero)
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "reference.jpg");
            using (Mat m = Cv2.ImRead(path, ImreadModes.Grayscale))
                refSizeCache = new Vector2(m.Width, m.Height);
        }
        return refSizeCache;
    }

    static Vector2 Warp(float[] H, Vector2 p)
    {
        float x = H[0] * p.x + H[1] * p.y + H[2];
        float y = H[3] * p.x + H[4] * p.y + H[5];
        float w = H[6] * p.x + H[7] * p.y + H[8];
        if (Mathf.Abs(w) < 1e-6f) w = 1e-6f;
        return new Vector2(x / w, y / w);
    }

    static Mat ToMat(Texture2D tex)
    {
        byte[] raw = tex.GetRawTextureData();
        Mat m = new Mat(tex.height, tex.width, MatType.CV_8UC4);
        Marshal.Copy(raw, 0, m.Data, raw.Length);
        Cv2.Flip(m, m, FlipMode.X);
        return m;
    }

    static void ToTexture(Mat rgbaMat, Texture2D tex)
    {
        using (Mat flipped = new Mat())
        {
            Cv2.Flip(rgbaMat, flipped, FlipMode.X);
            int len = (int)(flipped.Total() * flipped.ElemSize());
            byte[] buf = new byte[len];
            Marshal.Copy(flipped.Data, buf, 0, len);
            tex.LoadRawTextureData(buf);
            tex.Apply(false);
        }
    }

    void BuildUI()
    {
        var canvasGo = new GameObject("TestCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        var imgGo = new GameObject("Feed", typeof(RawImage));
        imgGo.transform.SetParent(canvasGo.transform, false);
        display = imgGo.GetComponent<RawImage>();
        var rt = display.rectTransform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

        var hudGo = new GameObject("TestHUD", typeof(Text));
        hudGo.transform.SetParent(canvasGo.transform, false);
        hud = hudGo.GetComponent<Text>();
        hud.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        hud.fontSize = 22;
        hud.color = Color.green;
        hudGo.AddComponent<Outline>().effectColor = Color.black;
        var hrt = hud.rectTransform;
        hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(0, 1);
        hrt.pivot = new Vector2(0, 1);
        hrt.anchoredPosition = new Vector2(16, -16);
        hrt.sizeDelta = new Vector2(1000, 40);
    }

    void OnDisable() { if (cam != null) cam.Stop(); }
}