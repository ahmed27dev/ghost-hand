// OWNER: Member 4. Builds the Main scene from code so everyone gets an identical setup.
// Use:  Unity menu -> GhostHand -> Build Main Scene
// This is an Editor script: it must live in Assets/Editor and is not included in builds.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public static class SceneBuilder
{
    [MenuItem("GhostHand/Build Main Scene")]
    public static void BuildMainScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // --- Canvas ---
        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);

        if (Object.FindObjectOfType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        // --- Camera feed (full-screen RawImage) ---
        var rawGo = new GameObject("CameraFeed", typeof(RawImage));
        rawGo.transform.SetParent(canvasGo.transform, false);
        var raw = rawGo.GetComponent<RawImage>();
        Stretch(raw.rectTransform);
        raw.rectTransform.localScale = new Vector3(-1, 1, 1);   // mirror, so movement matches reality

        // --- HUD text ---
        var hudGo = new GameObject("HUD", typeof(Text));
        hudGo.transform.SetParent(canvasGo.transform, false);
        var hud = hudGo.GetComponent<Text>();
        hud.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        hud.fontSize = 22;
        hud.color = Color.green;
        hud.alignment = TextAnchor.UpperLeft;
        hud.text = "starting...";
        var outline = hudGo.AddComponent<Outline>();            // dark border, readable on any background
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(1.5f, -1.5f);
        var hudRt = hud.rectTransform;
        hudRt.anchorMin = new Vector2(0, 1);
        hudRt.anchorMax = new Vector2(0, 1);
        hudRt.pivot = new Vector2(0, 1);
        hudRt.anchoredPosition = new Vector2(16, -16);
        hudRt.sizeDelta = new Vector2(900, 40);

        // --- Controller object with all four scripts ---
        var ctrlGo = new GameObject("ARController");
        var controller = ctrlGo.AddComponent<ARController>();
        var tracker = ctrlGo.AddComponent<Tracker>();
        var occlusion = ctrlGo.AddComponent<HandOcclusion>();
        var steps = ctrlGo.AddComponent<StepManager>();

        // --- Wire every slot ---
        controller.background = raw;
        controller.tracker = tracker;
        controller.occlusion = occlusion;
        controller.steps = steps;
        controller.hud = hud;
        steps.arCamera = Camera.main;

        EditorSceneManager.MarkSceneDirty(scene);
        System.IO.Directory.CreateDirectory(Application.dataPath + "/Scenes");
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Main.unity");
        Debug.Log("Main scene built and saved to Assets/Scenes/Main.unity - press Play.");
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}