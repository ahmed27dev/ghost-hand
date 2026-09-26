// OWNER: Member 2. Loads steps.json, places the 3D part for the current step.
// CONTRACT: ShowStep(index, H) positions the part; Hide() clears it;
//           StepCount / Current / CurrentStep are read by ARController.
using System.IO;
using UnityEngine;

public class StepManager : MonoBehaviour
{
    public GameObject[] partPrefabs;      // assign in Inspector, same order as steps
    public Camera arCamera;

    StepList data;
    GameObject spawned;

    public int StepCount => data == null ? 0 : data.steps.Length;
    public int Current { get; private set; }
    public Step CurrentStep => data.steps[Current];

    void Awake()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "steps.json");
        data = JsonUtility.FromJson<StepList>(File.ReadAllText(path));
    }

    public void Next() { if (Current < StepCount - 1) { Current++; Respawn(); } }
    public void Back() { if (Current > 0) { Current--; Respawn(); } }

    void Respawn()
    {
        if (spawned != null) Destroy(spawned);
        spawned = null;
    }

    public void Hide()
    {
        if (spawned != null) spawned.SetActive(false);
    }

    // H = row-major 3x3, reference pixels -> frame pixels
    public void ShowStep(int index, float[] H)
    {
        Step s = data.steps[index];
        Vector2 centreRef = new Vector2(s.box[0] + s.box[2] * 0.5f, s.box[1] + s.box[3] * 0.5f);
        Vector2 centreFrame = Warp(H, centreRef);

        if (spawned == null && partPrefabs.Length > index && partPrefabs[index] != null)
            spawned = Instantiate(partPrefabs[index]);
        if (spawned == null) return;

        spawned.SetActive(true);
        // TODO Member 2: replace with a proper plane/pose. Screen-space placement is enough for the MVP.
        // frame pixels are top-left origin, Unity screen coords are bottom-left
        Vector3 screen = new Vector3(centreFrame.x, Screen.height - centreFrame.y, 1.0f);
        spawned.transform.position = arCamera.ScreenToWorldPoint(screen);
    }

    public static Vector2 Warp(float[] H, Vector2 p)
    {
        float x = H[0] * p.x + H[1] * p.y + H[2];
        float y = H[3] * p.x + H[4] * p.y + H[5];
        float w = H[6] * p.x + H[7] * p.y + H[8];
        if (Mathf.Abs(w) < 1e-6f) w = 1e-6f;
        return new Vector2(x / w, y / w);
    }
}
