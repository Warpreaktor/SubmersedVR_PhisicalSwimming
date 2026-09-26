using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SubmersedVR
{
    /// <summary>
    /// Compact, non-interactive physical-swimming tuning HUD. The outer ring is the final
    /// movement input and the inner ring is the useful hand pull after palm/back-side physics.
    /// Exact hand speed is shown as text because that is the quantity Full Stroke Speed compares.
    /// </summary>
    static class PhysicalSwimmingStrokeHud
    {
        private const float Diameter = 210f;
        private const int TextureSize = 128;

        private static GameObject canvasRoot;
        private static GameObject root;
        private static Image movementRing;
        private static Image pullRing;
        private static TextMeshProUGUI valueText;
        private static Sprite circleSprite;
        private static Sprite ringSprite;
        private static Camera attachedCamera;

        public static void Update(
            bool visible,
            float movementSpeed,
            float movementLimit,
            float handSpeed,
            float fullStrokeSpeed,
            float pullStrength)
        {
            if (!visible)
            {
                Hide();
                return;
            }

            EnsureSetup();
            if (root == null)
            {
                return;
            }

            if (!root.activeSelf)
            {
                root.SetActive(true);
            }

            float safeMovementLimit = Mathf.Max(0.001f, movementLimit);
            float movementNormalized = Mathf.Clamp01(movementSpeed / safeMovementLimit);
            float pullNormalized = Mathf.Clamp01(pullStrength);

            if (movementRing != null)
            {
                movementRing.fillAmount = movementNormalized;
            }
            if (pullRing != null)
            {
                pullRing.fillAmount = pullNormalized;
            }
            if (valueText != null)
            {
                valueText.text =
                    $"<size=72%>MOVE</size>  {movementSpeed:0.00} / {movementLimit:0.00}\n" +
                    $"<size=72%>HAND</size>  {handSpeed:0.00} / {fullStrokeSpeed:0.00} m/s\n" +
                    $"<size=72%>PULL</size>  {pullNormalized * 100f:0}%";
            }
        }

        public static void Hide()
        {
            if (root != null && root.activeSelf)
            {
                root.SetActive(false);
            }
        }

        private static void EnsureSetup()
        {
            var rig = VRCameraRig.instance;
            if (rig == null || rig.uiCamera == null || uGUI.main == null)
            {
                return;
            }

            if (root != null && attachedCamera == rig.uiCamera)
            {
                return;
            }

            if (canvasRoot != null)
            {
                Object.Destroy(canvasRoot);
            }

            attachedCamera = rig.uiCamera;

            canvasRoot = new GameObject("PhysicalSwimmingStrokeHudCanvas")
                .WithParent(rig.uiCamera.transform)
                .ResetTransform();
            canvasRoot.layer = LayerID.UI;

            Canvas canvas = canvasRoot.CreateWorldCanvas();
            canvas.worldCamera = rig.uiCamera;
            canvas.sortingOrder = 120;

            RectTransform canvasRect = canvasRoot.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(Diameter + 24f, Diameter + 24f);
            canvasRoot.transform.localPosition = new Vector3(0f, -0.16f, 0.95f);
            canvasRoot.transform.localRotation = Quaternion.identity;
            canvasRoot.transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);

            root = new GameObject("PhysicalSwimmingStrokeHud", typeof(RectTransform));
            root.layer = LayerID.UI;
            root.transform.SetParent(canvasRoot.transform, false);

            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(Diameter, Diameter);
            rootRect.anchoredPosition = Vector2.zero;

            Image background = root.AddComponent<Image>();
            background.raycastTarget = false;
            background.sprite = GetOrCreateCircleSprite();
            background.type = Image.Type.Simple;
            background.preserveAspect = true;
            background.color = new Color(0.015f, 0.055f, 0.075f, 0.42f);

            movementRing = CreateRing(
                "MovementRing",
                root.transform,
                Diameter - 8f,
                new Color(0.20f, 0.90f, 1.00f, 0.78f)
            );

            pullRing = CreateRing(
                "PullRing",
                root.transform,
                Diameter - 38f,
                new Color(1.00f, 0.78f, 0.22f, 0.82f)
            );

            GameObject textGo = new GameObject("Values", typeof(RectTransform));
            textGo.layer = LayerID.UI;
            textGo.transform.SetParent(root.transform, false);

            valueText = textGo.AddComponent<TextMeshProUGUI>();
            valueText.raycastTarget = false;
            valueText.alignment = TextAlignmentOptions.Center;
            valueText.color = new Color(0.96f, 0.98f, 1f, 0.96f);
            valueText.fontSize = 26f;
            valueText.enableAutoSizing = false;
            valueText.richText = true;
            valueText.text = "MOVE  0.00 / 1.00\nHAND  0.00 m/s\nPULL  0%";

            TextMeshProUGUI templateText = FindTemplateText();
            if (templateText != null)
            {
                valueText.font = templateText.font;
                valueText.fontSharedMaterial = templateText.fontSharedMaterial;
            }

            RectTransform textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(18f, 40f);
            textRect.offsetMax = new Vector2(-18f, -40f);

            CanvasGroup canvasGroup = canvasRoot.AddComponent<CanvasGroup>();
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;

            root.SetActive(false);
        }

        private static Image CreateRing(string name, Transform parent, float diameter, Color color)
        {
            GameObject ringGo = new GameObject(name, typeof(RectTransform));
            ringGo.layer = LayerID.UI;
            ringGo.transform.SetParent(parent, false);

            RectTransform rect = ringGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(diameter, diameter);
            rect.anchoredPosition = Vector2.zero;

            Image image = ringGo.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = GetOrCreateRingSprite();
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Radial360;
            image.fillOrigin = (int)Image.Origin360.Top;
            image.fillClockwise = true;
            image.fillAmount = 0f;
            image.color = color;
            image.preserveAspect = true;
            return image;
        }

        private static TextMeshProUGUI FindTemplateText()
        {
            if (uGUI.main == null || uGUI.main.barsPanel == null)
            {
                return null;
            }

            TextMeshProUGUI[] texts = uGUI.main.barsPanel.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                if (texts[i] != null)
                {
                    return texts[i];
                }
            }
            return null;
        }

        private static Sprite GetOrCreateCircleSprite()
        {
            if (circleSprite == null)
            {
                circleSprite = CreateCircleTextureSprite("PhysicalSwimmingStrokeHudCircle", false);
            }
            return circleSprite;
        }

        private static Sprite GetOrCreateRingSprite()
        {
            if (ringSprite == null)
            {
                ringSprite = CreateCircleTextureSprite("PhysicalSwimmingStrokeHudRing", true);
            }
            return ringSprite;
        }

        private static Sprite CreateCircleTextureSprite(string name, bool ringOnly)
        {
            Texture2D texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
            texture.name = name + "Texture";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

            Color32[] pixels = new Color32[TextureSize * TextureSize];
            float center = (TextureSize - 1) * 0.5f;
            float outerRadius = TextureSize * 0.48f;
            float innerRadius = TextureSize * 0.415f;
            Color32 white = new Color32(255, 255, 255, 255);
            Color32 transparent = new Color32(0, 0, 0, 0);

            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    bool inside = distance <= outerRadius;
                    if (ringOnly)
                    {
                        inside = inside && distance >= innerRadius;
                    }
                    pixels[y * TextureSize + x] = inside ? white : transparent;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0, 0, TextureSize, TextureSize),
                new Vector2(0.5f, 0.5f),
                100f
            );
            sprite.name = name + "Sprite";
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }
    }
}
