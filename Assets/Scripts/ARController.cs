// OWNER: Member 4. Main loop: camera -> tracker -> occlusion -> step.
// N = next, B = back, R = restart, A = auto-advance on/off.
using UnityEngine;
using UnityEngine.UI;

public class ARController : MonoBehaviour
{
    public RawImage background;            // full-screen RawImage showing the camera feed
    public Tracker tracker;
    public HandOcclusion occlusion;
    public StepManager steps;
    public Text hud;
    public int requestedWidth = 640;
    public int requestedHeight = 480;
    public int requestedFps = 30;
    public int cameraIndex = 0;            // see Console on Play for the list of cameras

    [Header("Auto advance")]
    public bool autoAdvance = true;
    public float targetRadiusRef = 55f;    // radius around the target hole, in reference.jpg pixels
    public float handHoldTime = 0.8f;      // hand must stay on the target this long
    public float handLeaveTime = 0.5f;     // hand must be away this long to count as "done"
    public float cooldownTime = 1.3f;      // pause after each auto step

    WebCamTexture cam;
    Texture2D frame;
    float fps;
    float lastFrameTime = -1f;

    // auto-advance state
    float onTargetTimer;
    float offTargetTimer;
    bool armed;
    float cooldownUntil;

    void Start()
    {
        var devices = WebCamTexture.devices;
        if (devices.Length == 0) { Debug.LogError("No camera found"); enabled = false; return; }

        for (int d = 0; d < devices.Length; d++)
            Debug.Log("Camera " + d + ": " + devices[d].name + (devices[d].isFrontFacing ? " (front)" : ""));

        int i = Mathf.Clamp(cameraIndex, 0, devices.Length - 1);
        Debug.Log("Using camera " + i + ": " + devices[i].name);

        cam = new WebCamTexture(devices[i].name, requestedWidth, requestedHeight, requestedFps);
        cam.Play();
        background.texture = cam;
        frame = new Texture2D(requestedWidth, requestedHeight, TextureFormat.RGBA32, false);
    }

    void Update()
    {
        // Keys are checked every Unity frame, not only when a new camera frame arrives.
        if (Input.GetKeyDown(KeyCode.N)) { steps.Next(); ResetAuto(); }
        if (Input.GetKeyDown(KeyCode.B)) { steps.Back(); ResetAuto(); }
        if (Input.GetKeyDown(KeyCode.R)) { steps.Restart(); ResetAuto(); }
        if (Input.GetKeyDown(KeyCode.A)) { autoAdvance = !autoAdvance; ResetAuto(); }

        if (cam == null || !cam.didUpdateThisFrame) return;

        // FPS = camera frames actually processed per second
        float now = Time.realtimeSinceStartup;
        if (lastFrameTime > 0f)
            fps = Mathf.Lerp(fps, 1f / Mathf.Max(now - lastFrameTime, 1e-5f), 0.1f);
        lastFrameTime = now;

        if (frame.width != cam.width || frame.height != cam.height)
            frame.Reinitialize(cam.width, cam.height);
        frame.SetPixels32(cam.GetPixels32());
        frame.Apply(false);

        float[] H = tracker.GetHomography(frame);
        bool tracking = H != null;

        occlusion.SetHomography(H);                      // hand detection compares against the warped sheet
        Vector2[] hull = occlusion.GetHandHull(frame);   // hull in frame pixels, empty if no hand
        occlusion.ShowMask(hull);                        // hide the 3D parts under the hand

        if (tracking) steps.ShowStep(steps.Current, H);
        else steps.Hide();

        string auto = autoAdvance ? "AUTO" : "manual";
        if (autoAdvance && tracking) auto += UpdateAutoAdvance(H, hull);

        if (hud != null)
            hud.text = string.Format("Step {0}/{1}: {2} | {3} | {4} | hull {5} | {6:0.0} FPS",
                steps.Current + 1, steps.StepCount, steps.CurrentStep.part,
                tracking ? "TRACKING" : "SEARCHING", auto, hull.Length, fps);
    }

    // Hand stays on the target, then leaves -> go to the next step. Returns a short HUD note.
    string UpdateAutoAdvance(float[] H, Vector2[] hull)
    {
        if (steps.Current >= steps.StepCount) return "";
        if (Time.time < cooldownUntil) return " (wait)";

        var step = steps.CurrentStep;
        Vector2 target = RefToFrame(H, step.to[0], step.to[1]);
        float radius = (RefToFrame(H, step.to[0] + targetRadiusRef, step.to[1]) - target).magnitude;

        bool handOnTarget = hull.Length >= 3 && HullTouchesCircle(hull, target, radius);
        float dt = Time.deltaTime;

        if (handOnTarget)
        {
            onTargetTimer += dt;
            offTargetTimer = 0f;
            if (onTargetTimer >= handHoldTime) armed = true;
        }
        else
        {
            onTargetTimer = 0f;
            if (armed)
            {
                offTargetTimer += dt;
                if (offTargetTimer >= handLeaveTime)
                {
                    steps.Next();
                    ResetAuto();
                    cooldownUntil = Time.time + cooldownTime;
                    return "";
                }
            }
        }
        return armed ? " (hand done?)" : (handOnTarget ? " (hand on target)" : "");
    }

    void ResetAuto() { onTargetTimer = 0f; offTargetTimer = 0f; armed = false; }

    // reference.jpg pixel -> camera frame pixel using the 3x3 homography (row-major)
    static Vector2 RefToFrame(float[] H, float x, float y)
    {
        float w = H[6] * x + H[7] * y + H[8];
        if (Mathf.Abs(w) < 1e-6f) w = 1e-6f;
        return new Vector2((H[0] * x + H[1] * y + H[2]) / w, (H[3] * x + H[4] * y + H[5]) / w);
    }

    // true if the circle's centre is inside the hull, or any hull point is inside the circle
    static bool HullTouchesCircle(Vector2[] hull, Vector2 c, float r)
    {
        for (int i = 0; i < hull.Length; i++)
            if ((hull[i] - c).sqrMagnitude <= r * r) return true;

        bool inside = false;
        for (int i = 0, j = hull.Length - 1; i < hull.Length; j = i++)
        {
            Vector2 a = hull[i], b = hull[j];
            if (((a.y > c.y) != (b.y > c.y)) &&
                (c.x < (b.x - a.x) * (c.y - a.y) / (b.y - a.y) + a.x))
                inside = !inside;
        }
        return inside;
    }

    void OnDisable() { if (cam != null) cam.Stop(); }
}