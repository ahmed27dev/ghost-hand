// OWNER: Member 4. Main loop: camera -> tracker -> occlusion -> step. N = next, B = back.
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

    WebCamTexture cam;
    Texture2D frame;
    float fps;
    float lastFrameTime = -1f;

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
        if (Input.GetKeyDown(KeyCode.N)) steps.Next();
        if (Input.GetKeyDown(KeyCode.B)) steps.Back();

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

        if (hud != null)
            hud.text = string.Format("Step {0}/{1}: {2} | {3} | hull {4} | {5:0.0} FPS",
                steps.Current + 1, steps.StepCount, steps.CurrentStep.part,
                tracking ? "TRACKING" : "SEARCHING", hull.Length, fps);
    }

    void OnDisable() { if (cam != null) cam.Stop(); }
}