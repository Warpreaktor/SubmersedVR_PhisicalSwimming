using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SubmersedVR
{
    /// <summary>
    /// Circular compass attached to the right wrist. The native head-HUD compass keeps
    /// running in the background so the game's normal compass-availability state remains
    /// authoritative, but its visuals are hidden while this wrist replacement is enabled.
    /// </summary>
    static class WristCompassIndicator
    {
        private const float Diameter = 104f;
        private const float CardinalRadius = 36f;
        private const float DepthDiameter = 76f;
        private const float DepthPetalOffset = 92f;
        private const int TextureSize = 128;

        // Placement probe using the same mechanism as the original SubmersedVR wrist HUD:
        // local +Y was confirmed to move the HUD toward the elbow, so probe the opposite direction:
        // 10 cm below the mirrored base Y to move the whole cluster toward the wrist/hand.
        private static readonly TransformOffset WristOffset = new TransformOffset(
            new Vector3(0.079f, 0.048f, -0.158f),
            new Vector3(350.494f, 271.600f, 115.839f)
        );

        private static GameObject wristTarget;
        private static GameObject root;
        private static CanvasGroup wristCanvasGroup;
        private static GameObject depthRoot;
        private static RectTransform depthRect;
        private static TextMeshProUGUI depthText;
        private static int lastDepth = int.MinValue;
        private static bool wristPlacementLogged;
        private static TextMeshProUGUI headingText;
        private static TextMeshProUGUI northText;
        private static TextMeshProUGUI eastText;
        private static TextMeshProUGUI southText;
        private static TextMeshProUGUI westText;
        private static Transform uiCamera;
        private static Sprite circleSprite;

        private static uGUI_DepthCompass nativeDepthCompass;
        private static uGUI_Compass nativeCompass;
        private static CanvasGroup nativeCompassCanvasGroup;
        private static float nativeCompassOriginalAlpha = 1f;
        private static bool nativeCompassHiddenByUs;

        private static bool enabledBySetting = true;
        private static float displayedHeading;
        private static bool hasDisplayedHeading;
        private static int lastDisplayedDegree = -1;

        public static void Setup()
        {
            var rig = VRCameraRig.instance;
            if (rig == null || rig.rightControllerUI == null || rig.uiCamera == null || uGUI.main == null)
            {
                return;
            }

            uiCamera = rig.uiCamera.transform;

            if (wristTarget == null)
            {
                wristTarget = new GameObject("WristCompassTarget")
                    .WithParent(rig.rightControllerUI)
                    .ResetTransform();
                WristOffset.Apply(wristTarget.transform);

                var canvasGo = new GameObject("WristCompassCanvas")
                    .WithParent(wristTarget)
                    .ResetTransform();

                var canvas = canvasGo.CreateWorldCanvas();
                canvas.worldCamera = rig.uiCamera;
                canvasGo.transform.localScale = new Vector3(0.0004f, 0.0004f, 0.0004f);
                wristCanvasGroup = canvasGo.AddComponent<CanvasGroup>();

                var canvasRect = canvasGo.GetComponent<RectTransform>();
                canvasRect.sizeDelta = new Vector2(Diameter + 20f, Diameter + 20f);

                BuildCompass(canvasGo.transform);
                BuildDepthPetal(canvasGo.transform);
            }

            Settings.PutCompassOnRightWristChanged -= OnSettingChanged;
            Settings.PutCompassOnRightWristChanged += OnSettingChanged;
            enabledBySetting = Settings.PutCompassOnRightWrist;

            ManagedUpdate.Unsubscribe(ManagedUpdate.Queue.PreCanvasFirst, new ManagedUpdate.OnUpdate(OnUpdate));
            ManagedUpdate.Subscribe(ManagedUpdate.Queue.PreCanvasFirst, new ManagedUpdate.OnUpdate(OnUpdate));

            ResolveNativeCompass();
            LogWristPlacement();
            OnUpdate();
        }

        public static void OnSettingChanged(bool enabled)
        {
            enabledBySetting = enabled;
            OnUpdate();
        }

        private static void OnUpdate()
        {
            if (!uGUI.isMainLevel)
            {
                if (root != null)
                {
                    root.SetActive(false);
                }
                if (depthRoot != null)
                {
                    depthRoot.SetActive(false);
                }
                SetNativeCompassHidden(false);
                return;
            }

            LogWristPlacement();
            ResolveNativeCompass();
            bool compassAvailable = IsCompassAvailable();
            bool showCompass = enabledBySetting && compassAvailable;

            // Keep the native component alive and updating, but remove the forehead HUD
            // only while our wrist replacement is enabled.
            SetNativeCompassHidden(showCompass);

            if (root == null || depthRoot == null)
            {
                Setup();
                return;
            }

            if (root.activeSelf != showCompass)
            {
                root.SetActive(showCompass);
            }
            if (!depthRoot.activeSelf)
            {
                depthRoot.SetActive(true);
            }

            // Depth belongs with world-orientation information on the right wrist. Keep it
            // as a small petal next to the compass; if the compass is unavailable/disabled,
            // depth occupies the main watch position instead of orbiting an empty spot.
            depthRect.anchoredPosition = showCompass
                ? new Vector2(0f, -DepthPetalOffset)
                : Vector2.zero;

            UpdateFacingCamera();
            if (showCompass)
            {
                UpdateHeading();
            }
            UpdateDepth();
        }

        private static void BuildCompass(Transform parent)
        {
            root = new GameObject("WristCompassIndicator", typeof(RectTransform));
            root.layer = LayerID.UI;
            root.transform.SetParent(parent, false);

            var rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(Diameter, Diameter);
            rootRect.anchoredPosition = Vector2.zero;
            rootRect.localRotation = Quaternion.identity;
            rootRect.localScale = Vector3.one;

            var background = root.AddComponent<Image>();
            background.raycastTarget = false;
            background.sprite = GetOrCreateCircleSprite();
            background.type = Image.Type.Simple;
            background.preserveAspect = true;

            var barsRect = uGUI.main.barsPanel != null
                ? uGUI.main.barsPanel.GetComponent<RectTransform>()
                : null;
            var templateImage = FindTemplateImage(barsRect);
            if (templateImage != null && templateImage.material != null)
            {
                background.material = templateImage.material;
            }

            var templateText = FindTemplateText(barsRect);
            headingText = CreateText(root.transform, "Heading", "N", 22f, new Color(0.90f, 0.98f, 1f, 1f), templateText);
            headingText.rectTransform.anchorMin = Vector2.zero;
            headingText.rectTransform.anchorMax = Vector2.one;
            headingText.rectTransform.offsetMin = new Vector2(14f, 22f);
            headingText.rectTransform.offsetMax = new Vector2(-14f, -22f);
            headingText.text = "N\n<size=55%>000°</size>";

            northText = CreateCardinal(root.transform, "North", "N", templateText);
            eastText = CreateCardinal(root.transform, "East", "E", templateText);
            southText = CreateCardinal(root.transform, "South", "S", templateText);
            westText = CreateCardinal(root.transform, "West", "W", templateText);

            var marker = CreateText(root.transform, "ForwardMarker", "•", 19f, new Color(1f, 0.78f, 0.22f, 1f), templateText);
            marker.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            marker.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            marker.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            marker.rectTransform.sizeDelta = new Vector2(18f, 18f);
            marker.rectTransform.anchoredPosition = new Vector2(0f, Diameter * 0.39f);

            root.SetActive(false);
        }

        private static void BuildDepthPetal(Transform parent)
        {
            depthRoot = new GameObject("WristDepthIndicator", typeof(RectTransform));
            depthRoot.layer = LayerID.UI;
            depthRoot.transform.SetParent(parent, false);

            depthRect = depthRoot.GetComponent<RectTransform>();
            depthRect.anchorMin = new Vector2(0.5f, 0.5f);
            depthRect.anchorMax = new Vector2(0.5f, 0.5f);
            depthRect.pivot = new Vector2(0.5f, 0.5f);
            depthRect.sizeDelta = new Vector2(DepthDiameter, DepthDiameter);
            depthRect.anchoredPosition = Vector2.zero;
            depthRect.localRotation = Quaternion.identity;
            depthRect.localScale = Vector3.one;

            var background = depthRoot.AddComponent<Image>();
            background.raycastTarget = false;
            background.sprite = GetOrCreateCircleSprite();
            background.type = Image.Type.Simple;
            background.preserveAspect = true;

            var barsRect = uGUI.main.barsPanel != null
                ? uGUI.main.barsPanel.GetComponent<RectTransform>()
                : null;
            var templateImage = FindTemplateImage(barsRect);
            if (templateImage != null && templateImage.material != null)
            {
                background.material = templateImage.material;
            }

            var templateText = FindTemplateText(barsRect);
            depthText = CreateText(
                depthRoot.transform,
                "Depth",
                "0\n<size=48%>m</size>",
                25f,
                new Color(0.88f, 0.98f, 1f, 1f),
                templateText
            );
            depthText.rectTransform.anchorMin = Vector2.zero;
            depthText.rectTransform.anchorMax = Vector2.one;
            depthText.rectTransform.offsetMin = new Vector2(5f, 6f);
            depthText.rectTransform.offsetMax = new Vector2(-5f, -6f);

            depthRoot.SetActive(false);
        }

        private static TextMeshProUGUI CreateCardinal(Transform parent, string name, string label, TextMeshProUGUI template)
        {
            var text = CreateText(parent, name, label, 18f, new Color(0.55f, 0.92f, 1f, 1f), template);
            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(24f, 24f);
            return text;
        }

        private static TextMeshProUGUI CreateText(
            Transform parent,
            string name,
            string value,
            float fontSize,
            Color color,
            TextMeshProUGUI template)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerID.UI;
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.raycastTarget = false;
            text.alignment = TextAlignmentOptions.Center;
            text.color = color;
            text.fontSize = fontSize;
            text.enableAutoSizing = false;
            text.richText = true;
            text.text = value;

            if (template != null)
            {
                text.font = template.font;
                text.fontSharedMaterial = template.fontSharedMaterial;
            }

            var rect = go.GetComponent<RectTransform>();
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            return text;
        }

        private static void UpdateFacingCamera()
        {
            if (wristCanvasGroup == null || wristTarget == null || uiCamera == null)
            {
                return;
            }

            Vector3 wristDir = wristTarget.transform.TransformDirection(Vector3.forward);
            Vector3 toCamera = (wristTarget.transform.position - uiCamera.position).normalized;
            float wristCameraDot = Vector3.Dot(wristDir, toCamera);
            wristCanvasGroup.alpha = Mathf.Max(wristCameraDot, 0f);
        }

        private static void UpdateHeading()
        {
            if (wristTarget == null)
            {
                return;
            }

            // The marker at 12 o'clock is fixed in the wrist-compass canvas, so the heading
            // must come from that same physical axis. Using the HMD here made the wrist dial
            // continue behaving like the vanilla forehead compass even after moving the UI.
            Vector3 forward = wristTarget.transform.up;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                return;
            }

            forward.Normalize();
            float targetHeading = Mathf.Repeat(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 360f);

            if (!hasDisplayedHeading)
            {
                displayedHeading = targetHeading;
                hasDisplayedHeading = true;
            }
            else
            {
                float smoothing = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
                displayedHeading = Mathf.LerpAngle(displayedHeading, targetHeading, smoothing);
                displayedHeading = Mathf.Repeat(displayedHeading, 360f);
            }

            PositionCardinal(northText, 0f, displayedHeading);
            PositionCardinal(eastText, 90f, displayedHeading);
            PositionCardinal(southText, 180f, displayedHeading);
            PositionCardinal(westText, 270f, displayedHeading);

            int degree = Mathf.RoundToInt(displayedHeading) % 360;
            if (degree != lastDisplayedDegree && headingText != null)
            {
                lastDisplayedDegree = degree;
                headingText.text = $"{GetCardinalName(degree)}\n<size=55%>{degree:000}°</size>";
            }
        }

        private static void PositionCardinal(TextMeshProUGUI label, float worldHeading, float currentHeading)
        {
            if (label == null)
            {
                return;
            }

            float angle = (worldHeading - currentHeading) * Mathf.Deg2Rad;
            label.rectTransform.anchoredPosition = new Vector2(
                Mathf.Sin(angle) * CardinalRadius,
                Mathf.Cos(angle) * CardinalRadius);
        }

        private static string GetCardinalName(float heading)
        {
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            int index = Mathf.RoundToInt(heading / 45f) % names.Length;
            return names[index];
        }

        private static void LogWristPlacement()
        {
            if (wristPlacementLogged || wristTarget == null)
            {
                return;
            }

            var rig = VRCameraRig.instance;
            Transform leftWristTarget = rig != null && rig.leftControllerUI != null
                ? rig.leftControllerUI.transform.Find("WristTarget")
                : null;

            Transform right = wristTarget.transform;
            Mod.logger?.LogInfo(
                $"[WRIST PLACEMENT] RIGHT target localPos={FormatVector(right.localPosition)} " +
                $"localEuler={FormatVector(right.localEulerAngles)} worldPos={FormatVector(right.position)} " +
                $"worldEuler={FormatVector(right.eulerAngles)}"
            );

            if (leftWristTarget != null)
            {
                Mod.logger?.LogInfo(
                    $"[WRIST PLACEMENT] LEFT target localPos={FormatVector(leftWristTarget.localPosition)} " +
                    $"localEuler={FormatVector(leftWristTarget.localEulerAngles)} worldPos={FormatVector(leftWristTarget.position)} " +
                    $"worldEuler={FormatVector(leftWristTarget.eulerAngles)}"
                );
            }
            else
            {
                Mod.logger?.LogWarning("[WRIST PLACEMENT] LEFT WristTarget not found.");
            }

            wristPlacementLogged = true;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:0.000}, {value.y:0.000}, {value.z:0.000})";
        }

        private static void UpdateDepth()
        {
            var player = Player.main;
            if (player == null || depthText == null)
            {
                return;
            }

            int depth = Mathf.Max(0, Mathf.RoundToInt(player.GetDepth()));
            if (depth == lastDepth)
            {
                return;
            }

            lastDepth = depth;
            depthText.text = $"{depth}\n<size=48%>m</size>";
        }

        private static bool IsCompassAvailable()
        {
            // The equipped-compass state is owned by uGUI_DepthCompass, not by
            // the uGUI_Compass visual itself. Use the game's own check so the wrist
            // HUD follows exactly the same equipment state as the native HUD.
            return nativeDepthCompass != null && nativeDepthCompass.IsCompassEnabled();
        }

        private static void ResolveNativeCompass()
        {
            if (nativeDepthCompass == null)
            {
                nativeDepthCompass = UnityEngine.Object.FindObjectOfType<uGUI_DepthCompass>();
            }

            if (nativeCompass == null && nativeDepthCompass != null)
            {
                nativeCompass = nativeDepthCompass.compass;
            }

            if (nativeCompass == null)
            {
                nativeCompass = UnityEngine.Object.FindObjectOfType<uGUI_Compass>();
            }

            if (nativeCompass == null || nativeCompassCanvasGroup != null)
            {
                return;
            }

            nativeCompassCanvasGroup = nativeCompass.GetComponent<CanvasGroup>();
            if (nativeCompassCanvasGroup == null)
            {
                nativeCompassCanvasGroup = nativeCompass.gameObject.AddComponent<CanvasGroup>();
            }

            nativeCompassOriginalAlpha = nativeCompassCanvasGroup.alpha;
            nativeCompassHiddenByUs = false;
            Mod.logger?.LogInfo("Wrist compass linked to native uGUI_DepthCompass state.");
        }

        private static void SetNativeCompassHidden(bool hidden)
        {
            ResolveNativeCompass();
            if (nativeCompassCanvasGroup == null)
            {
                return;
            }

            if (hidden)
            {
                if (!nativeCompassHiddenByUs)
                {
                    nativeCompassOriginalAlpha = nativeCompassCanvasGroup.alpha;
                    nativeCompassHiddenByUs = true;
                }
                nativeCompassCanvasGroup.alpha = 0f;
            }
            else if (nativeCompassHiddenByUs)
            {
                nativeCompassCanvasGroup.alpha = nativeCompassOriginalAlpha;
                nativeCompassHiddenByUs = false;
            }
        }

        private static Image FindTemplateImage(RectTransform parent)
        {
            if (parent == null)
            {
                return null;
            }

            var images = parent.GetComponentsInChildren<Image>(true);
            foreach (var image in images)
            {
                if (image != null)
                {
                    return image;
                }
            }
            return null;
        }

        private static TextMeshProUGUI FindTemplateText(RectTransform parent)
        {
            if (parent == null)
            {
                return null;
            }

            var texts = parent.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var text in texts)
            {
                if (text != null)
                {
                    return text;
                }
            }
            return null;
        }

        private static Sprite GetOrCreateCircleSprite()
        {
            if (circleSprite != null)
            {
                return circleSprite;
            }

            var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
            texture.name = "WristCompassCircle";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

            var pixels = new Color32[TextureSize * TextureSize];
            var center = (TextureSize - 1) * 0.5f;
            var outerRadius = TextureSize * 0.48f;
            var ringInnerRadius = TextureSize * 0.395f;

            var ringColor = new Color32(74, 218, 239, 245);
            var fillColor = new Color32(4, 26, 35, 220);
            var transparent = new Color32(0, 0, 0, 0);

            for (var y = 0; y < TextureSize; y++)
            {
                for (var x = 0; x < TextureSize; x++)
                {
                    var dx = x - center;
                    var dy = y - center;
                    var distance = Mathf.Sqrt(dx * dx + dy * dy);

                    Color32 color;
                    if (distance > outerRadius)
                    {
                        color = transparent;
                    }
                    else if (distance >= ringInnerRadius)
                    {
                        color = ringColor;
                    }
                    else
                    {
                        color = fillColor;
                    }

                    pixels[y * TextureSize + x] = color;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            circleSprite = Sprite.Create(
                texture,
                new Rect(0, 0, TextureSize, TextureSize),
                new Vector2(0.5f, 0.5f),
                100f);
            circleSprite.name = "WristCompassCircleSprite";
            circleSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;

            return circleSprite;
        }
    }
}
