// OWNER: Member 1. STUB - replace with ORB/SIFT + Lucas-Kanade + homography (OpenCvSharp).
// CONTRACT: GetHomography(frame) returns 9 floats (row-major 3x3) mapping
//           reference.jpg pixels -> current frame pixels, or null if the board is not found.
using UnityEngine;

public class Tracker : MonoBehaviour
{
    public float[] GetHomography(Texture2D frame)
    {
        return null;   // TODO Member 1
    }
}
