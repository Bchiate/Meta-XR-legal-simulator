using System.Linq;
using HIS.Core;
using HIS.Intruder;
using HIS.Room;
using Meta.XR.EnvironmentDepth;
using UnityEngine.AI;
using HIS.UI;
using HIS.Weapons;
using HIS.Outcome;
using HIS.Scenario;
using BodyPart = HIS.Weapons.BodyPart;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds Assets/_Game/Scenes/Main.unity from scratch: the same objects Meta's Building Blocks
/// would create (camera rig, passthrough underlay, controllers, MRUK) plus our game roots.
/// Idempotent: re-running overwrites the scene. Headless:
///   Unity -batchmode -quit -projectPath . -executeMethod MainSceneBuilder.Build
/// </summary>
public static class MainSceneBuilder
{
    const string ScenePath = "Assets/_Game/Scenes/Main.unity";
    const string CameraRigPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
    const string ControllerPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRControllerPrefab.prefab";
    const string MrukPrefab = "Packages/com.meta.xr.mrutilitykit/Core/Tools/MRUK.prefab";
    const string WireframeMat = "Packages/com.meta.xr.mrutilitykit/Core/Materials/Wireframe.mat";
    const string InvisibleOccluderMat = "Packages/com.meta.xr.mrutilitykit/Core/Materials/InvisibleOccluder.mat";
    const string OcclusionLitShader = "EnvironmentDepth/OcclusionLit"; // Meta URP lit shader that respects Depth API occlusion
    const string IntruderPrefabPath = "Assets/_Game/Prefabs/Intruder_Placeholder.prefab";
    const string PistolPrefabPath = "Assets/_Game/Prefabs/Weapon_Pistol.prefab";
    const string KnifePrefabPath = "Assets/_Game/Prefabs/Weapon_Knife.prefab";
    const string BrettRoomJson = "Assets/_Game/Rooms/BrettRoom.json"; // your own room, pulled from a dev build with scripts/pull-room.sh
    // Without a room of your own: MRUK's sample living room (two door frames), straight from the package.
    const string SampleRoomJson = "Packages/com.meta.xr.mrutilitykit/Core/Rooms/Json/MeshLivingRoom1.json";
    static readonly string[] FallbackRooms =
    {
        "Packages/com.meta.xr.mrutilitykit/Core/Rooms/Prefabs/LivingRoom/LivingRoom00.prefab",
        "Packages/com.meta.xr.mrutilitykit/Core/Rooms/Prefabs/LivingRoom/LivingRoom01.prefab",
        "Packages/com.meta.xr.mrutilitykit/Core/Rooms/Prefabs/Bedroom/Bedroom00.prefab",
    };

    [MenuItem("HIS/Build Main Scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // --- Lighting: one directional light, no shadows (Quest budget) ---
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.0f;
        light.shadows = LightShadows.None;
        lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.5f);

        // --- Camera rig (Building Block "Camera Rig") ---
        var rig = Instantiate(CameraRigPrefab, "[HIS] OVRCameraRig");
        var cameraRig = rig.GetComponent<OVRCameraRig>();
        var manager = rig.GetComponent<OVRManager>();
        manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
        manager.isInsightPassthroughEnabled = true;
        foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0); // transparent so the underlay shows through
        }
        var centerEye = cameraRig.centerEyeAnchor;
        if (centerEye.GetComponent<AudioListener>() == null) centerEye.gameObject.AddComponent<AudioListener>();

        // --- Passthrough underlay (Building Block "Passthrough") ---
        var pt = new GameObject("[HIS] PassthroughUnderlay");
        var layer = pt.AddComponent<OVRPassthroughLayer>();
        layer.overlayType = OVROverlay.OverlayType.Underlay;
        layer.compositionDepth = 0;

        // --- Controllers (Building Block "Controller Tracking") ---
        var left = Instantiate(ControllerPrefab, "[HIS] Controller Left", cameraRig.leftControllerAnchor);
        left.GetComponent<OVRControllerHelper>().m_controller = OVRInput.Controller.LTouch;
        var right = Instantiate(ControllerPrefab, "[HIS] Controller Right", cameraRig.rightControllerAnchor);
        right.GetComponent<OVRControllerHelper>().m_controller = OVRInput.Controller.RTouch;

        // --- Our grab hands (one per controller anchor) ---
        var leftHand = MakeHand(cameraRig.leftControllerAnchor, OVRInput.Controller.LTouch);
        var rightHand = MakeHand(cameraRig.rightControllerAnchor, OVRInput.Controller.RTouch);

        // --- MRUK (Building Block "MR Utility Kit") ---
        var mrukGo = Instantiate(MrukPrefab, "[HIS] MRUK");
        var mruk = mrukGo.GetComponent<MRUK>();
        // Editor: the simulator's synthetic room, else Brett's real room (JSON) if captured, else a stock prefab room.
        // Player builds are forced to Device-only by HISBuildProcessor (never a fake room on the headset).
        var brettRoom = AssetDatabase.LoadAssetAtPath<TextAsset>(BrettRoomJson) ?? AssetDatabase.LoadAssetAtPath<TextAsset>(SampleRoomJson);
        mruk.SceneSettings.DataSource = brettRoom != null ? MRUK.SceneDataSource.DeviceWithJsonFallback : MRUK.SceneDataSource.DeviceWithPrefabFallback;
        mruk.SceneSettings.SceneJsons = brettRoom != null ? new[] { brettRoom } : new TextAsset[0];
        mruk.SceneSettings.LoadSceneOnStartup = true;
        mruk.SceneSettings.RoomIndex = 0;
        mruk.SceneSettings.RoomPrefabs = FallbackRooms
            .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
            .Where(p => p != null).ToArray();

        // Wireframe over walls/furniture so the tester can see the scan lines up with the real room.
        var effect = new GameObject("[HIS] RoomWireframe").AddComponent<EffectMesh>();
        effect.MeshMaterial = AssetDatabase.LoadAssetAtPath<Material>(WireframeMat);
        effect.Labels = MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH |
                        MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.OTHER;
        effect.SpawnOnStart = MRUK.RoomFilter.CurrentRoomOnly;
        effect.BarycentricCoordinatesEnabled = true; // Wireframe.mat needs barycentrics or it renders solid
        effect.Colliders = false;
        effect.CastShadow = false;
        // Not over the doorway: a scanned door frame lies within ~1 mm of its wall (BrettRoom.json: 0.2-0.9 mm), where the
        // virtual leaf's front face is, and the 16-bit eye depth can't part them at 2-4 m (a step is 0.6-2.4 mm), so the
        // wall's see-through film and lines z-fought on the door (Brett 0.1.17: "a light gray outline in the door that flickers").
        effect.CutHoles = MRUKAnchor.SceneLabels.DOOR_FRAME;

        // --- Dynamic occlusion (Depth API via com.unity.xr.meta-openxr "Meta Quest: Occlusion" feature) ---
        var depthGo = new GameObject("[HIS] EnvironmentDepth");
        var depth = depthGo.AddComponent<EnvironmentDepthManager>();
        depth.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
        depth.RemoveHands = true; // real hands must not occlude a held virtual weapon

        // --- Static occluders on scanned walls (covers what the depth sensor can't reach) ---
        var occluders = new GameObject("[HIS] WallOccluders").AddComponent<EffectMesh>();
        occluders.MeshMaterial = AssetDatabase.LoadAssetAtPath<Material>(InvisibleOccluderMat);
        occluders.Labels = MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE | MRUKAnchor.SceneLabels.CEILING;
        occluders.SpawnOnStart = MRUK.RoomFilter.CurrentRoomOnly;
        occluders.Colliders = true;   // walls/ceiling also act as physics surfaces
        occluders.CastShadow = false;
        occluders.Layer = QuestProjectSetup.RoomLayer;
        // No invisible wall across the doorway: the door rig's dark box sits behind the frame and must stay visible.
        occluders.CutHoles = MRUKAnchor.SceneLabels.DOOR_FRAME;

        // --- Invisible physics for the real floor + furniture: dropped weapons land on them, knives/blood raycasts hit them ---
        var roomPhysics = new GameObject("[HIS] RoomPhysics").AddComponent<EffectMesh>();
        roomPhysics.MeshMaterial = AssetDatabase.LoadAssetAtPath<Material>(InvisibleOccluderMat);
        roomPhysics.Labels = MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH |
                             MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.OTHER;
        roomPhysics.SpawnOnStart = MRUK.RoomFilter.CurrentRoomOnly;
        roomPhysics.Colliders = true;
        roomPhysics.HideMesh = true;
        roomPhysics.CastShadow = false;
        roomPhysics.Layer = QuestProjectSetup.RoomLayer;

        // --- NavMesh baked at runtime from the real floor, carving around real furniture ---
        var navGo = new GameObject("[HIS] SceneNavigation");
        var nav = navGo.AddComponent<SceneNavigation>();
        nav.BuildOnSceneLoaded = MRUK.RoomFilter.CurrentRoomOnly;
        nav.NavigableSurfaces = MRUKAnchor.SceneLabels.FLOOR;
        nav.SceneObstacles = MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.STORAGE |
                             MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.OTHER | MRUKAnchor.SceneLabels.PLANT |
                             MRUKAnchor.SceneLabels.LAMP | MRUKAnchor.SceneLabels.SCREEN;
        nav.AgentRadius = 0.3f;
        nav.AgentHeight = 1.8f;
        nav.AgentClimb = 0.1f;
        nav.AgentMaxSlope = 20f;
        nav.CustomAgent = true;
        nav.UseSceneData = true;

        // --- Game roots ---
        // --- Audio: pooled 3D one-shots driven by a key→clip library (clips get filled in as assets arrive) ---
        var sfxGo = new GameObject("[HIS] Sfx");
        var sfx = sfxGo.AddComponent<Sfx>();
        var lib = AssetDatabase.LoadAssetAtPath<SfxLibrary>("Assets/_Game/Audio/SfxLibrary.asset");
        if (lib == null) { lib = ScriptableObject.CreateInstance<SfxLibrary>(); AssetDatabase.CreateAsset(lib, "Assets/_Game/Audio/SfxLibrary.asset"); }
        SetPrivate(sfx, "library", lib);

        var game = new GameObject("[HIS] Game");
        game.AddComponent<MRBootstrap>();
        game.AddComponent<GamePause>();
        game.AddComponent<PlayerState>();
        var scanner = game.AddComponent<RoomScanner>();
        // Door rig: depth-occluded so a real person in the doorway still shows through; small bias for surfaces flush with the wall.
        SetPrivate(scanner, "doorLeafMaterial", OcclusionMaterial("Assets/_Game/Materials/Door_Leaf.mat", new Color(0.86f, 0.84f, 0.8f), 0f, 0.35f, 0.06f));
        SetPrivate(scanner, "doorTrimMaterial", OcclusionMaterial("Assets/_Game/Materials/Door_Trim.mat", new Color(0.9f, 0.89f, 0.86f), 0f, 0.35f, 0.06f));
        // The void is drawn only through the opening (stencil portal, research/19): HIS/DoorVoid + the HIS/DoorPortalMask quad.
        var voidMat = LoadOrCreateMaterial("Assets/_Game/Materials/Door_Void.mat", new Color(0.012f, 0.012f, 0.016f), "HIS/DoorVoid");
        voidMat.SetFloat("_EnvironmentDepthBias", 0.06f);
        EditorUtility.SetDirty(voidMat);
        SetPrivate(scanner, "doorVoidMaterial", voidMat);
        SetPrivate(scanner, "doorPortalMaterial", LoadOrCreateMaterial("Assets/_Game/Materials/Door_PortalMask.mat", Color.clear, "HIS/DoorPortalMask"));
        SetPrivate(scanner, "doorMetalMaterial", OcclusionMaterial("Assets/_Game/Materials/Door_Metal.mat", new Color(0.72f, 0.7f, 0.66f), 0.9f, 0.6f, 0.06f));
        var spawner = game.AddComponent<IntruderSpawner>();
        var alex = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/Intruder_Alex.prefab");
        SetPrivate(spawner, "intruderPrefab", alex != null ? alex.GetComponent<Intruder>() : BuildIntruderPlaceholderPrefab());
        SetPrivate(spawner, "sceneNavigation", nav);

        game.AddComponent<GameSession>();
        // Body frame + holsters follow the head; weapons start holstered.
        var body = rig.AddComponent<PlayerBody>();
        SetPrivate(body, "head", centerEye);
        var loadout = game.AddComponent<Loadout>();
        SetPrivate(loadout, "body", body);
        // The pause menu's loadout choices (Loadout.Slots): the first available option is the default. A new variant is
        // one more Option here — a slot with two or more options gets a menu row. Ids are saved in PlayerPrefs: keep them.
        loadout.Slots = new[]
        {
            LoadoutSlotDef(LoadoutSlot.Sidearm, "Sidearm",
                LoadoutOption("glock19", "Glock", BuildPistolPrefab()),
                LoadoutOption("m9", "M9", M9Builder.WeaponIfModelBuilt())), // "coming soon" only if Model_M9 was never built
            LoadoutSlotDef(LoadoutSlot.Knife, "Knife", LoadoutOption("kabar", "Ka-Bar", BuildKnifePrefab())),
            LoadoutSlotDef(LoadoutSlot.LongGun, "Long gun",
                LoadoutOption("r870", "Shotgun", BuildShotgunPrefab()),     // the default (first available)
                LoadoutOption("ar15", "AR-15", BuildRiflePrefab())),         // Brett 2026-10-03; "coming soon" until HIS > Build AR-15 ran
        };
        EditorUtility.SetDirty(loadout);

        // --- Messages the player must read in any build (pause, "scan your room") ---
        BuildNotice();

        // --- Credits (pause menu row; CC BY needs in-game attribution): CREDITS.md copied into a TextAsset ---
        SetPrivate(game.AddComponent<CreditsMenu>(), "source", CreditsSync.Sync());

        // --- M1a safety: confirm the intruder door, the safe-spot floor ring, near-wall/furniture warnings ---
        var picker = game.AddComponent<DoorPicker>();
        SetPrivate(picker, "highlightMaterial", LoadOrCreateMaterial("Assets/_Game/Materials/FX_DoorHighlight.mat", new Color(1f, 0.78f, 0.2f), "Universal Render Pipeline/Unlit"));
        var safeSpot = game.AddComponent<SafeSpot>();
        var ringMat = LoadOrCreateMaterial("Assets/_Game/Materials/FX_SafeRing.mat", new Color(0.35f, 0.9f, 1f, 0.45f), "HIS/OcclusionUnlitBias"); // (bias 0.06: it lies on the real floor)
        ringMat.renderQueue = 3000; // after opaque; the shader alpha-blends, depth-occluded by real furniture/legs
        EditorUtility.SetDirty(ringMat);
        SetPrivate(safeSpot, "ringMaterial", ringMat);
        game.AddComponent<ProximityGuard>();

        // --- Scenario simulator: yell commands (A/X), the director (one incident at a time + the judge), the end show ---
        var commands = game.AddComponent<CommandSystem>();
        SetPrivate(commands, "left", leftHand);
        SetPrivate(commands, "right", rightHand);
        var director = game.AddComponent<ScenarioDirector>();
        SetPrivate(director, "spawner", spawner);
        var outcome = new GameObject("[HIS] Outcome").AddComponent<OutcomeShow>();
        SetPrivate(outcome, "passthrough", layer); // the one [HIS] PassthroughUnderlay layer (never add a second)
        SetPrivate(outcome, "textMaterial", AssetDatabase.LoadAssetAtPath<Material>(XrTextMaterialPath));
        SetPrivate(outcome, "panelMaterial", AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Materials/UI_Panel.mat"));
        var confettiMat = LoadOrCreateMaterial("Assets/_Game/Materials/FX_Confetti.mat", Color.white, "Universal Render Pipeline/Particles/Unlit");
        confettiMat.SetFloat("_Cull", 0f); EditorUtility.SetDirty(confettiMat);
        SetPrivate(outcome, "confettiMaterial", confettiMat);
        // Bad-call searchlight + height chart (runtime copies): depth-occluded like the ring, but with Meta's 0.06 depth bias
        // so marks lying on a real wall/floor 3 m away aren't eaten by depth noise (research/05).
        var roomFxMat = LoadOrCreateMaterial("Assets/_Game/Materials/FX_RoomLight.mat", Color.white, "HIS/OcclusionUnlitBias");
        roomFxMat.SetFloat("_EnvironmentDepthBias", 0.06f);
        roomFxMat.renderQueue = 3001;
        EditorUtility.SetDirty(roomFxMat);
        SetPrivate(outcome, "fxMaterial", roomFxMat);

        // --- Dev HUD (development builds / Editor only; DebugHud switches itself off in release) ---
        // Lives on the left wrist like a watch (Brett found the head-locked readout strange): look at the back of the left
        // hand to read it. Text faces up out of the controller, top edge toward the fingers.
        var hudGo = new GameObject("[HIS] DebugHud");
        hudGo.transform.SetParent(cameraRig.leftControllerAnchor, false);
        hudGo.transform.localPosition = new Vector3(0f, 0.035f, -0.09f);
        hudGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        hudGo.transform.localScale = Vector3.one * 0.1f; // TMP renders reliably at font sizes ~1-10; scale the object instead
        var hudText = hudGo.AddComponent<TextMeshPro>();
        hudText.fontSize = 0.7f;
        hudText.alignment = TextAlignmentOptions.Center;
        hudText.color = new Color(0.6f, 1f, 0.6f);
        hudText.textWrappingMode = TextWrappingModes.Normal;
        hudText.overflowMode = TextOverflowModes.Overflow;
        hudText.rectTransform.sizeDelta = new Vector2(2.2f, 1.2f);
        ApplyXrTextMaterial(hudText);
        var hud = hudGo.AddComponent<DebugHud>();
        SetPrivate(hud, "text", hudText);
        SetPrivate(hud, "spawner", spawner);
        SetPrivate(hud, "depth", depth);
        SetPrivate(hud, "leftHand", leftHand);
        SetPrivate(hud, "rightHand", rightHand);

        // --- Sanity cube: 1.5 m in front at chest height, so "I see a red cube in my room" = rendering works ---
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "[HIS] SanityCube";
        cube.transform.position = new Vector3(0, 1.2f, 1.5f);
        cube.transform.localScale = Vector3.one * 0.2f;
        cube.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial("Assets/_Game/Materials/Debug_Red.mat", new Color(0.9f, 0.1f, 0.1f), OcclusionLitShader);

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log($"[HIS] Main scene built and saved to {ScenePath}; build settings now contain only this scene");
    }

    /// <summary>World-space message panel: text over an opaque dark plate so it reads against any real room.</summary>
    static void BuildNotice()
    {
        var root = new GameObject("[HIS] Notice");
        var notice = root.AddComponent<Notice>();

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(root.transform, false);
        textGo.transform.localScale = Vector3.one * 0.1f; // TMP renders reliably at font sizes ~1-10; scale the object instead
        var tmp = textGo.AddComponent<TextMeshPro>();
        tmp.fontSize = 3f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.rectTransform.sizeDelta = new Vector2(9f, 3f);
        ApplyXrTextMaterial(tmp);

        var plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
        plate.name = "Backing";
        Object.DestroyImmediate(plate.GetComponent<Collider>());
        plate.transform.SetParent(root.transform, false);
        plate.GetComponent<Renderer>().sharedMaterial =
            LoadOrCreateMaterial("Assets/_Game/Materials/UI_Panel.mat", new Color(0.05f, 0.055f, 0.07f), "Universal Render Pipeline/Unlit");

        SetPrivate(notice, "text", tmp);
        SetPrivate(notice, "backing", plate.transform);
    }

    const string XrTextMaterialPath = "Assets/_Game/Materials/UI_Text_URP.mat";
    const string XrTextShader = "TextMeshPro/SRP/TMP_SDF-URP Unlit";

    /// <summary>
    /// TMP's legacy SDF shaders drew nothing in stereo (single-pass instanced) in the XR Simulator, while a plain camera
    /// rendered them fine. TMP's URP Shader Graph variant is XR-safe: clone the default font's material onto it.
    /// </summary>
    static void ApplyXrTextMaterial(TMP_Text text)
    {
        var font = text.font != null ? text.font : TMP_Settings.defaultFontAsset;
        var shader = Shader.Find(XrTextShader);
        if (font == null || shader == null) { Debug.LogWarning($"[HIS] XR text material skipped (font {font}, shader {shader})"); return; }
        var mat = AssetDatabase.LoadAssetAtPath<Material>(XrTextMaterialPath);
        if (mat == null)
        {
            mat = new Material(font.material) { name = "UI_Text_URP" };
            mat.shader = shader;
            AssetDatabase.CreateAsset(mat, XrTextMaterialPath);
        }
        mat.shader = shader;
        mat.SetTexture("_MainTex", font.atlasTexture);
        EditorUtility.SetDirty(mat);
        text.font = font;
        text.fontSharedMaterial = mat;
    }

    static Material OcclusionMaterial(string path, Color color, float metallic, float gloss, float depthBias)
    {
        var mat = LoadOrCreateMaterial(path, color, "HIS/OcclusionLitPBR");
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Glossiness", gloss);
        mat.SetFloat("_EnvironmentDepthBias", depthBias);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
    }

    static GameObject Instantiate(string prefabPath, string name, Transform parent = null)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) throw new System.IO.FileNotFoundException($"[HIS] Prefab not found: {prefabPath}");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = name;
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    static Material LoadOrCreateMaterial(string path, Color color, string shaderName = "Universal Render Pipeline/Lit", bool transparent = false)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null) throw new System.Exception($"[HIS] Shader not found: {shaderName}");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        mat.color = color; // maps to _Color on Meta's occlusion shader, _BaseColor on URP Lit
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (transparent)
        {
            // URP Unlit transparent surface: Surface=Transparent(1), Blend=Alpha(0), ZWrite off, render queue 3000+
            mat.SetFloat("_Surface", 1); mat.SetFloat("_Blend", 0); mat.SetFloat("_ZWrite", 0);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); mat.EnableKeyword("_ALPHAPREMULTIPLY_ON"); mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.SetFloat("_QueueOffset", 1); // URP recomputes the queue as 3000 + offset on every validate; keep 3001
            mat.renderQueue = 3001;
            mat.SetOverrideTag("RenderType", "Transparent");
        }
        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>Capsule body + sphere head, 1.75 m tall, occlusion-aware material, NavMeshAgent + Intruder logic.</summary>
    static Intruder BuildIntruderPlaceholderPrefab()
    {
        var root = new GameObject("Intruder_Placeholder");
        var agent = root.AddComponent<NavMeshAgent>();
        agent.radius = 0.3f; agent.height = 1.75f; agent.speed = 1.1f; agent.angularSpeed = 240f; agent.acceleration = 4f;
        agent.stoppingDistance = 0.6f; agent.baseOffset = 0f;
        root.AddComponent<Intruder>();

        var clothes = LoadOrCreateMaterial("Assets/_Game/Materials/Intruder_Clothes.mat", new Color(0.12f, 0.12f, 0.14f), OcclusionLitShader);
        var skin = LoadOrCreateMaterial("Assets/_Game/Materials/Intruder_Skin.mat", new Color(0.8f, 0.6f, 0.5f), OcclusionLitShader);

        root.AddComponent<WoundSystem>();
        var wounds = root.GetComponent<WoundSystem>();
        var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/_Game/Textures/BloodSplatAtlas.png");
        var bloodMat = LoadOrCreateMaterial("Assets/_Game/Materials/Blood_Decal.mat", Color.white, "Universal Render Pipeline/Unlit", transparent: true);
        var splatMat = LoadOrCreateMaterial("Assets/_Game/Materials/Blood_Splatter.mat", new Color(1f, 1f, 1f, 0.9f), "Universal Render Pipeline/Unlit", transparent: true);
        foreach (var m in new[] { bloodMat, splatMat }) { m.SetTexture("_BaseMap", atlas); m.mainTexture = atlas; EditorUtility.SetDirty(m); }
        SetPrivate(wounds, "bloodMaterial", bloodMat);
        SetPrivate(wounds, "wallSplatterMaterial", splatMat);

        // Body regions = hitboxes. Placeholder proportions; real model gets per-bone boxes.
        var torso = Part(root, "Torso", PrimitiveType.Capsule, clothes, new Vector3(0, 1.15f, 0), new Vector3(0.42f, 0.34f, 0.28f), BodyPart.Torso, 1f);
        var abdomen = Part(root, "Abdomen", PrimitiveType.Capsule, clothes, new Vector3(0, 0.82f, 0), new Vector3(0.38f, 0.16f, 0.26f), BodyPart.Abdomen, 0.9f);
        Part(root, "Head", PrimitiveType.Sphere, skin, new Vector3(0, 1.63f, 0), Vector3.one * 0.27f, BodyPart.Head, 3f);
        Part(root, "Neck", PrimitiveType.Cylinder, skin, new Vector3(0, 1.48f, 0), new Vector3(0.13f, 0.05f, 0.13f), BodyPart.Neck, 2f);
        Part(root, "LeftArm", PrimitiveType.Capsule, clothes, new Vector3(-0.27f, 1.1f, 0), new Vector3(0.11f, 0.32f, 0.11f), BodyPart.LeftArm, 0.6f);
        var rightArm = Part(root, "RightArm", PrimitiveType.Capsule, clothes, new Vector3(0.27f, 1.1f, 0), new Vector3(0.11f, 0.32f, 0.11f), BodyPart.RightArm, 0.6f);
        SetPrivate(root.GetComponent<Intruder>(), "strikingArm", rightArm.transform);
        Part(root, "LeftLeg", PrimitiveType.Capsule, clothes, new Vector3(-0.1f, 0.4f, 0), new Vector3(0.15f, 0.4f, 0.15f), BodyPart.LeftLeg, 0.7f);
        Part(root, "RightLeg", PrimitiveType.Capsule, clothes, new Vector3(0.1f, 0.4f, 0), new Vector3(0.15f, 0.4f, 0.15f), BodyPart.RightLeg, 0.7f);

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, IntruderPrefabPath);
        Object.DestroyImmediate(root);
        return prefab.GetComponent<Intruder>();
    }

    static GameObject Part(GameObject root, string name, PrimitiveType type, Material mat, Vector3 pos, Vector3 scale, BodyPart part, float mult)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name; go.transform.SetParent(root.transform, false);
        go.transform.localPosition = pos; go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        var hb = go.AddComponent<Hitbox>(); hb.Part = part; hb.Multiplier = mult;
        return go;
    }

    static HandController MakeHand(Transform anchor, OVRInput.Controller controller)
    {
        bool left = controller == OVRInput.Controller.LTouch;
        var go = new GameObject(left ? "[HIS] Hand Left" : "[HIS] Hand Right");
        go.transform.SetParent(anchor, false);
        var hand = go.AddComponent<HandController>();
        var so = new SerializedObject(hand);
        so.FindProperty("controller").enumValueFlag = (int)controller;
        so.ApplyModifiedPropertiesWithoutUndo();
        // The virtual hand on a held weapon: Meta's OculusHand mesh as shipped in the Core package, stylised grey (research/16 §3.3).
        var grip = go.AddComponent<GripHand>();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>($"Packages/com.meta.xr.sdk.core/Meshes/HandTracking/OculusHand_{(left ? "L" : "R")}.fbx");
        if (model == null) Debug.LogWarning("[HIS] OculusHand model missing — no gun hands");
        SetPrivate(grip, "handModel", model);
        var mat = LoadOrCreateMaterial("Assets/_Game/Materials/Hand_Grey.mat", GripHand.HandColor, "HIS/LitPBR");
        mat.SetFloat("_Metallic", 0f); mat.SetFloat("_Glossiness", 0.3f); EditorUtility.SetDirty(mat);
        SetPrivate(grip, "material", mat);
        return hand;
    }

    /// <summary>The virtual-hand data for a weapon (GripPoses holds the poses; see HandSnapshots for renders).</summary>
    static Loadout.Slot LoadoutSlotDef(LoadoutSlot kind, string label, params Loadout.Option[] options) =>
        new() { Kind = kind, Label = label, Options = options };

    static Loadout.Option LoadoutOption(string id, string label, WeaponBase prefab)
    {
        if (prefab == null) Debug.Log($"[HIS] Loadout option '{label}' has no prefab yet — listed as coming soon");
        return new Loadout.Option { Id = id, Label = label, Prefab = prefab };
    }

    static WeaponGrip AddGrip(GameObject root, GripKind kind, Transform frame, Transform trigger, float triggerDeg)
    {
        var g = root.AddComponent<WeaponGrip>();
        g.Kind = kind; g.Frame = frame; g.Trigger = trigger; g.TriggerMaxDegrees = triggerDeg;
        if (trigger == null && kind != GripKind.Knife) Debug.LogWarning($"[HIS] {root.name}: no trigger part to animate");
        return g;
    }

    /// <summary>The held knife's blade: straight ahead, this many degrees above the hand's aim line (it used to lie on it).</summary>
    const float KnifeRaise = 35f;

    // The G19 grip the holster pose was tuned against (research/15): GripPoint used to sit 3.5 cm above the magazine base.
    static readonly Vector3 LegacyPistolGrip = new(0f, -0.02358f, -0.07368f);

    static GameObject ModelInstance(string path, Transform parent)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new System.IO.FileNotFoundException("[HIS] model prefab missing: " + path);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = "Model"; go.transform.SetParent(parent, false);
        return go;
    }

    static BoxCollider FitBox(GameObject root, GameObject model, float pad = 0.005f)
    {
        var rs = model.GetComponentsInChildren<Renderer>(true).Where(r => r.gameObject.activeInHierarchy).ToArray();
        var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
        var col = root.AddComponent<BoxCollider>();
        col.center = root.transform.InverseTransformPoint(b.center);
        col.size = b.size + Vector3.one * pad * 2f;
        return col;
    }

    /// <summary>Glock 19 (Sketchfab/Rotuma) — semi-auto, 15+1, separate slide.</summary>
    static WeaponBase BuildPistolPrefab()
    {
        var root = new GameObject("Weapon_Pistol");
        var rb = root.AddComponent<Rigidbody>(); rb.mass = 0.7f;
        var grabbable = root.AddComponent<Grabbable>();
        var gun = root.AddComponent<Firearm>(); gun.DisplayName = "Glock 19"; gun.SfxKey = "pistol";
        gun.HolsterType = HolsterType.Hip;
        gun.HolsterOffsetEuler = new Vector3(95f, 0, 0); // muzzle down the thigh, grip to the rear, 5° cant (research/15)
        gun.Configure(dmg: 34f, pelletCount: 1, spread: 0.6f, rps: 6f, capacity: 15, reserve: 4, pump: false, shells: false, travel: 0.035f, kick: 4f);
        root.AddComponent<GunEffects>();

        var model = ModelInstance("Assets/_Game/Models/Model_G19.prefab", root.transform);
        // Place the model so the grip point sits at the weapon origin (that's what snaps to the hand).
        var grip = model.transform.Find("GripPoint");
        model.transform.localPosition = -grip.localPosition; model.transform.localRotation = Quaternion.identity;
        // GripPoint moved up to the tang (research/16 §3.1): shift the holster by the same amount so the holstered gun
        // sits exactly where research/15 tuned it.
        gun.HolsterOffsetPosition = Quaternion.Euler(gun.HolsterOffsetEuler) * (grip.localPosition - LegacyPistolGrip);
        var gripPoint = new GameObject("GripPoint"); gripPoint.transform.SetParent(root.transform, false); gripPoint.transform.localRotation = grip.localRotation;
        FitBox(root, model);
        SetPrivate(grabbable, "gripPoint", gripPoint.transform);
        SetPrivate(gun, "muzzle", model.transform.Find("Muzzle"));
        SetPrivate(gun, "slide", model.transform.Find("Parts/Slide"));
        var fx = root.GetComponent<GunEffects>();
        SetPrivate(fx, "muzzle", model.transform.Find("Muzzle"));
        SetPrivate(fx, "ejectionPort", model.transform.Find("Parts/Slide"));
        AddGrip(root, GripKind.Pistol, model.transform, model.transform.Find("Parts/Frame/Trigger"), 20f);

        SetLayerRecursive(root, QuestProjectSetup.WeaponLayer);
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PistolPrefabPath);
        Object.DestroyImmediate(root);
        return prefab.GetComponent<WeaponBase>();
    }

    /// <summary>Remington 870 (Sketchfab/tris09) — pump action, 5 shells, 8 pellets.</summary>
    static WeaponBase BuildShotgunPrefab()
    {
        const string path = "Assets/_Game/Prefabs/Weapon_Shotgun.prefab";
        var root = new GameObject("Weapon_Shotgun");
        var rb = root.AddComponent<Rigidbody>(); rb.mass = 3.2f;
        var grabbable = root.AddComponent<Grabbable>();
        var gun = root.AddComponent<Firearm>(); gun.DisplayName = "Remington 870"; gun.SfxKey = "shotgun";
        gun.HolsterType = HolsterType.Back;
        gun.HolsterOffsetEuler = Vector3.zero; // the back slot sets the sling pose
        // 00 buck: 9 pellets × 13 in a tight 1.6° cone — a solid torso hit at room range (≈117) drops him in one shot.
        gun.Configure(dmg: 13f, pelletCount: 9, spread: 1.6f, rps: 1.3f, capacity: 5, reserve: 12, pump: true, shells: true, travel: 0.08f, kick: 9f, back: 0.04f);
        root.AddComponent<GunEffects>();

        var model = ModelInstance("Assets/_Game/Models/Model_R870.prefab", root.transform);
        // GripPoint = the web of the hand on top of the stock's wrist, just behind the receiver (research/16 §3.4 step 6,
        // read off Logs/poses/models_shotgun.png): 7 cm behind and 4 cm above the trigger, like the pistol's tang → trigger.
        // It used to be 3 cm below the barrel line inside the wrist, which put the gun low in the hand like the old pistol.
        var web = new Vector3(0f, 0.004f, -0.192f);
        var legacyWeb = new Vector3(0f, -0.03f, -0.2f);
        model.transform.localPosition = -web;
        gun.HolsterOffsetPosition = web - legacyWeb; // sling pose unchanged (the back slot's rotation is identity)
        // Same grip pitch as the pistol (−15°) so both guns point the same way in the hand (−10° sat 5° high).
        var gripPoint = new GameObject("GripPoint"); gripPoint.transform.SetParent(root.transform, false); gripPoint.transform.localRotation = Quaternion.Euler(-15f, 0, 0);
        var muzzle = new GameObject("Muzzle"); muzzle.transform.SetParent(model.transform, false); muzzle.transform.localPosition = new Vector3(0, 0.02f, 0.5f);
        // Grab anywhere from the grip back to the butt of the stock (it's slung stock-up at the right shoulder). Model frame → root.
        var stockEnd = new GameObject("StockEnd"); stockEnd.transform.SetParent(root.transform, false); stockEnd.transform.localPosition = new Vector3(0, -0.05f, -0.48f) - web;
        // Hulls leave the receiver's ejection port (right side, ahead of the grip) on the pump's back-stroke.
        var ejectPort = new GameObject("EjectionPort"); ejectPort.transform.SetParent(root.transform, false); ejectPort.transform.localPosition = new Vector3(0.03f, 0.01f, -0.08f) - web;
        FitBox(root, model);
        SetPrivate(grabbable, "gripPoint", gripPoint.transform);
        SetPrivate(grabbable, "grabZoneEnd", stockEnd.transform);
        SetPrivate(gun, "muzzle", muzzle.transform);
        SetPrivate(gun, "slide", model.transform.Find("Pump"));
        var fx = root.GetComponent<GunEffects>();
        SetPrivate(fx, "muzzle", muzzle.transform);
        SetPrivate(fx, "ejectionPort", ejectPort.transform);
        var so = new SerializedObject(fx);
        so.FindProperty("ejectDirectionLocal").vector3Value = new Vector3(1f, 0.5f, 0f);
        so.FindProperty("flashScale").floatValue = 1.8f;
        so.FindProperty("casingColor").colorValue = new Color(0.7f, 0.08f, 0.06f); // red 12-gauge hull
        so.FindProperty("casingSize").floatValue = 0.032f;
        so.FindProperty("ejectOnFire").boolValue = false;
        so.ApplyModifiedPropertiesWithoutUndo();
        var sg = AddGrip(root, GripKind.ShotgunRear, model.transform, model.transform.Find("Trigger"), 12f);
        sg.SupportKind = GripKind.ShotgunPump; sg.SupportFrame = model.transform.Find("Pump");

        SetLayerRecursive(root, QuestProjectSetup.WeaponLayer);
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab.GetComponent<WeaponBase>();
    }

    // The slung shotgun's stock end in the back slot's frame (BuildShotgunPrefab: StockEnd (0, −0.05, −0.48) − its legacy web
    // (0, −0.03, −0.2), the holster offset cancelling the new web): where the player's hand finds a long gun over his right shoulder.
    static readonly Vector3 SlungStockEnd = new(0f, -0.02f, -0.28f);
    /// <summary>The AR-15 sits this much further out from the back than the shotgun (m, the back slot's +y: away from the body),
    /// receiver and red dot behind the back; its stock end 5 cm behind the shotgun's, well inside the grab zone (Grabbable 12 cm).</summary>
    const float SlungBack = 0.05f;

    /// <summary>
    /// AR-15 (Sketchfab/SpatialNeglect, CC BY 4.0; "AR-15" in game, never a brand) — the second Long gun option (Brett 2026-10-03:
    /// "a semi automatic ar 15 we can swap out for the shotgun"): semi-auto 5.56, one round per pull (≤ 10 a second), 30 + 1, a
    /// red dot. Rounds kill by Brett's rule like every gun's (Hitbox.LethalRounds: one in the head or centre mass, two in the limbs);
    /// 55 a round (the pistols' 34) only matters to the graded-HP tests and through-door energy. Slung where the shotgun goes, the
    /// stock end at the same spot over the right shoulder. Sounds: no rifle recordings are staged, so it borrows pistol.* a little
    /// deeper (Firearm sfxPitch 0.88). Null (listed "coming soon") until AR15Builder has made Model_AR15.
    /// </summary>
    public static WeaponBase BuildRiflePrefab()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(AR15Builder.ModelPath) == null) return null;
        var root = new GameObject("Weapon_AR15");
        var rb = root.AddComponent<Rigidbody>(); rb.mass = 3.0f; // a carbine with a loaded 30-round magazine and a red dot (prep §4)
        var grabbable = root.AddComponent<Grabbable>();
        var gun = root.AddComponent<Firearm>(); gun.DisplayName = "AR-15"; gun.SfxKey = "pistol";
        gun.HolsterType = HolsterType.Back;
        // Rolled 90° about the bore, its side flat against the back like a real slung carbine (the back slot's own pose has the
        // top of the gun away from the body). Unrolled, the pistol grip and the magazine hung into the torso; rolled 180° (the
        // first fix), the red dot and its housing did instead — a weapon is drawn over passthrough, so either showed as black
        // shapes over the belly beside the holster when you looked down (the shotgun has nothing under it). On its side the
        // magazine and grip point up the back toward the spine (away from the hip you draw from), the red dot down the back to the right.
        // Measured in the chest frame (the fixer's probe, 2026-10-05; ScenarioTests.Rifle checks it): forward-most part below
        // the chest −1.9 cm on its side vs +4.2 rolled 180° and +8.7 unrolled (the shotgun −2.4); with SlungBack it's −6.8 cm,
        // all of it behind the neck line, and ~4% of it in view pitched 60° down (unrolled ~42%, the shotgun ~23%).
        gun.HolsterOffsetEuler = new Vector3(0f, 0f, 90f);
        // Light 5.56: a tight 0.25° cone, half the pistols' climb and a short straight-back push into the shoulder that settles
        // fast (recover 16 vs 12) — much less than the shotgun's 9°.
        gun.Configure(dmg: 55f, pelletCount: 1, spread: 0.25f, rps: 10f, capacity: 30, reserve: 4, pump: false, shells: false, travel: 0.020f, kick: 2.5f, back: 0.018f);
        root.AddComponent<GunEffects>();

        var model = ModelInstance(AR15Builder.ModelPath, root.transform);
        var grip = model.transform.Find("GripPoint");
        model.transform.localPosition = -grip.localPosition; model.transform.localRotation = Quaternion.identity;
        var gripPoint = new GameObject("GripPoint"); gripPoint.transform.SetParent(root.transform, false); gripPoint.transform.localRotation = grip.localRotation;
        // Grab anywhere from the grip back to the butt plate (slung stock-up at the right shoulder, like the shotgun).
        var stockEnd = new GameObject("StockEnd"); stockEnd.transform.SetParent(root.transform, false);
        stockEnd.transform.localPosition = model.transform.Find("StockEnd").localPosition - grip.localPosition;
        gun.HolsterOffsetPosition = SlungStockEnd - Quaternion.Euler(gun.HolsterOffsetEuler) * stockEnd.transform.localPosition
                                    + new Vector3(0f, SlungBack, 0f);
        FitBox(root, model);
        SetPrivate(grabbable, "gripPoint", gripPoint.transform);
        SetPrivate(grabbable, "grabZoneEnd", stockEnd.transform);
        var muzzle = model.transform.Find("Muzzle");
        SetPrivate(gun, "muzzle", muzzle);
        SetPrivate(gun, "slide", model.transform.Find("Parts/Bolt")); // the bolt carrier: 20 mm (the mesh is only the 84 mm seen in the port)
        var cover = model.transform.Find("Parts/Frame/DustCover");
        gun.ConfigureRifle(cover, -AR15Builder.CoverClosedDegrees(cover), model.transform.Find("Parts/ChargingHandle"), 0.06f,
                           ActionMotion.Rifle, reload: 1.8f, recover: 16f, pitch: 0.88f);
        var fx = root.GetComponent<GunEffects>();
        SetPrivate(fx, "muzzle", muzzle);
        SetPrivate(fx, "ejectionPort", model.transform.Find("Parts/Frame/EjectionPort"));
        var so = new SerializedObject(fx);
        so.FindProperty("ejectDirectionLocal").vector3Value = new Vector3(1f, 0.4f, 0.3f); // AR brass: right, a little up and forward
        so.FindProperty("ejectSpeed").floatValue = 3f;
        so.FindProperty("flashScale").floatValue = 1.3f;                                   // a bit bigger than the pistols' (shotgun 1.8)
        so.ApplyModifiedPropertiesWithoutUndo();
        var g = AddGrip(root, GripKind.RifleGrip, model.transform, model.transform.Find("Parts/Frame/Trigger"), 11f);
        g.SupportKind = GripKind.RifleHandguard; g.SupportFrame = model.transform.Find("Handguard"); // static: no pump stroke

        SetLayerRecursive(root, QuestProjectSetup.WeaponLayer);
        var back = Quaternion.Euler(gun.HolsterOffsetEuler);
        Debug.Log($"[HIS] Weapon_AR15: holster offset {gun.HolsterOffsetPosition.ToString("F4")} (stock end at {(SlungStockEnd + new Vector3(0f, SlungBack, 0f)).ToString("F3")}: the shotgun's, {SlungBack * 100f:F0} cm further out; " +
                  $"muzzle at {(gun.HolsterOffsetPosition + back * (model.transform.localPosition + muzzle.localPosition)).ToString("F3")} in the back slot), " +
                  $"dust cover opens {-AR15Builder.CoverClosedDegrees(cover):F1}°");
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, AR15Builder.WeaponPath);
        Object.DestroyImmediate(root);
        return prefab.GetComponent<WeaponBase>();
    }

    /// <summary>Ka-Bar (Sketchfab/vitvitskyi) — blade along +Z, handle at −Z.</summary>
    static WeaponBase BuildKnifePrefab()
    {
        var root = new GameObject("Weapon_Knife");
        var rb = root.AddComponent<Rigidbody>(); rb.mass = 0.3f;
        var grabbable = root.AddComponent<Grabbable>();
        var knife = root.AddComponent<Knife>(); knife.DisplayName = "Ka-Bar";
        knife.HolsterType = HolsterType.Hip;
        knife.HolsterOffsetEuler = new Vector3(90f, 0, 0); // blade pointing down the leg

        var model = ModelInstance("Assets/_Game/Models/Model_KaBar.prefab", root.transform);
        // Model is centred on its overall length (31 cm): handle z ∈ [−0.137, −0.024], guard at −0.023, blade z ∈ [0, 0.155],
        // edge down. GripPoint = the web of a hammer grip: on the spine side of the handle right behind the guard.
        var web = new Vector3(0f, 0.018f, -0.034f);
        var legacyWeb = new Vector3(0f, 0f, -0.09f); // mid-handle, where the holster pose was set
        model.transform.localPosition = -web;
        knife.HolsterOffsetPosition = Quaternion.Euler(knife.HolsterOffsetEuler) * (web - legacyWeb);
        // Hammer grip: the knife sits in the fist the way the pistol's grip does, so the virtual hand's wrist matches the
        // real one: knife-in-aim = pose(pistol)·pose(knife)⁻¹ with the pistol's grip rotation. That points the blade out of the
        // top of the fist (up, and a little sideways); the smallest turn that brings it straight ahead, raised
        // KnifeRaise above the aim line, keeps the hand's roll (a knife held ready leans forward; straight up felt like a torch).
        var inAim = Quaternion.Inverse(Quaternion.Euler(-15f, 0, 0)) * GripPoses.Pistol.Rotation * Quaternion.Inverse(GripPoses.Knife.Rotation);
        var blade = new Vector3(0f, Mathf.Sin(KnifeRaise * Mathf.Deg2Rad), Mathf.Cos(KnifeRaise * Mathf.Deg2Rad));
        inAim = Quaternion.FromToRotation(inAim * Vector3.forward, blade) * inAim;
        var gripRot = Quaternion.Inverse(inAim);
        var gripPoint = new GameObject("GripPoint"); gripPoint.transform.SetParent(root.transform, false); gripPoint.transform.localRotation = gripRot;
        Vector3 M(float x, float y, float z) => new Vector3(x, y, z) - web; // model frame → root
        var bladeBase = new GameObject("BladeBase"); bladeBase.transform.SetParent(root.transform, false); bladeBase.transform.localPosition = M(0, 0, 0);
        var bladeTip = new GameObject("BladeTip"); bladeTip.transform.SetParent(root.transform, false); bladeTip.transform.localPosition = M(0, 0, 0.155f);
        var bladeCol = root.AddComponent<BoxCollider>(); bladeCol.isTrigger = true; bladeCol.center = M(0, 0, 0.08f); bladeCol.size = new Vector3(0.03f, 0.045f, 0.16f);
        var handleCol = root.AddComponent<CapsuleCollider>(); handleCol.direction = 2; handleCol.center = M(0, 0, -0.1f); handleCol.height = 0.13f; handleCol.radius = 0.016f;
        SetPrivate(grabbable, "gripPoint", gripPoint.transform);
        SetPrivate(knife, "bladeBase", bladeBase.transform);
        SetPrivate(knife, "bladeTip", bladeTip.transform);
        AddGrip(root, GripKind.Knife, model.transform, null, 0f);

        SetLayerRecursive(root, QuestProjectSetup.WeaponLayer);
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, KnifePrefabPath);
        Object.DestroyImmediate(root);
        return prefab.GetComponent<WeaponBase>();
    }

    static void SetPrivate(Object target, string field, Object value)
    {
        var so = new SerializedObject(target);
        so.FindProperty(field).objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
