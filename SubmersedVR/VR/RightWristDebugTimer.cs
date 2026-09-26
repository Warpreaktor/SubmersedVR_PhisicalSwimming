using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SubmersedVR
{
    /// <summary>
    /// Small recording stopwatch on the right wrist. It is visible only while
    /// physical-swimming telemetry is being recorded.
    /// </summary>
    static class RightWristDebugTimer
    {
        private const float Diameter = 96f;
        private const int TextureSize = 128;

        // Mirrored version of the working left-wrist HUD transform.
        private static readonly TransformOffset WristOffset = new TransformOffset(
            new Vector3(0.079f, 0.148f, -0.158f),
            new Vector3(350.494f, 271.600f, 115.839f)
        );

        private static GameObject wristTarget;
        private static GameObject root;
        private static CanvasGroup canvasGroup;
        private static TextMeshProUGUI timerText;
        private static Transform uiCamera;
        private static Sprite circleSprite;
        private static int lastDisplayedTenth = -1;

        public static void Update(bool recording, float elapsedSeconds)
        {
            if (root == null)
            {
                Setup();
            }

            if (root == null)
            {
                return;
            }

            if (root.activeSelf != recording)
            {
                root.SetActive(recording);
            }

            if (!recording)
            {
                lastDisplayedTenth = -1;
                return;
            }

            UpdateFacingCamera();
            UpdateText(elapsedSeconds);
        }

        private static void Setup()
        {
            var rig = VRCameraRig.instance;
            if (rig == null || rig.rightControllerUI == null || rig.uiCamera == null || uGUI.main == null)
            {
                return;
            }

            uiCamera = rig.uiCamera.transform;

            wristTarget = new GameObject("PhysicalSwimmingDebugWristTarget")
                .WithParent(rig.rightControllerUI)
                .ResetTransform();
            WristOffset.Apply(wristTarget.transform);

            var canvasGo = new GameObject("PhysicalSwimmingDebugWristCanvas")
                .WithParent(wristTarget)
                .ResetTransform();

            var canvas = canvasGo.CreateWorldCanvas();
            canvas.worldCamera = rig.uiCamera;
            canvasGo.transform.localScale = new Vector3(0.0004f, 0.0004f, 0.0004f);
            canvasGroup = canvasGo.AddComponent<CanvasGroup>();

            var canvasRect = canvasGo.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(Diameter + 20f, Diameter + 20f);

            root = new GameObject("PhysicalSwimmingDebugTimer", typeof(RectTransform));
            root.layer = LayerID.UI;
            root.transform.SetParent(canvasGo.transform, false);

            var rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(Diameter, Diameter);
            rootRect.anchoredPosition = new Vector2(112f, 0f);
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

            var textGo = new GameObject("TimerText", typeof(RectTransform));
            textGo.layer = LayerID.UI;
            textGo.transform.SetParent(root.transform, false);

            timerText = textGo.AddComponent<TextMeshProUGUI>();
            timerText.raycastTarget = false;
            timerText.alignment = TextAlignmentOptions.Center;
            timerText.color = new Color(1f, 0.95f, 0.85f, 1f);
            timerText.fontSize = 28f;
            timerText.enableAutoSizing = false;
            timerText.richText = true;
            timerText.text = "0.0\n<size=42%>REC</size>";

            var templateText = FindTemplateText(barsRect);
            if (templateText != null)
            {
                timerText.font = templateText.font;
                timerText.fontSharedMaterial = templateText.fontSharedMaterial;
            }

            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(4f, 7f);
            textRect.offsetMax = new Vector2(-4f, -7f);
            textRect.localRotation = Quaternion.identity;
            textRect.localScale = Vector3.one;

            root.SetActive(false);
        }

        private static void UpdateFacingCamera()
        {
            if (canvasGroup == null || wristTarget == null || uiCamera == null)
            {
                return;
            }

            Vector3 wristDir = wristTarget.transform.TransformDirection(Vector3.forward);
            Vector3 toCamera = (wristTarget.transform.position - uiCamera.position).normalized;
            float wristCameraDot = Vector3.Dot(wristDir, toCamera);
            canvasGroup.alpha = Mathf.Max(wristCameraDot, 0f);
        }

        private static void UpdateText(float elapsedSeconds)
        {
            if (timerText == null)
            {
                return;
            }

            int tenth = Mathf.Max(0, Mathf.FloorToInt(elapsedSeconds * 10f));
            if (tenth == lastDisplayedTenth)
            {
                return;
            }

            lastDisplayedTenth = tenth;
            timerText.text = $"{tenth / 10f:0.0}\n<size=42%>REC</size>";
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
            texture.name = "PhysicalSwimmingDebugTimerCircle";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

            var pixels = new Color32[TextureSize * TextureSize];
            float center = (TextureSize - 1) * 0.5f;
            float outerRadius = TextureSize * 0.48f;
            float ringInnerRadius = TextureSize * 0.395f;

            var ringColor = new Color32(255, 105, 70, 250);
            var fillColor = new Color32(40, 12, 8, 220);
            var transparent = new Color32(0, 0, 0, 0);

            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

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
                100f
            );
            circleSprite.name = "PhysicalSwimmingDebugTimerCircleSprite";
            circleSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;

            return circleSprite;
        }
    }
}
