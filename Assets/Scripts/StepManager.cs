// OWNER: Member 2. Loads steps.json and draws the current step through the homography.
// H maps reference.jpg pixels -> frame pixels (top-left origin). Never throws.
// Keys: N / B come from ARController. R (restart) is handled here.
using System.IO;
using UnityEngine;
using UnityEngine.UI;

public class StepManager : MonoBehaviour
{
    [Header("Scene refs (auto-found if empty)")]
    public RawImage background;
    public Camera arCamera;

    [Header("Tuning")]
    public float partDepth = 2f;        // distance in front of the camera where parts are drawn
    public float holePitchRef = 14f;    // reference.jpg pixels between two neighbouring breadboard holes
    public string stepsFile = "steps.json";

    [Header("Visibility")]
    public float partScale = 1.5f;      // LED / resistor drawn bigger than real size
    public float ringScale = 1.8f;      // target ring diameter, in holes
    public float replayEvery = 4f;      // seconds between drop-in / wire-grow replays (0 = play once)
    public bool showCallout = true;     // floating "1/4 LED" bubble with a pointer line
    public bool showSpotlight = true;   // darken the video except around the current step
    [Range(0f, 1f)] public float spotlightDim = 0.55f;
    public bool showFinishedSteps = true; // earlier steps stay visible as faded ghosts

    public int Current { get; private set; }
    public bool IsDone { get; private set; }
    public int StepCount => stepList != null && stepList.steps != null ? stepList.steps.Length : 0;
    public Step CurrentStep => StepCount > 0 ? stepList.steps[Current] : emptyStep;

    StepList stepList;
    readonly Step emptyStep = new Step
    {
        id = 0, part = "none", label = "No steps loaded", type = "point",
        from = new float[] { 0, 0 }, to = new float[] { 0, 0 }
    };
    bool ready;

    // 3D
    Transform holder;
    GameObject ledRoot, resistorRoot, markerFrom, markerTo, halo;
    Material haloMat;
    LineRenderer wireLine, arrowHead;
    GameObject[] ghostParts;
    Material[] ghostMats;
    LineRenderer[] ghostWires;

    // UI (overlay canvas, on top of everything)
    Canvas labelCanvas;
    Text label, calloutText, fromTag, toTag;
    RectTransform calloutRT, leaderRT;
    Image leaderImg;
    GameObject donePanel;

    // Spotlight (inside the camera-feed canvas, so it darkens the video but not the 3D parts)
    Canvas spotCanvas;
    RectTransform spotRT, spotHole;
    RawImage spotHoleImg;
    readonly RectTransform[] spotBars = new RectTransform[4];
    readonly Image[] spotBarImgs = new Image[4];

    int lastShown = -1;
    float stepStart;
    readonly Vector3[] corners = new Vector3[4];
    readonly Vector3[] wirePts = new Vector3[24];

    static readonly Color RingFrom = new Color(0.2f, 1f, 0.3f, 0.65f);
    static readonly Color RingTo = new Color(1f, 0.9f, 0.2f, 0.65f);

    // ---------------------------------------------------------------- lifecycle

    void Awake()
    {
        LoadSteps();
    }

    void Start()
    {
        if (background == null)
        {
            var ar = FindObjectOfType<ARController>();
            if (ar != null) background = ar.background;
        }
        if (arCamera == null) arCamera = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();

        FixBackgroundCanvas();
        BuildParts();
        BuildGhosts();
        BuildUI();
        BuildSpotlight();
        ready = true;
        Hide();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R)) Restart();
    }

    void LoadSteps()
    {
        stepList = null;
        Current = 0;
        try
        {
            string path = Path.Combine(Application.streamingAssetsPath, stepsFile);
            var list = JsonUtility.FromJson<StepList>(File.ReadAllText(path));
            if (list == null || list.steps == null || list.steps.Length == 0)
            {
                Debug.LogWarning("StepManager: steps.json has no steps");
                return;
            }
            stepList = list;
            Debug.Log("StepManager: loaded " + StepCount + " steps");
        }
        catch (System.Exception e)
        {
            Debug.LogError("StepManager: could not load steps.json: " + e.Message);
        }
    }

    // Overlay canvases draw on top of all 3D objects, so the camera feed would hide the parts.
    void FixBackgroundCanvas()
    {
        if (background == null || arCamera == null) return;
        var c = background.canvas != null ? background.canvas.rootCanvas : null;
        if (c != null && c.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            c.renderMode = RenderMode.ScreenSpaceCamera;
            c.worldCamera = arCamera;
            c.planeDistance = 50f;
        }
        if (arCamera.farClipPlane < 100f) arCamera.farClipPlane = 100f;
        if (arCamera.nearClipPlane > 0.1f) arCamera.nearClipPlane = 0.1f;
    }

    // ---------------------------------------------------------------- public API

    public void ShowStep(int index, float[] H)
    {
        if (!ready || StepCount == 0 || H == null || H.Length < 9 || background == null || arCamera == null)
        {
            Hide();
            return;
        }

        Current = Mathf.Clamp(index, 0, StepCount - 1);
        HideOverlays();

        // Finished steps as ghosts (all of them on the completion screen)
        if (showFinishedSteps || IsDone)
        {
            int upto = IsDone ? StepCount : Current;
            for (int i = 0; i < upto; i++) DrawGhost(i, H, IsDone ? 0.85f : 0.4f);
        }

        if (IsDone)
        {
            donePanel.SetActive(true);
            SetLabel(true);
            return;
        }

        if (Current != lastShown) { lastShown = Current; stepStart = Time.time; }

        var s = CurrentStep;
        if (!Geometry(s, H, out Vector2 sa, out Vector2 sb, out Vector3 a, out Vector3 b, out float pitch, out float pitchPx))
        {
            Hide();
            return;
        }

        float t = AnimTime();
        float pulse = 1f + 0.2f * Mathf.Sin(Time.time * 6f);
        string part = (s.part ?? "").ToLowerInvariant();
        Color col = PartColor(s);
        Vector3 fwd = arCamera.transform.forward;
        bool isPart = part == "led" || part == "resistor";

        if (isPart)
        {
            GameObject root = part == "led" ? ledRoot : resistorRoot;
            PlaceSpan(root, a, b, pitch, partScale);
            float drop = 1f - EaseOutBounce(Mathf.Clamp01(t / 0.6f));
            root.transform.position -= fwd * (drop * pitch * 10f);   // falls toward the board

            haloMat.color = new Color(col.r, col.g, col.b, 0.25f + 0.15f * Mathf.Sin(Time.time * 4f));
            PlaceDisc(halo, (a + b) * 0.5f, Vector3.Distance(a, b) * partScale * 1.8f + pitch * 2f, 0.02f);
        }
        else
        {
            DrawWire(a, b, pitch, col, Mathf.Clamp01(t / 0.7f));
        }

        PlaceDisc(markerFrom, a, pitch * ringScale * pulse, 0.01f);
        if (Vector3.Distance(a, b) > pitch * 0.5f) PlaceDisc(markerTo, b, pitch * ringScale * pulse, 0.01f);

        PlaceTags(s, sa, sb, pitchPx);

        if (showCallout)
        {
            string name = part == "led" ? "LED" : part == "resistor" ? "Resistor 220 ohm" : "Jumper wire";
            Vector2 target = isPart ? (sa + sb) * 0.5f : sa;
            PlaceCallout(target, (Current + 1) + "/" + StepCount + "  " + name, col);
        }

        if (showSpotlight)
        {
            float span = Vector2.Distance(sa, sb) * 0.5f * (isPart ? partScale : 1f);
            PlaceSpotlight((sa + sb) * 0.5f, Mathf.Max(span + pitchPx * 3f, 60f));
        }

        SetLabel(true);
    }

    public void Hide()
    {
        HideOverlays();
        SetLabel(false);
    }

    public void Next()
    {
        if (StepCount == 0) return;
        if (Current >= StepCount - 1) IsDone = true;
        else Current++;
    }

    public void Back()
    {
        if (StepCount == 0) return;
        if (IsDone) IsDone = false;
        else Current = Mathf.Max(Current - 1, 0);
    }

    public void Restart()
    {
        Current = 0;
        IsDone = false;
        lastShown = -1;
    }

    // ---------------------------------------------------------------- coordinate mapping

    bool Geometry(Step s, float[] H, out Vector2 sa, out Vector2 sb, out Vector3 a, out Vector3 b,
                  out float pitch, out float pitchPx)
    {
        sa = sb = Vector2.zero;
        a = b = Vector3.zero;
        pitch = pitchPx = 0f;
        if (s == null || s.from == null || s.from.Length < 2) return false;
        float[] to = (s.to != null && s.to.Length >= 2) ? s.to : s.from;

        if (!RefToScreen(s.from[0], s.from[1], H, out sa) ||
            !RefToScreen(to[0], to[1], H, out sb) ||
            !RefToScreen(s.from[0] + holePitchRef, s.from[1], H, out Vector2 sp))
            return false;

        a = ScreenToWorld(sa);
        b = ScreenToWorld(sb);
        pitch = Vector3.Distance(a, ScreenToWorld(sp));
        pitchPx = Vector2.Distance(sa, sp);
        return pitch > 1e-5f;
    }

    // reference px -> (H) -> frame px -> RawImage -> screen px
    bool RefToScreen(float x, float y, float[] H, out Vector2 screen)
    {
        screen = Vector2.zero;

        float w = H[6] * x + H[7] * y + H[8];
        if (Mathf.Abs(w) < 1e-8f) return false;
        float fx = (H[0] * x + H[1] * y + H[2]) / w;
        float fy = (H[3] * x + H[4] * y + H[5]) / w;
        if (float.IsNaN(fx) || float.IsNaN(fy) || float.IsInfinity(fx) || float.IsInfinity(fy)) return false;

        Texture tex = background.texture;
        if (tex == null || tex.width <= 16 || tex.height <= 16) return false;   // WebCamTexture reports 16x16 before it starts

        float u = fx / tex.width;          // frame px (top-left origin) -> texture uv (bottom-left origin)
        float v = 1f - fy / tex.height;

        Rect uv = background.uvRect;
        if (Mathf.Abs(uv.width) < 1e-6f || Mathf.Abs(uv.height) < 1e-6f) return false;
        float s = (u - uv.x) / uv.width;
        float r = (v - uv.y) / uv.height;

        background.rectTransform.GetWorldCorners(corners);
        Canvas c = background.canvas != null ? background.canvas.rootCanvas : null;
        Camera cc = (c == null || c.renderMode == RenderMode.ScreenSpaceOverlay) ? null : c.worldCamera;
        Vector2 bl = RectTransformUtility.WorldToScreenPoint(cc, corners[0]);
        Vector2 tl = RectTransformUtility.WorldToScreenPoint(cc, corners[1]);
        Vector2 br = RectTransformUtility.WorldToScreenPoint(cc, corners[3]);
        screen = bl + s * (br - bl) + r * (tl - bl);
        return true;
    }

    Vector3 ScreenToWorld(Vector2 s)
    {
        return arCamera.ScreenToWorldPoint(new Vector3(s.x, s.y, partDepth));
    }

    float AnimTime()
    {
        float e = Time.time - stepStart;
        if (replayEvery > 0f) e %= replayEvery;
        return e;
    }

    static float EaseOutBounce(float x)
    {
        const float n1 = 7.5625f, d1 = 2.75f;
        if (x < 1f / d1) return n1 * x * x;
        if (x < 2f / d1) { x -= 1.5f / d1; return n1 * x * x + 0.75f; }
        if (x < 2.5f / d1) { x -= 2.25f / d1; return n1 * x * x + 0.9375f; }
        x -= 2.625f / d1;
        return n1 * x * x + 0.984375f;
    }

    // ---------------------------------------------------------------- placing 3D things

    // Root's local x runs from 'a' to 'b', local -z points at the camera.
    void PlaceSpan(GameObject root, Vector3 a, Vector3 b, float pitch, float scale)
    {
        Vector3 fwd = arCamera.transform.forward;
        Vector3 d = Vector3.ProjectOnPlane(b - a, fwd);
        float len = d.magnitude;
        Vector3 x = len > 1e-6f ? d / len : arCamera.transform.right;
        if (len < pitch) len = pitch * 2f;

        Vector3 up = Vector3.Cross(fwd, x);
        root.transform.SetPositionAndRotation((a + b) * 0.5f, Quaternion.LookRotation(fwd, up));
        root.transform.localScale = Vector3.one * len * scale;
        root.SetActive(true);
    }

    void PlaceDisc(GameObject m, Vector3 pos, float size, float pushBack)
    {
        Vector3 fwd = arCamera.transform.forward;
        m.transform.position = pos + fwd * pushBack;   // just behind the parts
        m.transform.rotation = Quaternion.LookRotation(fwd, arCamera.transform.up) * Quaternion.Euler(90f, 0f, 0f);
        m.transform.localScale = new Vector3(size, size * 0.02f, size);
        m.SetActive(true);
    }

    void ComputeArc(Vector3 a, Vector3 b)
    {
        Vector3 toCam = -arCamera.transform.forward;
        float len = Vector3.Distance(a, b);
        Vector3 ctrl = (a + b) * 0.5f + toCam * len * 0.25f;   // arc the wire up toward the camera
        int n = wirePts.Length;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)(n - 1);
            float it = 1f - t;
            wirePts[i] = it * it * a + 2f * it * t * ctrl + t * t * b;
        }
    }

    void DrawWire(Vector3 a, Vector3 b, float pitch, Color col, float grow)
    {
        ComputeArc(a, b);
        int n = wirePts.Length;
        int count = Mathf.Clamp(Mathf.CeilToInt(grow * (n - 1)) + 1, 3, n);
        wireLine.positionCount = count;
        for (int i = 0; i < count; i++) wireLine.SetPosition(i, wirePts[i]);
        wireLine.widthMultiplier = pitch * 0.5f;
        wireLine.startColor = wireLine.endColor = col;
        wireLine.gameObject.SetActive(true);

        Vector3 tip = wirePts[count - 1];
        Vector3 dir = (tip - wirePts[count - 3]).normalized;
        arrowHead.positionCount = 2;
        arrowHead.SetPosition(0, tip - dir * pitch * 2.2f);
        arrowHead.SetPosition(1, tip);
        arrowHead.widthMultiplier = pitch * 1.6f;
        arrowHead.startColor = arrowHead.endColor = col;
        arrowHead.gameObject.SetActive(true);
    }

    void DrawGhost(int i, float[] H, float alpha)
    {
        var s = stepList.steps[i];
        if (!Geometry(s, H, out _, out _, out Vector3 a, out Vector3 b, out float pitch, out _)) return;

        Color c = PartColor(s);
        c.a = alpha;

        if (ghostParts[i] != null)
        {
            ghostMats[i].color = c;
            PlaceSpan(ghostParts[i], a, b, pitch, partScale);
        }
        else if (ghostWires[i] != null)
        {
            ComputeArc(a, b);
            var lr = ghostWires[i];
            lr.positionCount = wirePts.Length;
            lr.SetPositions(wirePts);
            lr.widthMultiplier = pitch * 0.4f;
            lr.startColor = lr.endColor = c;
            lr.gameObject.SetActive(true);
        }
    }

    Color PartColor(Step s)
    {
        string p = (s.part ?? "").ToLowerInvariant();
        if (p == "led") return new Color(1f, 0.3f, 0.3f);
        if (p == "resistor") return new Color(1f, 0.8f, 0.45f);
        string l = s.label ?? "";
        return l.Contains("GND") ? new Color(0.3f, 0.6f, 1f) : new Color(1f, 0.55f, 0f);
    }

    void HideOverlays()
    {
        if (ledRoot != null) ledRoot.SetActive(false);
        if (resistorRoot != null) resistorRoot.SetActive(false);
        if (markerFrom != null) markerFrom.SetActive(false);
        if (markerTo != null) markerTo.SetActive(false);
        if (halo != null) halo.SetActive(false);
        if (wireLine != null) wireLine.gameObject.SetActive(false);
        if (arrowHead != null) arrowHead.gameObject.SetActive(false);

        if (ghostParts != null)
            for (int i = 0; i < ghostParts.Length; i++)
            {
                if (ghostParts[i] != null) ghostParts[i].SetActive(false);
                if (ghostWires[i] != null) ghostWires[i].gameObject.SetActive(false);
            }

        if (calloutRT != null) calloutRT.gameObject.SetActive(false);
        if (leaderRT != null) leaderRT.gameObject.SetActive(false);
        if (fromTag != null) fromTag.gameObject.SetActive(false);
        if (toTag != null) toTag.gameObject.SetActive(false);
        if (donePanel != null) donePanel.SetActive(false);
        if (spotRT != null) spotRT.gameObject.SetActive(false);
    }

    // ---------------------------------------------------------------- placing UI things (screen space)

    void PlaceTags(Step s, Vector2 sa, Vector2 sb, float pitchPx)
    {
        bool led = (s.part ?? "").ToLowerInvariant() == "led";
        string ft = !string.IsNullOrEmpty(s.fromText) ? s.fromText : (led ? "+" : null);
        string tt = !string.IsNullOrEmpty(s.toText) ? s.toText : (led ? "\u2212" : null);

        Vector2 dir = sa - sb;
        dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector2.left;
        float ringR = pitchPx * ringScale * 0.5f;

        PlaceTag(fromTag, sa, dir, ringR, ft);
        PlaceTag(toTag, sb, -dir, ringR, tt);
    }

    void PlaceTag(Text t, Vector2 p, Vector2 dir, float ringR, string text)
    {
        if (string.IsNullOrEmpty(text)) { t.gameObject.SetActive(false); return; }
        t.text = text;
        float sf = labelCanvas.scaleFactor;
        float halfW = t.preferredWidth * 0.5f * sf;
        float halfH = t.preferredHeight * 0.5f * sf;
        float dist = ringR + 10f * sf + Mathf.Abs(dir.x) * halfW + Mathf.Abs(dir.y) * halfH;
        t.rectTransform.position = p + dir * dist;
        t.gameObject.SetActive(true);
    }

    void PlaceCallout(Vector2 target, string text, Color col)
    {
        float sf = labelCanvas.scaleFactor;
        float halfW = calloutRT.sizeDelta.x * 0.5f * sf;
        float halfH = calloutRT.sizeDelta.y * 0.5f * sf;

        Vector2 pos = target + new Vector2(0f, 130f * sf);
        pos.x = Mathf.Clamp(pos.x, halfW + 10f, Screen.width - halfW - 10f);
        pos.y = Mathf.Clamp(pos.y, 100f * sf + halfH, Screen.height - halfH - 10f);   // stay above the bottom bar

        calloutRT.position = pos;
        calloutText.text = text;
        calloutText.color = col;
        calloutRT.gameObject.SetActive(true);

        Vector2 bottom = pos - new Vector2(0f, halfH);
        Vector2 d = bottom - target;
        leaderRT.position = target;
        leaderRT.sizeDelta = new Vector2(d.magnitude / sf, 4f);
        leaderRT.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        leaderImg.color = col;
        leaderRT.gameObject.SetActive(true);
    }

    // Clear circle around 'centerScreen', dark everywhere else (radial image + 4 bars).
    void PlaceSpotlight(Vector2 centerScreen, float radiusScreen)
    {
        if (spotRT == null) return;
        Camera cam = spotCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : spotCanvas.worldCamera;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(spotRT, centerScreen, cam, out Vector2 c)) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(spotRT, centerScreen + new Vector2(radiusScreen, 0f), cam, out Vector2 e)) return;

        float half = Mathf.Abs(e.x - c.x) / 0.35f;   // the texture is clear out to 35% of its half-size
        Rect R = spotRT.rect;

        spotHoleImg.color = new Color(1f, 1f, 1f, spotlightDim);
        spotHole.anchoredPosition = c;
        spotHole.sizeDelta = new Vector2(half * 2f, half * 2f);

        SetBar(0, R.xMin, c.x - half, R.yMin, R.yMax);         // left
        SetBar(1, c.x + half, R.xMax, R.yMin, R.yMax);         // right
        SetBar(2, c.x - half, c.x + half, c.y + half, R.yMax); // top
        SetBar(3, c.x - half, c.x + half, R.yMin, c.y - half); // bottom

        spotRT.gameObject.SetActive(true);
    }

    void SetBar(int k, float x0, float x1, float y0, float y1)
    {
        spotBars[k].anchoredPosition = new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
        spotBars[k].sizeDelta = new Vector2(Mathf.Max(0f, x1 - x0), Mathf.Max(0f, y1 - y0));
        spotBarImgs[k].color = new Color(0f, 0f, 0f, spotlightDim);
    }

    // ---------------------------------------------------------------- building the 3D parts

    void BuildParts()
    {
        holder = new GameObject("StepParts").transform;

        Material red = Lit(new Color(1f, 0.1f, 0.1f), 1.2f);
        Material metal = Lit(new Color(0.8f, 0.8f, 0.85f), 0f);
        Material tan = Lit(new Color(0.85f, 0.72f, 0.5f), 0.3f);
        Material bandRed = Lit(new Color(0.8f, 0.05f, 0.05f), 0f);
        Material bandBrown = Lit(new Color(0.4f, 0.22f, 0.08f), 0f);
        Material bandGold = Lit(new Color(0.85f, 0.7f, 0.2f), 0f);

        // LED: span 1 between legs. x = -0.5 is the long leg (from), x = +0.5 the short leg (to).
        ledRoot = new GameObject("LED");
        ledRoot.transform.SetParent(holder, false);
        var lt = ledRoot.transform;
        Prim(PrimitiveType.Sphere,   lt, new Vector3(0, 0, -0.5f),  Vector3.zero,          new Vector3(0.6f, 0.6f, 0.6f),   red);
        Prim(PrimitiveType.Cylinder, lt, new Vector3(0, 0, -0.3f),  new Vector3(90, 0, 0), new Vector3(0.6f, 0.2f, 0.6f),   red);
        Prim(PrimitiveType.Cylinder, lt, new Vector3(0, 0, -0.1f),  new Vector3(90, 0, 0), new Vector3(0.7f, 0.02f, 0.7f),  red);
        Prim(PrimitiveType.Cylinder, lt, new Vector3(-0.33f, 0, -0.05f), new Vector3(0, 0, 90), new Vector3(0.05f, 0.18f, 0.05f), metal);
        Prim(PrimitiveType.Cylinder, lt, new Vector3(0.33f, 0, -0.05f),  new Vector3(0, 0, 90), new Vector3(0.05f, 0.18f, 0.05f), metal);

        // Resistor: span 1 between leads. 220 ohm = red, red, brown, gold.
        resistorRoot = new GameObject("Resistor");
        resistorRoot.transform.SetParent(holder, false);
        var rt = resistorRoot.transform;
        Prim(PrimitiveType.Cylinder, rt, new Vector3(0, 0, -0.12f),      new Vector3(0, 0, 90), new Vector3(0.22f, 0.28f, 0.22f),   tan);
        Prim(PrimitiveType.Cylinder, rt, new Vector3(-0.18f, 0, -0.12f), new Vector3(0, 0, 90), new Vector3(0.235f, 0.02f, 0.235f), bandRed);
        Prim(PrimitiveType.Cylinder, rt, new Vector3(-0.09f, 0, -0.12f), new Vector3(0, 0, 90), new Vector3(0.235f, 0.02f, 0.235f), bandRed);
        Prim(PrimitiveType.Cylinder, rt, new Vector3(0f, 0, -0.12f),     new Vector3(0, 0, 90), new Vector3(0.235f, 0.02f, 0.235f), bandBrown);
        Prim(PrimitiveType.Cylinder, rt, new Vector3(0.19f, 0, -0.12f),  new Vector3(0, 0, 90), new Vector3(0.235f, 0.02f, 0.235f), bandGold);
        Prim(PrimitiveType.Cylinder, rt, new Vector3(-0.39f, 0, -0.12f), new Vector3(0, 0, 90), new Vector3(0.04f, 0.11f, 0.04f),   metal);
        Prim(PrimitiveType.Cylinder, rt, new Vector3(0.39f, 0, -0.12f),  new Vector3(0, 0, 90), new Vector3(0.04f, 0.11f, 0.04f),   metal);
        Prim(PrimitiveType.Cylinder, rt, new Vector3(-0.5f, 0, -0.06f),  new Vector3(90, 0, 0), new Vector3(0.04f, 0.06f, 0.04f),   metal);
        Prim(PrimitiveType.Cylinder, rt, new Vector3(0.5f, 0, -0.06f),   new Vector3(90, 0, 0), new Vector3(0.04f, 0.06f, 0.04f),   metal);

        // Target rings (green = FROM / long leg, yellow = TO / short leg) and the glow under parts
        markerFrom = Prim(PrimitiveType.Cylinder, holder, Vector3.zero, Vector3.zero, Vector3.one, Flat(RingFrom));
        markerTo   = Prim(PrimitiveType.Cylinder, holder, Vector3.zero, Vector3.zero, Vector3.one, Flat(RingTo));
        haloMat = Flat(new Color(1f, 1f, 1f, 0.3f));
        halo = Prim(PrimitiveType.Cylinder, holder, Vector3.zero, Vector3.zero, Vector3.one, haloMat);
        markerFrom.name = "MarkerFrom";
        markerTo.name = "MarkerTo";
        halo.name = "Glow";

        wireLine = MakeLine("Wire", holder, AnimationCurve.Constant(0f, 1f, 1f));
        arrowHead = MakeLine("ArrowHead", holder, AnimationCurve.Linear(0f, 1f, 1f, 0f));
    }

    // One faded copy per step, shown once that step is finished.
    void BuildGhosts()
    {
        int n = StepCount;
        ghostParts = new GameObject[n];
        ghostMats = new Material[n];
        ghostWires = new LineRenderer[n];

        for (int i = 0; i < n; i++)
        {
            string p = (stepList.steps[i].part ?? "").ToLowerInvariant();
            if (p == "led" || p == "resistor")
            {
                var g = Instantiate(p == "led" ? ledRoot : resistorRoot, holder);
                g.name = "Ghost" + (i + 1) + "_" + stepList.steps[i].part;
                foreach (var col in g.GetComponentsInChildren<Collider>(true)) Destroy(col);
                ghostMats[i] = Flat(Color.white);
                foreach (var r in g.GetComponentsInChildren<Renderer>(true)) r.sharedMaterial = ghostMats[i];
                g.SetActive(false);
                ghostParts[i] = g;
            }
            else
            {
                ghostWires[i] = MakeLine("Ghost" + (i + 1) + "_Wire", holder, AnimationCurve.Constant(0f, 1f, 1f));
                ghostWires[i].gameObject.SetActive(false);
            }
        }
    }

    GameObject Prim(PrimitiveType type, Transform parent, Vector3 pos, Vector3 euler, Vector3 scale, Material m)
    {
        var g = GameObject.CreatePrimitive(type);
        Destroy(g.GetComponent<Collider>());
        g.transform.SetParent(parent, false);
        g.transform.localPosition = pos;
        g.transform.localEulerAngles = euler;
        g.transform.localScale = scale;
        g.GetComponent<Renderer>().sharedMaterial = m;
        return g;
    }

    LineRenderer MakeLine(string name, Transform parent, AnimationCurve width)
    {
        var g = new GameObject(name);
        g.transform.SetParent(parent, false);
        var lr = g.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.material = Flat(Color.white);
        lr.widthCurve = width;
        lr.numCapVertices = 4;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    Material Lit(Color c, float emission)
    {
        var m = new Material(Shader.Find("Standard"));
        m.color = c;
        if (emission > 0f)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * emission);
        }
        return m;
    }

    Material Flat(Color c)
    {
        var m = new Material(Shader.Find("Sprites/Default"));
        m.color = c;
        return m;
    }

    // ---------------------------------------------------------------- UI

    void BuildUI()
    {
        var go = new GameObject("StepLabelCanvas");
        labelCanvas = go.AddComponent<Canvas>();
        labelCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        labelCanvas.sortingOrder = 100;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Leader line (created first so it draws behind the bubble)
        var lgo = new GameObject("Leader", typeof(RectTransform), typeof(Image));
        lgo.transform.SetParent(go.transform, false);
        leaderRT = lgo.GetComponent<RectTransform>();
        leaderRT.pivot = new Vector2(0f, 0.5f);
        leaderImg = lgo.GetComponent<Image>();
        leaderImg.raycastTarget = false;

        // Callout bubble
        var cgo = new GameObject("Callout", typeof(RectTransform), typeof(Image));
        cgo.transform.SetParent(go.transform, false);
        calloutRT = cgo.GetComponent<RectTransform>();
        calloutRT.sizeDelta = new Vector2(300, 56);
        var cimg = cgo.GetComponent<Image>();
        cimg.color = new Color(0f, 0f, 0f, 0.8f);
        cimg.raycastTarget = false;
        calloutText = MakeText("Text", cgo.transform, font, 30, FontStyle.Bold);
        var ct = calloutText.rectTransform;
        ct.anchorMin = Vector2.zero; ct.anchorMax = Vector2.one;
        ct.offsetMin = new Vector2(8, 4); ct.offsetMax = new Vector2(-8, -4);

        // Tags next to the target holes
        fromTag = MakeText("FromTag", go.transform, font, 26, FontStyle.Bold);
        fromTag.color = new Color(RingFrom.r, RingFrom.g, RingFrom.b, 1f);
        fromTag.rectTransform.sizeDelta = new Vector2(300, 50);
        toTag = MakeText("ToTag", go.transform, font, 26, FontStyle.Bold);
        toTag.color = new Color(RingTo.r, RingTo.g, RingTo.b, 1f);
        toTag.rectTransform.sizeDelta = new Vector2(300, 50);

        // Completion panel
        donePanel = new GameObject("DonePanel", typeof(RectTransform), typeof(Image));
        donePanel.transform.SetParent(go.transform, false);
        var drt = donePanel.GetComponent<RectTransform>();
        drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0.9f);
        drt.sizeDelta = new Vector2(660, 140);
        var dimg = donePanel.GetComponent<Image>();
        dimg.color = new Color(0f, 0.35f, 0.12f, 0.85f);
        dimg.raycastTarget = false;
        var dtext = MakeText("Text", donePanel.transform, font, 26, FontStyle.Normal);
        var dtr = dtext.rectTransform;
        dtr.anchorMin = Vector2.zero; dtr.anchorMax = Vector2.one;
        dtr.offsetMin = new Vector2(10, 8); dtr.offsetMax = new Vector2(-10, -8);
        dtext.text = "<size=48><b>Circuit complete!</b></size>\nAll " + StepCount + " steps done. Plug in the USB cable.";
        donePanel.SetActive(false);

        // Bottom instruction bar
        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(go.transform, false);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = new Vector2(0, 0);
        prt.anchorMax = new Vector2(1, 0);
        prt.pivot = new Vector2(0.5f, 0);
        prt.sizeDelta = new Vector2(0, 90);
        prt.anchoredPosition = Vector2.zero;
        var pimg = panel.GetComponent<Image>();
        pimg.color = new Color(0, 0, 0, 0.6f);
        pimg.raycastTarget = false;

        label = MakeText("Label", panel.transform, font, 28, FontStyle.Normal);
        var trt = label.rectTransform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(20, 8);
        trt.offsetMax = new Vector2(-20, -8);
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
    }

    void BuildSpotlight()
    {
        if (background == null) return;
        spotCanvas = background.canvas != null ? background.canvas.rootCanvas : null;
        if (spotCanvas == null) return;

        var root = new GameObject("Spotlight", typeof(RectTransform));
        spotRT = root.GetComponent<RectTransform>();
        spotRT.SetParent(spotCanvas.transform, false);
        spotRT.anchorMin = Vector2.zero;
        spotRT.anchorMax = Vector2.one;
        spotRT.offsetMin = spotRT.offsetMax = Vector2.zero;
        // right above the camera feed, below the HUD
        if (background.transform.parent == spotCanvas.transform)
            spotRT.SetSiblingIndex(background.transform.GetSiblingIndex() + 1);

        var hole = new GameObject("Hole", typeof(RectTransform), typeof(RawImage));
        hole.transform.SetParent(spotRT, false);
        spotHoleImg = hole.GetComponent<RawImage>();
        spotHoleImg.texture = MakeSpotTexture(128);
        spotHoleImg.raycastTarget = false;
        spotHole = spotHoleImg.rectTransform;
        spotHole.anchorMin = spotHole.anchorMax = new Vector2(0.5f, 0.5f);

        for (int k = 0; k < 4; k++)
        {
            var g = new GameObject("Bar" + k, typeof(RectTransform), typeof(Image));
            g.transform.SetParent(spotRT, false);
            spotBarImgs[k] = g.GetComponent<Image>();
            spotBarImgs[k].raycastTarget = false;
            spotBars[k] = g.GetComponent<RectTransform>();
            spotBars[k].anchorMin = spotBars[k].anchorMax = new Vector2(0.5f, 0.5f);
        }
        root.SetActive(false);
    }

    // Black texture: transparent in the middle, fading to opaque at the edges.
    Texture2D MakeSpotTexture(int n)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        var px = new Color32[n * n];
        float c = (n - 1) * 0.5f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (n * 0.5f);
                float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.6f, r));
                px[y * n + x] = new Color32(0, 0, 0, (byte)(a * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    Text MakeText(string name, Transform parent, Font font, int size, FontStyle style)
    {
        var g = new GameObject(name, typeof(RectTransform), typeof(Text));
        g.transform.SetParent(parent, false);
        var t = g.GetComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        t.supportRichText = true;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.raycastTarget = false;
        var o = g.AddComponent<Outline>();
        o.effectColor = new Color(0, 0, 0, 0.9f);
        o.effectDistance = new Vector2(2, -2);
        return t;
    }

    void SetLabel(bool tracking)
    {
        if (label == null) return;
        string main = IsDone
            ? "All " + StepCount + " steps done! Plug in the Arduino - the LED should blink."
            : "Step " + (Current + 1) + "/" + Mathf.Max(StepCount, 1) + ": " + CurrentStep.label;
        string hint;
        if (!tracking) hint = "<size=20><color=#ffcc00>Point the camera at the board...</color></size>";
        else if (IsDone) hint = "<size=20>B = back    R = restart</size>";
        else hint = "<size=20>Place the part, then press N    (B = back, R = restart)</size>";
        label.text = main + "\n" + hint;
    }
}