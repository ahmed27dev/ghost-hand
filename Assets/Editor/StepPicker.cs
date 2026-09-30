using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Menu: GhostHand > Step Picker
// Shows StreamingAssets/reference.jpg. Every step takes 2 clicks: FROM then TO.
//   point steps (LED): FROM = long leg (anode), TO = short leg (cathode). Drawn as a plain line.
//   arrow steps (resistor, wire): FROM = start, TO = end. Drawn as an arrow.
// Coordinates are reference.jpg pixels, top-left origin. Saves StreamingAssets/steps.json.
public class StepPicker : EditorWindow
{
    class PickStep
    {
        public int id;
        public string part, label, type, fromHint, toHint;
        public bool hasFrom, hasTo;
        public Vector2 from, to;

        public bool Done => hasFrom && hasTo;
        public void Clear() { hasFrom = false; hasTo = false; }
    }

    PickStep[] steps;
    Texture2D tex;
    int selected;
    float zoom = 1f;
    Vector2 scroll;
    Vector2 sideScroll;
    Vector2 hoverPx = new Vector2(-1, -1);

    static readonly Color[] Colors =
    {
        Color.red, new Color(1f, 0.6f, 0f), Color.yellow, new Color(1f, 0.4f, 0.8f),
        Color.green, Color.cyan, new Color(0.6f, 0.6f, 1f)
    };

    string RefPath => Path.Combine(Application.streamingAssetsPath, "reference.jpg");
    string JsonPath => Path.Combine(Application.streamingAssetsPath, "steps.json");

    [MenuItem("GhostHand/Step Picker")]
    static void Open()
    {
        var w = GetWindow<StepPicker>("Step Picker");
        w.minSize = new Vector2(900, 600);
    }

    void OnEnable()
    {
        wantsMouseMove = true;
        steps = new[]
        {
            new PickStep { id = 1, part = "LED", type = "point",
                label = "Place LED 1: long leg in row 10, short leg in row 12",
                fromHint = "long leg (row 10)", toHint = "short leg (row 12)" },
            new PickStep { id = 2, part = "Resistor", type = "arrow",
                label = "Place resistor 1 from row 12 to the negative rail",
                fromHint = "row 12", toHint = "negative rail" },
            new PickStep { id = 3, part = "LED", type = "point",
                label = "Place LED 2: long leg in row 20, short leg in row 22",
                fromHint = "long leg (row 20)", toHint = "short leg (row 22)" },
            new PickStep { id = 4, part = "Resistor", type = "arrow",
                label = "Place resistor 2 from row 22 to the negative rail",
                fromHint = "row 22", toHint = "negative rail" },
            new PickStep { id = 5, part = "Wire", type = "arrow",
                label = "Connect a jumper wire from Arduino pin 13 to row 10",
                fromHint = "Arduino pin 13", toHint = "row 10" },
            new PickStep { id = 6, part = "Wire", type = "arrow",
                label = "Connect a jumper wire from Arduino pin 12 to row 20",
                fromHint = "Arduino pin 12", toHint = "row 20" },
            new PickStep { id = 7, part = "Wire", type = "arrow",
                label = "Connect a jumper wire from Arduino GND to the negative rail",
                fromHint = "Arduino GND", toHint = "negative rail" },
        };
        selected = 0;
        LoadTexture();
    }

    void LoadTexture()
    {
        tex = null;
        if (!File.Exists(RefPath)) return;
        tex = new Texture2D(2, 2);
        tex.LoadImage(File.ReadAllBytes(RefPath));
    }

    void OnGUI()
    {
        if (tex == null)
        {
            EditorGUILayout.HelpBox("reference.jpg not found at:\n" + RefPath, MessageType.Error);
            if (GUILayout.Button("Reload")) LoadTexture();
            return;
        }

        EditorGUILayout.BeginHorizontal();
        DrawSidePanel();
        DrawImagePanel();
        EditorGUILayout.EndHorizontal();

        if (Event.current.type == EventType.MouseMove) Repaint();
    }

    void DrawSidePanel()
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(300));
        EditorGUILayout.LabelField("reference.jpg: " + tex.width + " x " + tex.height, EditorStyles.boldLabel);
        EditorGUILayout.LabelField(hoverPx.x >= 0 ? $"Mouse: ({hoverPx.x:0}, {hoverPx.y:0})" : "Mouse: -");

        var cur = steps[selected];
        var big = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true, normal = { textColor = Colors[selected % Colors.Length] } };
        EditorGUILayout.LabelField(cur.Done ? $"Step {cur.id} done" :
            $"NOW: Step {cur.id} - click {(cur.hasFrom ? cur.toHint : cur.fromHint)}", big);
        EditorGUILayout.Space();

        sideScroll = EditorGUILayout.BeginScrollView(sideScroll);
        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            GUI.backgroundColor = i == selected ? Color.yellow : Color.white;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = Color.white;

            var old = GUI.contentColor;
            GUI.contentColor = Colors[i % Colors.Length];
            if (GUILayout.Button($"Step {s.id}: {s.part} ({s.type})", EditorStyles.boldLabel)) selected = i;
            GUI.contentColor = old;

            EditorGUILayout.LabelField(s.label, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(StatusText(s), EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Click a step to select it, then click on the image.\n" +
                                "Clicking a finished step again redoes it.", MessageType.Info);

        if (GUILayout.Button("Clear selected step")) steps[selected].Clear();
        if (GUILayout.Button("Clear all")) foreach (var s in steps) s.Clear();

        EditorGUILayout.Space();
        bool allDone = true;
        foreach (var s in steps) if (!s.Done) allDone = false;

        GUI.enabled = allDone;
        if (GUILayout.Button("SAVE steps.json", GUILayout.Height(35))) Save();
        GUI.enabled = true;
        if (!allDone) EditorGUILayout.LabelField("Finish all steps to enable Save.", EditorStyles.miniLabel);

        EditorGUILayout.EndVertical();
    }

    string StatusText(PickStep s)
    {
        if (s.Done) return $"Done: ({s.from.x:0}, {s.from.y:0}) -> ({s.to.x:0}, {s.to.y:0})";
        return s.hasFrom ? $"Waiting: click {s.toHint}" : $"Waiting: click {s.fromHint}";
    }

    void DrawImagePanel()
    {
        EditorGUILayout.BeginVertical();
        zoom = EditorGUILayout.Slider("Zoom", zoom, 1f, 4f);

        Rect area = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        float fit = Mathf.Min(area.width / tex.width, area.height / tex.height);
        if (fit <= 0f) { EditorGUILayout.EndVertical(); return; }
        float scale = fit * zoom;

        Rect content = new Rect(0, 0, tex.width * scale, tex.height * scale);
        scroll = GUI.BeginScrollView(area, scroll, content);

        GUI.DrawTexture(content, tex);
        DrawMarkers(scale);
        HandleMouse(content, scale);

        GUI.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    void DrawMarkers(float scale)
    {
        if (Event.current.type != EventType.Repaint) return;

        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            Color c = Colors[i % Colors.Length];
            if (s.hasFrom) DrawDot(s.from * scale, c, s.id.ToString());
            if (!s.hasTo) continue;

            Vector2 a = s.from * scale, b = s.to * scale;
            Handles.color = c;
            Handles.DrawAAPolyLine(4f, a, b);
            if (s.type == "arrow")
            {
                Vector2 dir = (b - a).normalized, side = new Vector2(-dir.y, dir.x);
                Handles.DrawAAPolyLine(4f, b, b - dir * 12f + side * 6f);
                Handles.DrawAAPolyLine(4f, b, b - dir * 12f - side * 6f);
            }
            DrawDot(b, c, null);
        }
    }

    void DrawDot(Vector2 p, Color c, string text)
    {
        EditorGUI.DrawRect(new Rect(p.x - 5, p.y - 5, 10, 10), Color.black);
        EditorGUI.DrawRect(new Rect(p.x - 4, p.y - 4, 8, 8), c);
        if (text != null)
        {
            var style = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = c } };
            GUI.Label(new Rect(p.x + 6, p.y - 18, 30, 20), text, style);
        }
    }

    void HandleMouse(Rect content, float scale)
    {
        Event e = Event.current;
        if (!content.Contains(e.mousePosition)) { hoverPx = new Vector2(-1, -1); return; }

        Vector2 px = new Vector2(Mathf.Round(e.mousePosition.x / scale), Mathf.Round(e.mousePosition.y / scale));
        hoverPx = px;

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            var s = steps[selected];
            if (s.Done) s.Clear();

            if (!s.hasFrom) { s.from = px; s.hasFrom = true; }
            else { s.to = px; s.hasTo = true; }

            if (s.Done) AdvanceSelection();
            e.Use();
            Repaint();
        }
    }

    void AdvanceSelection()
    {
        for (int k = 1; k <= steps.Length; k++)
        {
            int i = (selected + k) % steps.Length;
            if (!steps[i].Done) { selected = i; return; }
        }
    }

    void Save()
    {
        var sb = new StringBuilder("{\"steps\":[\n");
        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "  {{\"id\":{0},\"part\":\"{1}\",\"label\":\"{2}\",\"type\":\"{3}\",\"from\":[{4},{5}],\"to\":[{6},{7}]}}",
                s.id, s.part, s.label, s.type, (int)s.from.x, (int)s.from.y, (int)s.to.x, (int)s.to.y);
            sb.Append(i < steps.Length - 1 ? ",\n" : "\n");
        }
        sb.Append("]}\n");

        File.WriteAllText(JsonPath, sb.ToString());
        AssetDatabase.Refresh();
        Debug.Log("Saved steps.json:\n" + sb);
        EditorUtility.DisplayDialog("Step Picker", "Saved to\n" + JsonPath, "OK");
    }
}