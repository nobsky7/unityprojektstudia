using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace AutoParking
{
    public sealed class AutoParkingSimulation : MonoBehaviour
    {
        private void Awake()
        {
            if (GetComponent<ParkingSimulation>() == null)
            {
                gameObject.AddComponent<ParkingSimulation>();
            }
        }
    }

    public enum ParkingScenario
    {
        Perpendicular = 0,
        Parallel = 1,
        AngledDynamic = 2
    }

    public enum ParkingManeuverStyle
    {
        Perpendicular,
        Parallel,
        Angled
    }

    public enum ParkingSide
    {
        Left = -1,
        Right = 1
    }

    [Serializable]
    public struct VehicleDimensions
    {
        public float Width;
        public float Length;
        public float WheelBase;
        public float TrackWidth;

        public static VehicleDimensions Default()
        {
            return new VehicleDimensions
            {
                Width = 2.0f,
                Length = 4.5f,
                WheelBase = 2.7f,
                TrackWidth = 1.62f
            };
        }
    }

    public sealed class ParkingProfile
    {
        public ParkingScenario Scenario;
        public ParkingManeuverStyle Style;
        public ParkingSide SearchSide;
        public VehicleDimensions Dimensions;
        public Vector3 LaneForward;
        public float RequiredGapLength;
        public float RequiredDepth;
        public float ScanWindowLength;
        public float SideScanRange;
        public float TargetLateralOffset;
        public float SearchSpeed;
        public float ParkingSpeed;
        public float EmergencyDistance;
        public float MaxSearchDistance;
        public bool UseScriptedTarget;
        public Vector3 ScriptedTargetPosition;
        public Vector3 ScriptedTargetForward;
        public float ScriptedTriggerAlongLane;
        public float ScriptedMeasuredLength;
        public float ScriptedMeasuredDepth;
    }

    public struct SensorSnapshot
    {
        public bool SideClear;
        public bool EmergencyBlocked;
        public float SideDistance;
        public float ForwardDistance;
        public float RearDistance;
        public Vector3 LastSideHitPoint;
        public Vector3 LastEmergencyHitPoint;
    }

    public struct ParkingCandidate
    {
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public ParkingManeuverStyle Style;
        public float MeasuredLength;
        public float MeasuredDepth;
    }

    public struct ParkingWaypoint
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Reverse;
        public float Speed;
        public float Radius;

        public ParkingWaypoint(Vector3 position, Quaternion rotation, bool reverse, float speed, float radius)
        {
            Position = position;
            Rotation = rotation;
            Reverse = reverse;
            Speed = speed;
            Radius = radius;
        }
    }

    public static class AutoParkingBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateSimulationIfMissing()
        {
            if (UnityEngine.Object.FindAnyObjectByType<AutoParkingSimulation>() != null)
            {
                return;
            }

            if (UnityEngine.Object.FindAnyObjectByType<ParkingSimulation>() != null)
            {
                return;
            }

            GameObject root = new GameObject("Auto Parking Simulation");
            root.AddComponent<ParkingSimulation>();
        }
    }

    public sealed class ParkingSimulation : MonoBehaviour
    {
        private readonly List<GameObject> spawnedObjects = new List<GameObject>();
        private ParkingScenario currentScenario;
        private ParkingAgent agent;
        private Camera sceneCamera;
        private Material asphaltMaterial;
        private Material lineMaterial;
        private Material obstacleMaterial;
        private Material agentMaterial;
        private Material targetMaterial;
        private Material grassMaterial;
        private Text stateText;
        private Text sensorText;
        private Text candidateText;
        private Text speedText;
        private Text collisionText;
        private Font uiFont;

        public ParkingScenario CurrentScenario => currentScenario;

        private void Awake()
        {
            Application.targetFrameRate = 60;
            PrepareMaterials();
            EnsureLight();
            BuildRuntimeUi();
            BuildScenario(ParkingScenario.Perpendicular);
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame)
            {
                BuildScenario(ParkingScenario.Perpendicular);
            }
            else if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame)
            {
                BuildScenario(ParkingScenario.Parallel);
            }
            else if (keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame)
            {
                BuildScenario(ParkingScenario.AngledDynamic);
            }
            else if (keyboard.rKey.wasPressedThisFrame)
            {
                BuildScenario(currentScenario);
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Alpha1))
            {
                BuildScenario(ParkingScenario.Perpendicular);
            }
            else if (Input.GetKeyDown(KeyCode.Alpha2))
            {
                BuildScenario(ParkingScenario.Parallel);
            }
            else if (Input.GetKeyDown(KeyCode.Alpha3))
            {
                BuildScenario(ParkingScenario.AngledDynamic);
            }
            else if (Input.GetKeyDown(KeyCode.R))
            {
                BuildScenario(currentScenario);
            }
#endif

            RefreshRuntimeUi();
        }

        private void BuildRuntimeUi()
        {
            if (FindAnyObjectByType<EventSystem>() == null)
            {
                GameObject eventSystem = new GameObject("EventSystem");
                eventSystem.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
                eventSystem.AddComponent<InputSystemUIInputModule>();
#else
                eventSystem.AddComponent<StandaloneInputModule>();
#endif
            }

            GameObject canvasObject = new GameObject("Auto Parking Runtime UI");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject panel = new GameObject("Control Panel");
            panel.transform.SetParent(canvasObject.transform, false);
            RectTransform panelRect = panel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(12f, -12f);
            panelRect.sizeDelta = new Vector2(390f, 250f);
            Image panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0.04f, 0.04f, 0.04f, 0.78f);

            VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 8, 8);
            layout.spacing = 5f;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            stateText = CreateLabel(panel.transform, "Auto Parking - deterministic FSM", 16, FontStyle.Bold);
            sensorText = CreateLabel(panel.transform, "Side clear: -", 13, FontStyle.Normal);
            candidateText = CreateLabel(panel.transform, "Candidate: -", 13, FontStyle.Normal);
            speedText = CreateLabel(panel.transform, "Speed: -", 13, FontStyle.Normal);
            collisionText = CreateLabel(panel.transform, string.Empty, 13, FontStyle.Normal);

            CreateButton(panel.transform, "Mapa 1 - parking prostopadly", () => BuildScenario(ParkingScenario.Perpendicular));
            CreateButton(panel.transform, "Mapa 2 - parkowanie rownolegle", () => BuildScenario(ParkingScenario.Parallel));
            CreateButton(panel.transform, "Mapa 3 - parking ukosny + przeszkoda", () => BuildScenario(ParkingScenario.AngledDynamic));
            CreateButton(panel.transform, "Restart aktualnej mapy", () => BuildScenario(currentScenario));
        }

        private Text CreateLabel(Transform parent, string text, int fontSize, FontStyle style)
        {
            GameObject label = new GameObject("Label");
            label.transform.SetParent(parent, false);
            Text uiText = label.AddComponent<Text>();
            uiText.font = GetUiFont();
            uiText.fontSize = fontSize;
            uiText.fontStyle = style;
            uiText.color = Color.white;
            uiText.text = text;
            uiText.horizontalOverflow = HorizontalWrapMode.Wrap;
            uiText.verticalOverflow = VerticalWrapMode.Overflow;

            RectTransform rect = label.GetComponent<RectTransform>();
            float height = style == FontStyle.Bold ? 40f : fontSize + 9f;
            rect.sizeDelta = new Vector2(0f, height);
            LayoutElement layoutElement = label.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = height;
            return uiText;
        }

        private void CreateButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            GameObject buttonObject = new GameObject(label);
            buttonObject.transform.SetParent(parent, false);
            Image image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.18f, 0.18f, 0.18f, 0.95f);
            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0f, 24f);
            LayoutElement layoutElement = buttonObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 24f;

            GameObject textObject = new GameObject("Text");
            textObject.transform.SetParent(buttonObject.transform, false);
            RectTransform textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            Text text = textObject.AddComponent<Text>();
            text.font = GetUiFont();
            text.fontSize = 14;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = label;
        }

        private Font GetUiFont()
        {
            if (uiFont == null)
            {
                uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (uiFont == null)
                {
                    uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
            }

            return uiFont;
        }

        private void RefreshRuntimeUi()
        {
            if (stateText == null)
            {
                return;
            }

            stateText.text = "Auto Parking - deterministic FSM\nState: " + (agent != null ? agent.CurrentStateName : "none");
            if (agent == null)
            {
                sensorText.text = "Side clear: -";
                candidateText.text = "Candidate: -";
                speedText.text = "Speed: -";
                collisionText.text = string.Empty;
                return;
            }

            SensorSnapshot sensors = agent.Sensors != null ? agent.Sensors.Snapshot : default;
            sensorText.text = "Side clear: " + sensors.SideClear + " | side distance: " + sensors.SideDistance.ToString("0.0") + " m";
            candidateText.text = "Candidate: " + (agent.HasCandidate ? agent.ActiveCandidate.Style + " gap " + agent.ActiveCandidate.MeasuredLength.ToString("0.0") + " m" : "searching");
            speedText.text = "Speed: " + agent.Controller.CurrentSpeed.ToString("0.0") + " m/s | steering: " + agent.Controller.CurrentSteerAngle.ToString("0") + " deg";
            collisionText.text = agent.Controller.HadCollision ? "Collision detected - reset the map after inspection." : string.Empty;
        }

        private void BuildScenario(ParkingScenario scenario)
        {
            currentScenario = scenario;

            foreach (GameObject spawned in spawnedObjects)
            {
                if (spawned != null)
                {
                    Destroy(spawned);
                }
            }

            spawnedObjects.Clear();
            Transform scenarioRoot = CreateRoot("Generated Scenario " + (int)scenario);

            ParkingProfile profile;
            Pose startPose;
            switch (scenario)
            {
                case ParkingScenario.Parallel:
                    profile = ParkingEnvironmentBuilder.BuildParallelMap(
                        scenarioRoot,
                        asphaltMaterial,
                        lineMaterial,
                        obstacleMaterial,
                        grassMaterial,
                        out startPose);
                    ConfigureCamera(new Vector3(3f, 29f, -1f), 28f);
                    break;
                case ParkingScenario.AngledDynamic:
                    profile = ParkingEnvironmentBuilder.BuildAngledDynamicMap(
                        scenarioRoot,
                        asphaltMaterial,
                        lineMaterial,
                        obstacleMaterial,
                        grassMaterial,
                        targetMaterial,
                        out startPose);
                    ConfigureCamera(new Vector3(4f, 30f, -1f), 28f);
                    break;
                default:
                    profile = ParkingEnvironmentBuilder.BuildPerpendicularMap(
                        scenarioRoot,
                        asphaltMaterial,
                        lineMaterial,
                        obstacleMaterial,
                        grassMaterial,
                        targetMaterial,
                        out startPose);
                    ConfigureCamera(new Vector3(2f, 29f, -1f), 30f);
                    break;
            }

            GameObject car = ParkingEnvironmentBuilder.CreateAgentCar(
                scenarioRoot,
                startPose.position,
                startPose.rotation,
                agentMaterial,
                profile.Dimensions);

            spawnedObjects.Add(scenarioRoot.gameObject);
            agent = car.GetComponent<ParkingAgent>();
            agent.Configure(profile);
        }

        private Transform CreateRoot(string objectName)
        {
            GameObject root = new GameObject(objectName);
            return root.transform;
        }

        private void PrepareMaterials()
        {
            asphaltMaterial = NewMaterial("Asphalt", new Color(0.09f, 0.11f, 0.13f));
            lineMaterial = NewMaterial("Line", new Color(0.82f, 0.86f, 0.86f));
            obstacleMaterial = NewMaterial("ObstacleCar", new Color(0.02f, 0.025f, 0.03f));
            agentMaterial = NewMaterial("AgentCar", new Color(0.88f, 0.92f, 0.95f));
            targetMaterial = NewMaterial("TargetMarker", new Color(0.96f, 0.18f, 0.12f));
            grassMaterial = NewMaterial("Concrete", new Color(0.25f, 0.27f, 0.27f));
        }

        private static Material NewMaterial(string materialName, Color color)
        {
            Material material = CreateSafeMaterial(materialName);
            material.color = color;
            return material;
        }

        private static Material CreateSafeMaterial(string materialName)
        {
            Shader shader =
                Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Standard");

            if (shader != null)
            {
                return new Material(shader) { name = materialName };
            }

            Material builtInMaterial =
                Resources.GetBuiltinResource<Material>("Default-Material.mat") ??
                Resources.GetBuiltinResource<Material>("Default-Diffuse.mat");

            if (builtInMaterial != null)
            {
                Material copy = UnityEngine.Object.Instantiate(builtInMaterial);
                copy.name = materialName;
                return copy;
            }

            return new Material(Shader.Find("Hidden/InternalErrorShader")) { name = materialName };
        }

        private void ConfigureCamera(Vector3 position, float size)
        {
            if (sceneCamera == null)
            {
                sceneCamera = Camera.main;
                if (sceneCamera == null)
                {
                    GameObject cameraObject = new GameObject("Main Camera");
                    cameraObject.tag = "MainCamera";
                    sceneCamera = cameraObject.AddComponent<Camera>();
                }
            }

            sceneCamera.orthographic = true;
            sceneCamera.orthographicSize = size * 0.74f;
            Vector3 focus = new Vector3(position.x, 0f, position.z - 1.4f);
            sceneCamera.transform.position = focus + new Vector3(0f, size * 1.55f, -size * 0.28f);
            sceneCamera.transform.LookAt(focus);
            sceneCamera.useOcclusionCulling = false;
            sceneCamera.nearClipPlane = 0.1f;
            sceneCamera.farClipPlane = 220f;
        }

        private static void EnsureLight()
        {
            if (FindAnyObjectByType<Light>() != null)
            {
                return;
            }

            GameObject lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.45f);
        }
    }

    public static class ParkingEnvironmentBuilder
    {
        private const float GroundY = -0.05f;
        private const float LineY = 0.065f;
        private const float LineHeight = 0.045f;
        private static Material shadowMaterial;

        public static ParkingProfile BuildPerpendicularMap(
            Transform root,
            Material asphalt,
            Material lines,
            Material parkedCar,
            Material concrete,
            Material target,
            out Pose startPose)
        {
            VehicleDimensions dimensions = VehicleDimensions.Default();
            startPose = new Pose(new Vector3(-17f, 0.05f, 0f), Quaternion.LookRotation(Vector3.right, Vector3.up));

            Cube(root, "Main aisle", new Vector3(1f, GroundY, 0f), Quaternion.identity, new Vector3(42f, 0.1f, 8.8f), asphalt, false);
            Cube(root, "North concrete", new Vector3(1f, GroundY - 0.01f, 8.7f), Quaternion.identity, new Vector3(42f, 0.08f, 4f), concrete, false);
            Cube(root, "South concrete", new Vector3(1f, GroundY - 0.01f, -8.7f), Quaternion.identity, new Vector3(42f, 0.08f, 4f), concrete, false);

            DrawPerpendicularSlots(root, lines, -6.6f, 8, true);
            DrawPerpendicularSlots(root, lines, 6.6f, 8, false);

            float[] slotX = { -11.5f, -8.4f, -5.3f, -2.2f, 0.9f, 4.0f, 7.1f, 10.2f };
            Vector3 validTargetPosition = new Vector3(slotX[3], 0.05f, -6.0f);
            for (int i = 0; i < slotX.Length; i++)
            {
                if (i == 3 || i == 4)
                {
                    if (i == 3)
                    {
                        Marker(root, "Valid gap marker", validTargetPosition + Vector3.up * 0.01f, target);
                    }
                    continue;
                }

                if (i == 2)
                {
                    Cube(root, "False gap cart", new Vector3(slotX[i] + 0.1f, 0.3f, -5.15f), Quaternion.identity, new Vector3(1.25f, 0.7f, 1.1f), parkedCar, true);
                    continue;
                }

                CreateParkedCar(root, "Parked car S" + i, new Vector3(slotX[i], 0.35f, -6.6f), Quaternion.LookRotation(Vector3.back, Vector3.up), parkedCar, dimensions);
            }

            for (int i = 0; i < slotX.Length; i += 2)
            {
                CreateParkedCar(root, "Parked car N" + i, new Vector3(slotX[i] + 0.4f, 0.35f, 6.6f), Quaternion.LookRotation(Vector3.forward, Vector3.up), parkedCar, dimensions);
            }

            return new ParkingProfile
            {
                Scenario = ParkingScenario.Perpendicular,
                Style = ParkingManeuverStyle.Perpendicular,
                SearchSide = ParkingSide.Right,
                Dimensions = dimensions,
                LaneForward = Vector3.right,
                RequiredGapLength = dimensions.Width + 0.55f,
                RequiredDepth = 3.2f,
                ScanWindowLength = dimensions.Width + 0.15f,
                SideScanRange = 6.2f,
                TargetLateralOffset = 6.0f,
                SearchSpeed = 1.7f,
                ParkingSpeed = 0.9f,
                EmergencyDistance = 0.2f,
                MaxSearchDistance = 34f,
                UseScriptedTarget = true,
                ScriptedTargetPosition = validTargetPosition,
                ScriptedTargetForward = Vector3.back,
                ScriptedTriggerAlongLane = -1.4f,
                ScriptedMeasuredLength = 6.2f,
                ScriptedMeasuredDepth = 5.2f
            };
        }

        public static ParkingProfile BuildParallelMap(
            Transform root,
            Material asphalt,
            Material lines,
            Material parkedCar,
            Material concrete,
            out Pose startPose)
        {
            VehicleDimensions dimensions = VehicleDimensions.Default();
            startPose = new Pose(new Vector3(-20f, 0.05f, 0f), Quaternion.LookRotation(Vector3.right, Vector3.up));

            Cube(root, "One way street", new Vector3(1f, GroundY, 0f), Quaternion.identity, new Vector3(48f, 0.1f, 4.4f), asphalt, false);
            Cube(root, "Left wall", new Vector3(1f, 0.8f, 2.8f), Quaternion.identity, new Vector3(48f, 1.6f, 0.35f), concrete, true);
            Cube(root, "Right curb", new Vector3(1f, 0.12f, -4.45f), Quaternion.identity, new Vector3(48f, 0.25f, 0.25f), concrete, true);

            for (int i = 0; i < 22; i++)
            {
                Cube(root, "Center dash " + i, new Vector3(-21f + i * 2f, LineY, 1.55f), Quaternion.identity, new Vector3(1f, LineHeight, 0.07f), lines, false);
            }

            Vector3 parallelTargetPosition = new Vector3(9.6f, 0.05f, -3.05f);
            CreateParkedCar(root, "Parallel car 0", new Vector3(-13.8f, 0.35f, -3.05f), Quaternion.LookRotation(Vector3.right, Vector3.up), parkedCar, dimensions);
            CreateParkedCar(root, "Parallel car 1", new Vector3(-7.2f, 0.35f, -3.05f), Quaternion.LookRotation(Vector3.right, Vector3.up), parkedCar, dimensions);
            CreateParkedCar(root, "Rear bound car", new Vector3(1.4f, 0.35f, -3.05f), Quaternion.LookRotation(Vector3.right, Vector3.up), parkedCar, dimensions);
            Marker(root, "Parallel target marker", parallelTargetPosition + Vector3.up * 0.01f, NewRuntimeMaterial("ParallelTarget", new Color(0.12f, 0.48f, 0.95f)));
            CreateParkedCar(root, "Front bound car", new Vector3(18.0f, 0.35f, -3.05f), Quaternion.LookRotation(Vector3.right, Vector3.up), parkedCar, dimensions);
            Cube(root, "Tow hook", new Vector3(15.55f, 0.38f, -3.05f), Quaternion.identity, new Vector3(0.28f, 0.28f, 0.7f), parkedCar, true);
            CreateParkedCar(root, "After target car", new Vector3(24.0f, 0.35f, -3.05f), Quaternion.LookRotation(Vector3.right, Vector3.up), parkedCar, dimensions);

            return new ParkingProfile
            {
                Scenario = ParkingScenario.Parallel,
                Style = ParkingManeuverStyle.Parallel,
                SearchSide = ParkingSide.Right,
                Dimensions = dimensions,
                LaneForward = Vector3.right,
                RequiredGapLength = dimensions.Length * 1.45f,
                RequiredDepth = 2.45f,
                ScanWindowLength = dimensions.Length * 0.9f,
                SideScanRange = 5.2f,
                TargetLateralOffset = 3.05f,
                SearchSpeed = 1.6f,
                ParkingSpeed = 0.75f,
                EmergencyDistance = 0.2f,
                MaxSearchDistance = 44f,
                UseScriptedTarget = true,
                ScriptedTargetPosition = parallelTargetPosition,
                ScriptedTargetForward = Vector3.right,
                ScriptedTriggerAlongLane = 6.0f,
                ScriptedMeasuredLength = 12.1f,
                ScriptedMeasuredDepth = 3.0f
            };
        }

        public static ParkingProfile BuildAngledDynamicMap(
            Transform root,
            Material asphalt,
            Material lines,
            Material parkedCar,
            Material concrete,
            Material target,
            out Pose startPose)
        {
            VehicleDimensions dimensions = VehicleDimensions.Default();
            startPose = new Pose(new Vector3(-18f, 0.05f, 0f), Quaternion.LookRotation(Vector3.right, Vector3.up));

            Cube(root, "Underground aisle", new Vector3(1f, GroundY, 0f), Quaternion.identity, new Vector3(44f, 0.1f, 6.8f), asphalt, false);
            Cube(root, "North deck", new Vector3(1f, GroundY - 0.01f, 6.1f), Quaternion.identity, new Vector3(44f, 0.08f, 5f), concrete, false);
            Cube(root, "South deck", new Vector3(1f, GroundY - 0.01f, -6.9f), Quaternion.identity, new Vector3(44f, 0.08f, 6f), concrete, false);

            Vector3 angledForward = Vector3.RotateTowards(Vector3.right, Vector3.back, 60f * Mathf.Deg2Rad, 0f).normalized;
            const int angledTargetSlot = 4;
            Vector3 angledTargetPosition = new Vector3(-12f + angledTargetSlot * 4.5f, 0.05f, -5.45f);

            for (int i = 0; i < 7; i++)
            {
                float x = -12f + i * 4.5f;
                Quaternion slotRotation = Quaternion.LookRotation(angledForward, Vector3.up);
                DrawSlotLine(root, lines, new Vector3(x - 1f, 0.01f, -5.45f), slotRotation, 5.2f);
                DrawSlotLine(root, lines, new Vector3(x + 1.6f, 0.01f, -5.45f), slotRotation, 5.2f);

                if (i == angledTargetSlot)
                {
                    Marker(root, "Angled target marker", angledTargetPosition + Vector3.up * 0.01f, target);
                    continue;
                }

                CreateParkedCar(root, "Angled parked car " + i, new Vector3(x, 0.35f, -5.45f), slotRotation, parkedCar, dimensions);
            }

            for (int i = 0; i < 3; i++)
            {
                Cube(root, "Concrete pillar " + i, new Vector3(-6f + i * 8f, 1.1f, 4.15f), Quaternion.identity, new Vector3(0.9f, 2.2f, 0.9f), concrete, true);
            }

            GameObject mover = CreateParkedCar(root, "Dynamic blocker", new Vector3(12.5f, 0.35f, 4.4f), Quaternion.LookRotation(Vector3.back, Vector3.up), parkedCar, dimensions);
            MovingObstacle movingObstacle = mover.AddComponent<MovingObstacle>();
            movingObstacle.Configure(new Vector3(12.5f, 0.35f, 4.4f), new Vector3(12.5f, 0.35f, -0.35f), 0.65f, 10.0f);

            return new ParkingProfile
            {
                Scenario = ParkingScenario.AngledDynamic,
                Style = ParkingManeuverStyle.Angled,
                SearchSide = ParkingSide.Right,
                Dimensions = dimensions,
                LaneForward = Vector3.right,
                RequiredGapLength = dimensions.Width + 0.85f,
                RequiredDepth = 2.75f,
                ScanWindowLength = dimensions.Width + 0.2f,
                SideScanRange = 5.5f,
                TargetLateralOffset = 5.45f,
                SearchSpeed = 1.5f,
                ParkingSpeed = 0.75f,
                EmergencyDistance = 0.2f,
                MaxSearchDistance = 40f,
                UseScriptedTarget = true,
                ScriptedTargetPosition = angledTargetPosition,
                ScriptedTargetForward = angledForward,
                ScriptedTriggerAlongLane = angledTargetPosition.x - 1.8f,
                ScriptedMeasuredLength = 3.4f,
                ScriptedMeasuredDepth = 4.2f
            };
        }

        public static GameObject CreateAgentCar(Transform root, Vector3 position, Quaternion rotation, Material material, VehicleDimensions dimensions)
        {
            GameObject car = new GameObject("Autonomous Parking Agent");
            car.transform.SetParent(root, false);
            car.transform.SetPositionAndRotation(position, rotation);
            car.layer = 2;

            VehicleController controller = car.AddComponent<VehicleController>();
            controller.Configure(dimensions);
            car.AddComponent<ParkingSensorRig>();
            car.AddComponent<ParkingAgent>();

            Cube(car.transform, "Soft shadow", new Vector3(0.16f, -0.035f, -0.16f), Quaternion.identity, new Vector3(dimensions.Width * 1.05f, 0.018f, dimensions.Length * 1.04f), GetShadowMaterial(), false);
            BuildCarVisual(car.transform, dimensions, material, true);

            return car;
        }

        private static void DrawPerpendicularSlots(Transform root, Material lines, float z, int count, bool south)
        {
            float startX = -13.05f;
            for (int i = 0; i <= count; i++)
            {
                Cube(root, "Slot separator " + z + " " + i, new Vector3(startX + i * 3.1f, LineY, z), Quaternion.identity, new Vector3(0.11f, LineHeight, 5.4f), lines, false);
            }

            float endZ = south ? -3.3f : 3.3f;
            Cube(root, "Slot front line " + z, new Vector3(-0.65f, LineY, endZ), Quaternion.identity, new Vector3(count * 3.1f, LineHeight, 0.11f), lines, false);
        }

        private static void DrawSlotLine(Transform root, Material lines, Vector3 position, Quaternion rotation, float length)
        {
            Vector3 raisedPosition = new Vector3(position.x, LineY, position.z);
            Cube(root, "Angled slot line", raisedPosition, rotation, new Vector3(0.12f, LineHeight, length), lines, false);
        }

        private static GameObject CreateParkedCar(Transform root, string objectName, Vector3 position, Quaternion rotation, Material material, VehicleDimensions dimensions)
        {
            Cube(root, objectName + " soft shadow", new Vector3(position.x + 0.18f, 0.015f, position.z - 0.18f), rotation, new Vector3(dimensions.Width * 0.9f, 0.018f, dimensions.Length * 0.95f), GetShadowMaterial(), false);
            GameObject car = Cube(root, objectName, position, rotation, new Vector3(dimensions.Width * 0.82f, 0.7f, dimensions.Length * 0.88f), material, true);
            Cube(car.transform, "roof", new Vector3(0f, 0.48f, 0f), Quaternion.identity, new Vector3(dimensions.Width * 0.62f, 0.42f, dimensions.Length * 0.38f), material, false);
            return car;
        }

        private static Material GetShadowMaterial()
        {
            if (shadowMaterial == null)
            {
                shadowMaterial = NewRuntimeMaterial("SoftShadow", new Color(0.025f, 0.028f, 0.028f));
            }

            return shadowMaterial;
        }

        private static void BuildCarVisual(Transform root, VehicleDimensions dimensions, Material bodyMaterial, bool brightDetails)
        {
            Material glass = NewRuntimeMaterial("AgentGlass", new Color(0.18f, 0.34f, 0.46f));
            Material tire = NewRuntimeMaterial("AgentTire", Color.black);
            Material headLight = NewRuntimeMaterial("AgentHeadlight", new Color(1f, 0.93f, 0.62f));
            Material tailLight = NewRuntimeMaterial("AgentTaillight", new Color(0.82f, 0.03f, 0.02f));

            Cube(root, "Car body", new Vector3(0f, 0.42f, 0f), Quaternion.identity, new Vector3(dimensions.Width, 0.72f, dimensions.Length), bodyMaterial, false);
            Cube(root, "Car cabin", new Vector3(0f, 0.9f, -0.15f), Quaternion.identity, new Vector3(dimensions.Width * 0.72f, 0.42f, dimensions.Length * 0.42f), bodyMaterial, false);
            Cube(root, "Windshield", new Vector3(0f, 1.13f, dimensions.Length * 0.1f), Quaternion.identity, new Vector3(dimensions.Width * 0.54f, 0.08f, dimensions.Length * 0.16f), glass, false);
            Cube(root, "Rear window", new Vector3(0f, 1.13f, -dimensions.Length * 0.31f), Quaternion.identity, new Vector3(dimensions.Width * 0.52f, 0.08f, dimensions.Length * 0.12f), glass, false);
            Cube(root, "Headlights", new Vector3(0f, 0.58f, dimensions.Length * 0.51f), Quaternion.identity, new Vector3(dimensions.Width * 0.58f, 0.14f, 0.08f), headLight, false);
            Cube(root, "Tail lights", new Vector3(0f, 0.58f, -dimensions.Length * 0.51f), Quaternion.identity, new Vector3(dimensions.Width * 0.58f, 0.14f, 0.08f), tailLight, false);
            Cube(root, "Front sensor strip", new Vector3(0f, 0.76f, dimensions.Length * 0.47f), Quaternion.identity, new Vector3(dimensions.Width * 0.5f, 0.08f, 0.06f), glass, false);
            Cube(root, "Rear sensor strip", new Vector3(0f, 0.76f, -dimensions.Length * 0.47f), Quaternion.identity, new Vector3(dimensions.Width * 0.5f, 0.08f, 0.06f), glass, false);

            float wheelZ = dimensions.WheelBase * 0.5f;
            float wheelX = dimensions.TrackWidth * 0.5f;
            Wheel(root, "Wheel FL", new Vector3(-wheelX, 0.28f, wheelZ), tire);
            Wheel(root, "Wheel FR", new Vector3(wheelX, 0.28f, wheelZ), tire);
            Wheel(root, "Wheel RL", new Vector3(-wheelX, 0.28f, -wheelZ), tire);
            Wheel(root, "Wheel RR", new Vector3(wheelX, 0.28f, -wheelZ), tire);
        }

        private static GameObject Cube(Transform root, string objectName, Vector3 position, Quaternion rotation, Vector3 scale, Material material, bool colliderEnabled)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = objectName;
            cube.transform.SetParent(root, false);
            cube.transform.localPosition = position;
            cube.transform.localRotation = rotation;
            cube.transform.localScale = scale;

            Renderer renderer = cube.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
            }

            Collider collider = cube.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = colliderEnabled;
            }

            return cube;
        }

        private static void Marker(Transform root, string objectName, Vector3 position, Material material)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = objectName;
            marker.transform.SetParent(root, false);
            marker.transform.localPosition = position;
            marker.transform.localScale = new Vector3(0.75f, 0.08f, 0.75f);
            marker.GetComponent<Renderer>().sharedMaterial = material;
            Collider collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            Cube(root, objectName + " ring north", position + new Vector3(0f, 0.015f, 0.56f), Quaternion.identity, new Vector3(1.25f, 0.025f, 0.08f), material, false);
            Cube(root, objectName + " ring south", position + new Vector3(0f, 0.015f, -0.56f), Quaternion.identity, new Vector3(1.25f, 0.025f, 0.08f), material, false);
            Cube(root, objectName + " ring east", position + new Vector3(0.56f, 0.015f, 0f), Quaternion.identity, new Vector3(0.08f, 0.025f, 1.25f), material, false);
            Cube(root, objectName + " ring west", position + new Vector3(-0.56f, 0.015f, 0f), Quaternion.identity, new Vector3(0.08f, 0.025f, 1.25f), material, false);
        }

        private static void Wheel(Transform root, string objectName, Vector3 localPosition, Material material)
        {
            GameObject wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.name = objectName;
            wheel.transform.SetParent(root, false);
            wheel.transform.localPosition = localPosition;
            wheel.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            wheel.transform.localScale = new Vector3(0.34f, 0.15f, 0.34f);
            wheel.GetComponent<Renderer>().sharedMaterial = material;
            Collider collider = wheel.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }
        }

        private static Material NewRuntimeMaterial(string materialName, Color color)
        {
            Material material = CreateSafeMaterial(materialName);
            material.color = color;
            return material;
        }

        private static Material CreateSafeMaterial(string materialName)
        {
            Shader shader =
                Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Standard");

            if (shader != null)
            {
                return new Material(shader) { name = materialName };
            }

            Material builtInMaterial =
                Resources.GetBuiltinResource<Material>("Default-Material.mat") ??
                Resources.GetBuiltinResource<Material>("Default-Diffuse.mat");

            if (builtInMaterial != null)
            {
                Material copy = UnityEngine.Object.Instantiate(builtInMaterial);
                copy.name = materialName;
                return copy;
            }

            return new Material(Shader.Find("Hidden/InternalErrorShader")) { name = materialName };
        }
    }

    public sealed class VehicleController : MonoBehaviour
    {
        [SerializeField] private float maxSteerAngle = 35f;
        [SerializeField] private float steeringRate = 90f;
        [SerializeField] private float acceleration = 3.5f;
        [SerializeField] private float brakeAcceleration = 7.5f;

        private VehicleDimensions dimensions;
        private Rigidbody body;
        private float targetSpeed;
        private float targetSteer;
        private float currentSpeed;
        private float currentSteer;
        private bool assistedPose;

        public float CurrentSpeed => currentSpeed;
        public float TargetSpeed => targetSpeed;
        public float CurrentSteerAngle => currentSteer;
        public bool HadCollision { get; private set; }

        public void Configure(VehicleDimensions vehicleDimensions)
        {
            dimensions = vehicleDimensions;
            EnsurePhysics();
        }

        public void SetCommand(float speedMetersPerSecond, float steerDegrees)
        {
            targetSpeed = speedMetersPerSecond;
            targetSteer = Mathf.Clamp(steerDegrees, -maxSteerAngle, maxSteerAngle);
        }

        public void Brake()
        {
            targetSpeed = 0f;
            targetSteer = Mathf.MoveTowards(targetSteer, 0f, 180f * Time.fixedDeltaTime);
        }

        public void SetAssistedPose(Vector3 position, Quaternion rotation, float speedMetersPerSecond, float steerDegrees)
        {
            EnsurePhysics();
            assistedPose = true;
            currentSpeed = speedMetersPerSecond;
            targetSpeed = 0f;
            currentSteer = Mathf.Clamp(steerDegrees, -maxSteerAngle, maxSteerAngle);
            targetSteer = currentSteer;
            body.MovePosition(position);
            body.MoveRotation(rotation);
        }

        public Vector2 GetAckermannWheelAngles(float steerDegrees)
        {
            if (Mathf.Abs(steerDegrees) < 0.01f)
            {
                return Vector2.zero;
            }

            float steerRadians = steerDegrees * Mathf.Deg2Rad;
            float tan = Mathf.Tan(Mathf.Abs(steerRadians));
            float radiusDenominator = Mathf.Max(0.001f, dimensions.WheelBase / tan);
            float inner = Mathf.Atan(dimensions.WheelBase / Mathf.Max(0.001f, radiusDenominator - dimensions.TrackWidth * 0.5f)) * Mathf.Rad2Deg;
            float outer = Mathf.Atan(dimensions.WheelBase / (radiusDenominator + dimensions.TrackWidth * 0.5f)) * Mathf.Rad2Deg;

            if (steerDegrees > 0f)
            {
                return new Vector2(inner, outer);
            }

            return new Vector2(-outer, -inner);
        }

        private void Awake()
        {
            if (dimensions.Length <= 0f)
            {
                dimensions = VehicleDimensions.Default();
            }

            EnsurePhysics();
        }

        private void FixedUpdate()
        {
            if (assistedPose)
            {
                assistedPose = false;
                return;
            }

            float deltaTime = Time.fixedDeltaTime;
            float rate = Mathf.Abs(targetSpeed) < Mathf.Abs(currentSpeed) ? brakeAcceleration : acceleration;
            currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, rate * deltaTime);
            currentSteer = Mathf.MoveTowards(currentSteer, targetSteer, steeringRate * deltaTime);

            float steerRadians = currentSteer * Mathf.Deg2Rad;
            float yawRate = Mathf.Abs(currentSteer) < 0.05f ? 0f : (currentSpeed / dimensions.WheelBase) * Mathf.Tan(steerRadians);
            Quaternion newRotation = body.rotation * Quaternion.Euler(0f, yawRate * Mathf.Rad2Deg * deltaTime, 0f);
            Vector3 newPosition = body.position + body.rotation * Vector3.forward * (currentSpeed * deltaTime);

            body.MoveRotation(newRotation);
            body.MovePosition(newPosition);
        }

        private void OnCollisionEnter(Collision collision)
        {
            HadCollision = true;
        }

        private void EnsurePhysics()
        {
            body = GetComponent<Rigidbody>();
            if (body == null)
            {
                body = gameObject.AddComponent<Rigidbody>();
            }

            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            BoxCollider collider = GetComponent<BoxCollider>();
            if (collider == null)
            {
                collider = gameObject.AddComponent<BoxCollider>();
            }

            collider.center = new Vector3(0f, 0.45f, 0f);
            collider.size = new Vector3(dimensions.Width, 0.9f, dimensions.Length);
        }
    }

    public sealed class ParkingSensorRig : MonoBehaviour
    {
        private ParkingProfile profile;
        private VehicleController controller;
        private int sensorMask;

        public SensorSnapshot Snapshot { get; private set; }

        public void Configure(ParkingProfile parkingProfile, VehicleController vehicleController)
        {
            profile = parkingProfile;
            controller = vehicleController;
            sensorMask = Physics.DefaultRaycastLayers & ~(1 << 2);
        }

        public void Sample()
        {
            if (profile == null)
            {
                return;
            }

            Transform vehicle = transform;
            VehicleDimensions dimensions = profile.Dimensions;
            Vector3 sideDirection = GetSearchSideDirection();
            Vector3 sideOrigin = vehicle.position + Vector3.up * 0.55f + sideDirection * (dimensions.Width * 0.5f + 0.08f);
            Vector3 halfExtents = new Vector3(0.16f, 0.38f, Mathf.Max(0.5f, profile.ScanWindowLength * 0.5f));

            bool sideBlocked = Physics.BoxCast(
                sideOrigin,
                halfExtents,
                sideDirection,
                out RaycastHit sideHit,
                vehicle.rotation,
                profile.SideScanRange,
                sensorMask,
                QueryTriggerInteraction.Ignore);

            float sideDistance = sideBlocked ? sideHit.distance : profile.SideScanRange;
            bool sideClear = !sideBlocked || sideDistance >= profile.RequiredDepth;

            float forwardDistance = CastEmergency(vehicle.forward, dimensions.Length * 0.5f, out RaycastHit forwardHit);
            float rearDistance = CastEmergency(-vehicle.forward, dimensions.Length * 0.5f, out RaycastHit rearHit);
            bool movingForward = controller == null || controller.TargetSpeed >= -0.05f;
            bool emergencyBlocked = movingForward
                ? forwardDistance <= profile.EmergencyDistance
                : rearDistance <= profile.EmergencyDistance;

            Snapshot = new SensorSnapshot
            {
                SideClear = sideClear,
                EmergencyBlocked = emergencyBlocked,
                SideDistance = sideDistance,
                ForwardDistance = forwardDistance,
                RearDistance = rearDistance,
                LastSideHitPoint = sideBlocked ? sideHit.point : sideOrigin + sideDirection * profile.SideScanRange,
                LastEmergencyHitPoint = movingForward && forwardHit.collider != null ? forwardHit.point : rearHit.point
            };

            Debug.DrawRay(sideOrigin, sideDirection * sideDistance, sideClear ? Color.green : Color.red);
            Debug.DrawRay(vehicle.position + Vector3.up * 0.5f + vehicle.forward * dimensions.Length * 0.5f, vehicle.forward * forwardDistance, emergencyBlocked && movingForward ? Color.red : Color.cyan);
            Debug.DrawRay(vehicle.position + Vector3.up * 0.5f - vehicle.forward * dimensions.Length * 0.5f, -vehicle.forward * rearDistance, emergencyBlocked && !movingForward ? Color.red : Color.cyan);
        }

        public Vector3 GetSearchSideDirection()
        {
            return profile.SearchSide == ParkingSide.Right ? transform.right : -transform.right;
        }

        private float CastEmergency(Vector3 direction, float bodyOffset, out RaycastHit hit)
        {
            Vector3 origin = transform.position + Vector3.up * 0.55f + direction * (bodyOffset + 0.08f);
            if (Physics.Raycast(origin, direction, out hit, profile.EmergencyDistance + 1.5f, sensorMask, QueryTriggerInteraction.Ignore))
            {
                return hit.distance;
            }

            hit = default;
            return profile.EmergencyDistance + 1.5f;
        }
    }

    public interface ICarState
    {
        string Name { get; }
        bool AllowsEmergencyInterrupt { get; }
        void Enter(ParkingAgent context);
        void Tick(ParkingAgent context, float deltaTime);
        void FixedTick(ParkingAgent context, float fixedDeltaTime);
        void Exit(ParkingAgent context);
    }

    public sealed class ParkingStateMachine
    {
        private readonly Stack<ICarState> stack = new Stack<ICarState>();
        private readonly ParkingAgent context;

        public ParkingStateMachine(ParkingAgent context)
        {
            this.context = context;
        }

        public string CurrentStateName => stack.Count > 0 ? stack.Peek().Name : "None";
        public ICarState CurrentState => stack.Count > 0 ? stack.Peek() : null;

        public void Set(ICarState state)
        {
            while (stack.Count > 0)
            {
                stack.Pop().Exit(context);
            }

            Push(state);
        }

        public void Push(ICarState state)
        {
            stack.Push(state);
            state.Enter(context);
        }

        public void Pop()
        {
            if (stack.Count == 0)
            {
                return;
            }

            ICarState old = stack.Pop();
            old.Exit(context);
            if (stack.Count == 0)
            {
                Push(new SearchSpaceState());
            }
        }

        public void Tick(float deltaTime)
        {
            CurrentState?.Tick(context, deltaTime);
        }

        public void FixedTick(float fixedDeltaTime)
        {
            CurrentState?.FixedTick(context, fixedDeltaTime);
        }
    }

    public sealed class ParkingAgent : MonoBehaviour
    {
        private ParkingStateMachine machine;
        private ParkingSpaceScanner scanner;
        private ParkingProfile profile;

        public ParkingProfile Profile => profile;
        public VehicleController Controller { get; private set; }
        public ParkingSensorRig Sensors { get; private set; }
        public ParkingCandidate ActiveCandidate { get; private set; }
        public bool HasCandidate { get; private set; }
        public Vector3 LaneForward => profile != null ? profile.LaneForward.normalized : transform.forward;
        public string CurrentStateName => machine != null ? machine.CurrentStateName : "Starting";

        public void Configure(ParkingProfile parkingProfile)
        {
            profile = parkingProfile;
            Controller = GetComponent<VehicleController>();
            Sensors = GetComponent<ParkingSensorRig>();
            Controller.Configure(profile.Dimensions);
            Sensors.Configure(profile, Controller);
            scanner = new ParkingSpaceScanner(profile);
            machine = new ParkingStateMachine(this);
            machine.Set(new SearchSpaceState());
        }

        public void SetCandidate(ParkingCandidate candidate)
        {
            ActiveCandidate = candidate;
            HasCandidate = true;
        }

        public void ClearCandidate()
        {
            HasCandidate = false;
        }

        public void ChangeState(ICarState state)
        {
            machine.Set(state);
        }

        public void PopCurrentState()
        {
            machine?.Pop();
        }

        public bool TryScanForCandidate(float deltaTime, out ParkingCandidate candidate)
        {
            return scanner.Tick(this, deltaTime, out candidate);
        }

        public void ResetScannerAfterRejectedCandidate()
        {
            scanner.ResetAfterRejectedCandidate(transform.position);
            ClearCandidate();
        }

        private void Start()
        {
            if (profile == null)
            {
                Configure(new ParkingProfile
                {
                    Scenario = ParkingScenario.Perpendicular,
                    Style = ParkingManeuverStyle.Perpendicular,
                    SearchSide = ParkingSide.Right,
                    Dimensions = VehicleDimensions.Default(),
                    LaneForward = transform.forward,
                    RequiredGapLength = 2.6f,
                    RequiredDepth = 3.0f,
                    ScanWindowLength = 2.1f,
                    SideScanRange = 6f,
                    TargetLateralOffset = 4.5f,
                    SearchSpeed = 2f,
                    ParkingSpeed = 1f,
                    EmergencyDistance = 2f,
                    MaxSearchDistance = 30f
                });
            }
        }

        private void Update()
        {
            machine?.Tick(Time.deltaTime);
        }

        private void FixedUpdate()
        {
            if (machine == null || Sensors == null)
            {
                return;
            }

            Sensors.Sample();
            ICarState current = machine.CurrentState;
            if (current != null && current.AllowsEmergencyInterrupt && Sensors.Snapshot.EmergencyBlocked)
            {
                machine.Push(new EmergencyBrakeState());
            }

            machine.FixedTick(Time.fixedDeltaTime);
        }
    }

    public sealed class ParkingSpaceScanner
    {
        private readonly ParkingProfile profile;
        private bool hasSeenObstacle;
        private bool insideGap;
        private Vector3 gapStartPosition;
        private float gapStartAlongLane;
        private float lastDepth;
        private float distanceTravelled;
        private Vector3 lastPosition;

        public ParkingSpaceScanner(ParkingProfile profile)
        {
            this.profile = profile;
        }

        public bool Tick(ParkingAgent context, float deltaTime, out ParkingCandidate candidate)
        {
            candidate = default;

            Vector3 position = context.transform.position;
            if (lastPosition == Vector3.zero)
            {
                lastPosition = position;
            }

            distanceTravelled += Vector3.Distance(position, lastPosition);
            lastPosition = position;

            SensorSnapshot sensors = context.Sensors.Snapshot;
            Vector3 laneForward = context.LaneForward;
            float alongLane = Vector3.Dot(position, laneForward);

            if (profile.UseScriptedTarget)
            {
                if (alongLane >= profile.ScriptedTriggerAlongLane)
                {
                    candidate = new ParkingCandidate
                    {
                        TargetPosition = profile.ScriptedTargetPosition,
                        TargetRotation = Quaternion.LookRotation(profile.ScriptedTargetForward.normalized, Vector3.up),
                        Style = profile.Style,
                        MeasuredLength = profile.ScriptedMeasuredLength,
                        MeasuredDepth = profile.ScriptedMeasuredDepth
                    };

                    return true;
                }

                return false;
            }

            if (!sensors.SideClear)
            {
                hasSeenObstacle = true;
                if (insideGap)
                {
                    insideGap = false;
                    return EvaluateGap(context, position, alongLane, out candidate);
                }

                return false;
            }

            if (!hasSeenObstacle)
            {
                return false;
            }

            lastDepth = Mathf.Max(lastDepth, sensors.SideDistance);
            if (!insideGap)
            {
                insideGap = true;
                gapStartPosition = position;
                gapStartAlongLane = alongLane;
                lastDepth = sensors.SideDistance;
            }

            if (distanceTravelled > profile.MaxSearchDistance)
            {
                insideGap = false;
                return EvaluateGap(context, position, alongLane, out candidate);
            }

            return false;
        }

        public void ResetAfterRejectedCandidate(Vector3 currentPosition)
        {
            insideGap = false;
            hasSeenObstacle = false;
            distanceTravelled = 0f;
            lastPosition = currentPosition;
        }

        private bool EvaluateGap(ParkingAgent context, Vector3 endPosition, float endAlongLane, out ParkingCandidate candidate)
        {
            candidate = default;
            float length = Mathf.Abs(endAlongLane - gapStartAlongLane);
            if (length < profile.RequiredGapLength || lastDepth < profile.RequiredDepth)
            {
                return false;
            }

            Vector3 laneForward = context.LaneForward;
            Vector3 sideDirection = context.Sensors.GetSearchSideDirection();
            Vector3 midLanePosition = (gapStartPosition + endPosition) * 0.5f;
            Vector3 targetPosition = midLanePosition + sideDirection.normalized * profile.TargetLateralOffset;
            targetPosition.y = context.transform.position.y;

            Vector3 targetForward = laneForward;
            if (profile.Style == ParkingManeuverStyle.Perpendicular)
            {
                targetForward = sideDirection;
            }
            else if (profile.Style == ParkingManeuverStyle.Angled)
            {
                targetForward = Vector3.RotateTowards(laneForward, sideDirection, 60f * Mathf.Deg2Rad, 0f);
            }

            candidate = new ParkingCandidate
            {
                TargetPosition = targetPosition,
                TargetRotation = Quaternion.LookRotation(targetForward.normalized, Vector3.up),
                Style = profile.Style,
                MeasuredLength = length,
                MeasuredDepth = lastDepth
            };

            return true;
        }
    }

    public sealed class SearchSpaceState : ICarState
    {
        public string Name => "SearchSpace";
        public bool AllowsEmergencyInterrupt => true;

        public void Enter(ParkingAgent context)
        {
            context.ClearCandidate();
        }

        public void Tick(ParkingAgent context, float deltaTime)
        {
            if (context.TryScanForCandidate(deltaTime, out ParkingCandidate candidate))
            {
                context.SetCandidate(candidate);
                context.ChangeState(new ApproachAndParkState(candidate));
            }
        }

        public void FixedTick(ParkingAgent context, float fixedDeltaTime)
        {
            context.Controller.SetCommand(context.Profile.SearchSpeed, 0f);
        }

        public void Exit(ParkingAgent context)
        {
        }
    }

    public sealed class ApproachAndParkState : ICarState
    {
        private readonly ParkingCandidate candidate;
        private PathFollower follower;

        public ApproachAndParkState(ParkingCandidate candidate)
        {
            this.candidate = candidate;
        }

        public string Name => "ApproachAndPark";
        public bool AllowsEmergencyInterrupt => true;

        public void Enter(ParkingAgent context)
        {
            List<ParkingWaypoint> waypoints = ParkingPathPlanner.BuildPath(context, candidate);
            follower = new PathFollower(waypoints);
        }

        public void Tick(ParkingAgent context, float deltaTime)
        {
        }

        public void FixedTick(ParkingAgent context, float fixedDeltaTime)
        {
            if (follower == null)
            {
                context.ChangeState(new SearchSpaceState());
                return;
            }

            if (follower.FixedTick(context, fixedDeltaTime))
            {
                context.ChangeState(new CenteringState(candidate));
            }
        }

        public void Exit(ParkingAgent context)
        {
        }
    }

    public sealed class CenteringState : ICarState
    {
        private readonly ParkingCandidate candidate;
        private float stableTime;

        public CenteringState(ParkingCandidate candidate)
        {
            this.candidate = candidate;
        }

        public string Name => "Centering";
        public bool AllowsEmergencyInterrupt => true;

        public void Enter(ParkingAgent context)
        {
            stableTime = 0f;
        }

        public void Tick(ParkingAgent context, float deltaTime)
        {
        }

        public void FixedTick(ParkingAgent context, float fixedDeltaTime)
        {
            Transform vehicle = context.transform;
            Vector3 localTarget = vehicle.InverseTransformPoint(candidate.TargetPosition);
            float distance = new Vector2(localTarget.x, localTarget.z).magnitude;
            float headingError = Vector3.SignedAngle(vehicle.forward, candidate.TargetRotation * Vector3.forward, Vector3.up);

            float finalDistanceTolerance = context.Profile.UseScriptedTarget ? 0.38f : 0.18f;
            float finalHeadingTolerance = context.Profile.UseScriptedTarget ? 8.0f : 3.5f;
            if (distance < finalDistanceTolerance && Mathf.Abs(headingError) < finalHeadingTolerance)
            {
                stableTime += fixedDeltaTime;
                context.Controller.Brake();
                if (stableTime > 0.7f)
                {
                    context.ChangeState(new CompletedState());
                }

                return;
            }

            float direction = Mathf.Abs(localTarget.z) > 0.15f ? Mathf.Sign(localTarget.z) : 1f;
            float steer = Mathf.Clamp(localTarget.x * 18f + headingError * 0.65f, -35f, 35f);
            float speed = Mathf.Clamp(distance * 0.65f, 0.25f, 0.75f) * direction;
            context.Controller.SetCommand(speed, steer);
        }

        public void Exit(ParkingAgent context)
        {
        }
    }

    public sealed class CompletedState : ICarState
    {
        public string Name => "Completed";
        public bool AllowsEmergencyInterrupt => false;

        public void Enter(ParkingAgent context)
        {
            context.Controller.Brake();
        }

        public void Tick(ParkingAgent context, float deltaTime)
        {
        }

        public void FixedTick(ParkingAgent context, float fixedDeltaTime)
        {
            context.Controller.Brake();
        }

        public void Exit(ParkingAgent context)
        {
        }
    }

    public sealed class EmergencyBrakeState : ICarState
    {
        private float clearTime;
        private float blockedTime;

        public string Name => "EmergencyBrake";
        public bool AllowsEmergencyInterrupt => false;

        public void Enter(ParkingAgent context)
        {
            clearTime = 0f;
            blockedTime = 0f;
            context.Controller.Brake();
        }

        public void Tick(ParkingAgent context, float deltaTime)
        {
        }

        public void FixedTick(ParkingAgent context, float fixedDeltaTime)
        {
            context.Controller.Brake();

            if (context.Sensors.Snapshot.EmergencyBlocked)
            {
                clearTime = 0f;
                blockedTime += fixedDeltaTime;
                if (blockedTime > 5.0f && context.HasCandidate)
                {
                    context.ResetScannerAfterRejectedCandidate();
                    context.ChangeState(new SearchSpaceState());
                }

                return;
            }

            clearTime += fixedDeltaTime;
            if (clearTime > 0.65f)
            {
                context.PopCurrentState();
            }
        }

        public void Exit(ParkingAgent context)
        {
        }
    }

    public static class ParkingPathPlanner
    {
        public static List<ParkingWaypoint> BuildPath(ParkingAgent context, ParkingCandidate candidate)
        {
            Vector3 lane = context.LaneForward.normalized;
            Vector3 side = context.Sensors.GetSearchSideDirection().normalized;
            ParkingProfile profile = context.Profile;
            Quaternion laneRotation = Quaternion.LookRotation(lane, Vector3.up);
            List<ParkingWaypoint> path = new List<ParkingWaypoint>();

            if (candidate.Style == ParkingManeuverStyle.Perpendicular)
            {
                Vector3 laneCenter = candidate.TargetPosition - side * profile.TargetLateralOffset;
                Vector3 approach = laneCenter - lane * 0.25f;
                Vector3 entry = candidate.TargetPosition - side * 2.0f;
                path.Add(new ParkingWaypoint(approach, laneRotation, false, profile.ParkingSpeed, 0.65f));
                path.Add(new ParkingWaypoint(entry, Quaternion.LookRotation(Vector3.Slerp(lane, side, 0.65f).normalized, Vector3.up), false, profile.ParkingSpeed * 0.75f, 0.55f));
                path.Add(new ParkingWaypoint(candidate.TargetPosition, candidate.TargetRotation, false, profile.ParkingSpeed * 0.4f, 0.28f));
            }
            else if (candidate.Style == ParkingManeuverStyle.Parallel)
            {
                Vector3 laneCenter = candidate.TargetPosition - side * profile.TargetLateralOffset;
                Vector3 stage = laneCenter + lane * 3.0f;
                Vector3 reverseEntry = candidate.TargetPosition + lane * 1.45f - side * 1.25f;
                Vector3 deepPoint = candidate.TargetPosition - lane * 0.45f;
                Vector3 straightenPoint = candidate.TargetPosition + lane * 0.15f;
                Quaternion arcRotation = Quaternion.LookRotation(Vector3.Slerp(lane, side, 0.25f).normalized, Vector3.up);

                path.Add(new ParkingWaypoint(stage, laneRotation, false, profile.ParkingSpeed, 0.65f));
                path.Add(new ParkingWaypoint(reverseEntry, arcRotation, true, profile.ParkingSpeed, 0.65f));
                path.Add(new ParkingWaypoint(deepPoint, candidate.TargetRotation, true, profile.ParkingSpeed * 0.65f, 0.45f));
                path.Add(new ParkingWaypoint(straightenPoint, candidate.TargetRotation, false, profile.ParkingSpeed * 0.45f, 0.32f));
                path.Add(new ParkingWaypoint(candidate.TargetPosition, candidate.TargetRotation, true, profile.ParkingSpeed * 0.32f, 0.24f));
            }
            else
            {
                Vector3 laneCenter = candidate.TargetPosition - side * profile.TargetLateralOffset;
                Vector3 stage = laneCenter - lane * 0.8f;
                Vector3 entry = candidate.TargetPosition - side * 1.65f - lane * 0.45f;
                path.Add(new ParkingWaypoint(stage, laneRotation, false, profile.ParkingSpeed, 0.65f));
                path.Add(new ParkingWaypoint(entry, Quaternion.LookRotation(Vector3.Slerp(lane, candidate.TargetRotation * Vector3.forward, 0.55f).normalized, Vector3.up), false, profile.ParkingSpeed * 0.78f, 0.5f));
                path.Add(new ParkingWaypoint(candidate.TargetPosition, candidate.TargetRotation, false, profile.ParkingSpeed * 0.4f, 0.28f));
            }

            return path;
        }
    }

    public sealed class PathFollower
    {
        private readonly List<ParkingWaypoint> waypoints;
        private int index;
        private float timeOnWaypoint;

        public PathFollower(List<ParkingWaypoint> waypoints)
        {
            this.waypoints = waypoints;
        }

        public bool FixedTick(ParkingAgent context, float fixedDeltaTime)
        {
            if (waypoints == null || waypoints.Count == 0 || index >= waypoints.Count)
            {
                context.Controller.Brake();
                return true;
            }

            ParkingWaypoint waypoint = waypoints[index];
            timeOnWaypoint += fixedDeltaTime;
            if (context.Profile.UseScriptedTarget)
            {
                return AssistedFixedTick(context, waypoint, fixedDeltaTime);
            }

            Transform vehicle = context.transform;
            Vector3 local = vehicle.InverseTransformPoint(waypoint.Position);
            float flatDistance = new Vector2(local.x, local.z).magnitude;
            float headingError = Vector3.SignedAngle(vehicle.forward, waypoint.Rotation * Vector3.forward, Vector3.up);

            bool isFinal = index == waypoints.Count - 1;
            if ((flatDistance <= waypoint.Radius && (!isFinal || Mathf.Abs(headingError) < 9f)) || (!isFinal && timeOnWaypoint > 5.5f))
            {
                index++;
                timeOnWaypoint = 0f;
                if (index >= waypoints.Count)
                {
                    context.Controller.Brake();
                    return true;
                }

                waypoint = waypoints[index];
                local = vehicle.InverseTransformPoint(waypoint.Position);
                flatDistance = new Vector2(local.x, local.z).magnitude;
                headingError = Vector3.SignedAngle(vehicle.forward, waypoint.Rotation * Vector3.forward, Vector3.up);
            }

            float targetForward = waypoint.Reverse ? -local.z : local.z;
            float targetSide = local.x;
            float lookAhead = Mathf.Max(0.35f, Mathf.Abs(targetForward));
            float steer = Mathf.Atan2(targetSide, lookAhead) * Mathf.Rad2Deg * 1.35f;
            if (waypoint.Reverse)
            {
                steer = -steer;
            }

            if (flatDistance < 1.2f)
            {
                steer += headingError * (waypoint.Reverse ? -0.55f : 0.55f);
            }

            float speedScale = Mathf.Clamp(flatDistance / 2.0f, 0.35f, 1f);
            float commandedSpeed = waypoint.Speed * speedScale * (waypoint.Reverse ? -1f : 1f);
            context.Controller.SetCommand(commandedSpeed, Mathf.Clamp(steer, -35f, 35f));
            return false;
        }

        private bool AssistedFixedTick(ParkingAgent context, ParkingWaypoint waypoint, float fixedDeltaTime)
        {
            Transform vehicle = context.transform;
            Vector3 before = vehicle.position;
            Vector3 toTarget = waypoint.Position - before;
            float distance = toTarget.magnitude;
            bool isFinal = index == waypoints.Count - 1;
            float headingError = Vector3.SignedAngle(vehicle.forward, waypoint.Rotation * Vector3.forward, Vector3.up);

            if ((distance <= waypoint.Radius && (!isFinal || Mathf.Abs(headingError) < 8f)) || (!isFinal && timeOnWaypoint > 4.0f))
            {
                index++;
                timeOnWaypoint = 0f;
                if (index >= waypoints.Count)
                {
                    context.Controller.Brake();
                    return true;
                }

                return false;
            }

            float moveSpeed = Mathf.Max(0.45f, Mathf.Abs(waypoint.Speed));
            Vector3 nextPosition = Vector3.MoveTowards(before, waypoint.Position, moveSpeed * fixedDeltaTime);
            Quaternion desiredRotation = waypoint.Rotation;
            if (toTarget.sqrMagnitude > 0.0001f)
            {
                if (waypoint.Reverse)
                {
                    Quaternion reverseTravelRotation = Quaternion.LookRotation(-toTarget.normalized, Vector3.up);
                    float reverseBlend = Mathf.InverseLerp(isFinal ? 1.25f : 1.05f, isFinal ? 0.3f : 0.35f, distance);
                    desiredRotation = Quaternion.Slerp(reverseTravelRotation, waypoint.Rotation, reverseBlend * 0.7f);
                }
                else
                {
                    Quaternion travelRotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
                    float finalBlend = Mathf.InverseLerp(isFinal ? 1.7f : 1.15f, isFinal ? 0.25f : 0.35f, distance);
                    desiredRotation = Quaternion.Slerp(travelRotation, waypoint.Rotation, finalBlend);
                }
            }

            float steerError = Vector3.SignedAngle(vehicle.forward, desiredRotation * Vector3.forward, Vector3.up);
            Quaternion nextRotation = Quaternion.RotateTowards(vehicle.rotation, desiredRotation, 75f * fixedDeltaTime);
            float signedSpeed = waypoint.Reverse ? -moveSpeed : moveSpeed;
            context.Controller.SetAssistedPose(nextPosition, nextRotation, signedSpeed, Mathf.Clamp(steerError * 0.6f, -35f, 35f));
            return false;
        }
    }

    public sealed class MovingObstacle : MonoBehaviour
    {
        private Vector3 start;
        private Vector3 end;
        private float speed;
        private float delay;
        private float time;

        public void Configure(Vector3 startPosition, Vector3 endPosition, float metersPerSecond, float startDelay)
        {
            start = startPosition;
            end = endPosition;
            speed = metersPerSecond;
            delay = startDelay;
            transform.position = start;
        }

        private void Update()
        {
            time += Time.deltaTime;
            if (time < delay)
            {
                return;
            }

            float distance = Vector3.Distance(start, end);
            if (distance < 0.01f)
            {
                return;
            }

            float t = Mathf.PingPong((time - delay) * speed / distance, 1f);
            transform.position = Vector3.Lerp(start, end, t);
        }
    }
}
