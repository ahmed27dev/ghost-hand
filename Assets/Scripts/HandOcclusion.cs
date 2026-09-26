// OWNER: Member 3. STUB - replace with MediaPipe hands + convex hull + stencil mesh.
// CONTRACT: GetHandHull(frame) returns hull points in FRAME PIXEL coordinates
//           (origin top-left), or an empty array when no hand is visible.
using UnityEngine;

public class HandOcclusion : MonoBehaviour
{
    static readonly Vector2[] None = new Vector2[0];

    public Vector2[] GetHandHull(Texture2D frame)
    {
        return None;   // TODO Member 3
    }
}
