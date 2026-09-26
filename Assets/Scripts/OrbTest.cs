// MEMBER 1 - Day 1 spike. Proves OpenCvSharp works in Unity before any real tracking code.
// Use: File > New Scene > Basic (Built-in). Create Empty GameObject. Add this component. Press Play.
// O = toggle ORB/AKAZE, Q or Esc = stop. Builds its own UI, so nothing to wire.
using System.Runtime.InteropServices;
using OpenCvSharp;
using UnityEngine;
using UnityEngine.UI;

public class OrbTest : MonoBehaviour
{
    public int camIndex = 0;          // 0 = laptop cam, 1 = phone via Iriun
    public int width = 640;
    public int height = 480;
    public int maxFeatures = 500;

    WebCamTexture cam;
    Texture2D frameTex, displayTex;
    RawImage display;
    Text hud;

    ORB orb;
    AKAZE akaze;
    bool useAkaze;
    float ms;

    void Start()
    {
        BuildUI();

        var devices = WebCamTexture.devices;
        if (devices.Length == 0) { Debug.LogError("No camera"); enabled = false; return; }
        int i = Mathf.Clamp(camIndex, 0, devices.Length - 1);
        Debug.Log("Camera: " + devices[i].name);

        cam = new WebCamTexture(devices[i].name, width, height, 30);
        cam.Play();

        orb = ORB.Create(maxFeatures);
        akaze = AKAZE.Create();
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

        var t0 = Time.realtimeSinceStartup;

        using (Mat rgba = TextureToMat(frameTex))
        using (Mat gray = new Mat())
        {
            Cv2.CvtColor(rgba, gray, ColorConversionCodes.RGBA2GRAY);

            KeyPoint[] kps = useAkaze ? akaze.Detect(gray) : orb.Detect(gray);

            foreach (var kp in kps)
                Cv2.Circle(rgba, (int)kp.Pt.X, (int)kp.Pt.Y, 3, new Scalar(0, 255, 0, 255), 1);

            MatToTexture(rgba, displayTex);

            ms = Mathf.Lerp(ms, (Time.realtimeSinceStartup - t0) * 1000f, 0.2f);
            hud.text = string.Format("{0}: {1} keypoints | {2:0.0} ms/frame | {3:0} FPS  (O=toggle)",
                useAkaze ? "AKAZE" : "ORB", kps.Length, ms, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-5f));
        }

        if (Input.GetKeyDown(KeyCode.O)) useAkaze = !useAkaze;
        if (Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.Escape)) { cam.Stop(); enabled = false; }
    }

    void OnDisable() { if (cam != null) cam.Stop(); }

    // --- Texture2D <-> Mat. Unity textures are bottom-up, OpenCV is top-down, hence the flip. ---

    public static Mat TextureToMat(Texture2D tex)
    {
        byte[] raw = tex.GetRawTextureData();                 // RGBA32
        Mat m = new Mat(tex.height, tex.width, MatType.CV_8UC4);
        Marshal.Copy(raw, 0, m.Data, raw.Length);
        Cv2.Flip(m, m, FlipMode.X);
        return m;
    }

    public static void MatToTexture(Mat rgbaMat, Texture2D tex)
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
        var o = hudGo.AddComponent<Outline>();
        o.effectColor = Color.black;
        var hrt = hud.rectTransform;
        hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(0, 1);
        hrt.pivot = new Vector2(0, 1);
        hrt.anchoredPosition = new Vector2(16, -16);
        hrt.sizeDelta = new Vector2(900, 40);
    }
}