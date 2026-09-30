// OWNER: Member 3. Draws the camera image again inside the hand outline, in front of the
// 3D parts, so the parts look hidden under the hand. Filled in by HandOcclusion.ShowMask().
using UnityEngine;
using UnityEngine.UI;

public class HandPatch : MaskableGraphic
{
    Texture src;

    // Setting the texture tells Unity to refresh the material, otherwise the old (white) texture stays.
    public Texture source
    {
        get { return src; }
        set
        {
            if (src == value) return;
            src = value;
            SetMaterialDirty();
        }
    }

    Vector2[] localPts = new Vector2[0];
    Vector2[] uvs = new Vector2[0];
    int count;

    public override Texture mainTexture => src != null ? src : s_WhiteTexture;

    public void SetPolygon(Vector2[] local, Vector2[] uv, int n)
    {
        localPts = local;
        uvs = uv;
        count = n;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (count < 3 || localPts == null || uvs == null) return;

        // Triangle fan around the centre (the hull is convex)
        Vector2 c = Vector2.zero, cu = Vector2.zero;
        for (int i = 0; i < count; i++) { c += localPts[i]; cu += uvs[i]; }
        c /= count;
        cu /= count;

        UIVertex v = UIVertex.simpleVert;
        v.color = color;
        v.position = c;
        v.uv0 = cu;
        vh.AddVert(v);

        for (int i = 0; i < count; i++)
        {
            v.position = localPts[i];
            v.uv0 = uvs[i];
            vh.AddVert(v);
        }
        for (int i = 0; i < count; i++)
            vh.AddTriangle(0, 1 + i, 1 + (i + 1) % count);
    }
}