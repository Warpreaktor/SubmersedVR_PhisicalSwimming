using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SubmersedVR
{
    /// <summary>
    /// Small depth readout attached directly to the game's survival bars panel.
    /// Keeping the indicator inside barsPanel makes it use the same VR UI hierarchy
    /// as the native health/oxygen/food/water indicators.
    /// </summary>
    static class WristDepthIndicator
    {
        private const float Diameter = 88f;
        private const float Gap = 10f;
        private const int TextureSize = 128;
        private const string RootName = "WristDepthIndicator";

        private static GameObject root;
        private static RectTransform rootRect;
        private static RectTransform barsRect;
        private static TextMeshProUGUI depthText;
        private static Image background;
        private static Sprite circleSprite;
        private static int lastDepth = int.MinValue;

        public static void Setup(RectTransform survivalBarsRect)
        {
            if (survivalBarsRect == null)
            {
                return;
            }

            barsRect = survivalBarsRect;

            // Setup may be called again when the game's UI is rebuilt. Reuse the
            // existing object instead of creating another depth indicator.
            var existing = survivalBarsRect.Find(RootName);
            if (existing != null)
            {
                root = existing.gameObject;
                rootRect = existing as RectTransform;
                background = root.GetComponent<Image>();
                depthText = root.GetComponentInChildren<TextMeshProUGUI>(true);
                return;
            }

            root = new GameObject(RootName, typeof(RectTransform));
            root.layer = LayerID.UI;
            root.transform.SetParent(survivalBarsRect, false);

            rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.zero;
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(Diameter, Diameter);
            rootRect.localRotation = Quaternion.identity;
            rootRect.localScale = Vector3.one;

            // Do not let a LayoutGroup on barsPanel rearrange the native bars.
            var layoutElement = root.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            background = root.AddComponent<Image>();
            background.raycastTarget = false;
            background.sprite = GetOrCreateCircleSprite();
            background.type = Image.Type.Simple;
            background.preserveAspect = true;

            // Reuse a material from the survival bars when available. In VR this
            // matters: the native UI hierarchy/materials are already configured
            // for the game's stereo rendering path.
            var templateImage = FindTemplateImage(survivalBarsRect);
            if (templateImage != null && templateImage.material != null)
            {
                background.material = templateImage.material;
            }

            var textGo = new GameObject("DepthText", typeof(RectTransform));
            textGo.layer = LayerID.UI;
            textGo.transform.SetParent(root.transform, false);

            depthText = textGo.AddComponent<TextMeshProUGUI>();
            depthText.raycastTarget = false;
            depthText.alignment = TextAlignmentOptions.Center;
            depthText.color = new Color(0.88f, 0.98f, 1f, 1f);
            depthText.fontSize = 31f;
            depthText.enableAutoSizing = false;
            depthText.richText = true;
            depthText.text = "0\n<size=55%>m</size>";

            // Use the font/material from the wrist survival bars themselves,
            // rather than an arbitrary HUD text element.
            var templateText = FindTemplateText(survivalBarsRect);
            if (templateText != null)
            {
                depthText.font = templateText.font;
                depthText.fontSharedMaterial = templateText.fontSharedMaterial;
            }

            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.offsetMin = new Vector2(5f, 7f);
            textRect.offsetMax = new Vector2(-5f, -7f);
            textRect.localRotation = Quaternion.identity;
            textRect.localScale = Vector3.one;

            UpdatePosition();
        }

        public static void SetEnabled(bool enabled)
        {
            if (root != null)
            {
                root.SetActive(enabled);
            }
        }

        public static void Update(bool visible)
        {
            if (root == null || barsRect == null)
            {
                return;
            }

            if (root.activeSelf != visible)
            {
                root.SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            UpdatePosition();
            UpdateDepth();
        }

        private static void UpdateDepth()
        {
            var player = Player.main;
            if (player == null || depthText == null)
            {
                return;
            }

            var depth = Mathf.Max(0, Mathf.RoundToInt(player.GetDepth()));
            if (depth == lastDepth)
            {
                return;
            }

            lastDepth = depth;
            depthText.text = $"{depth}\n<size=55%>m</size>";
        }

        private static void UpdatePosition()
        {
            if (rootRect == null || barsRect == null)
            {
                return;
            }

            if (!TryGetSurvivalBarsBounds(out var bounds))
            {
                return;
            }

            var radius = Diameter * 0.5f;
            Vector3 position;

            if (bounds.size.x >= bounds.size.y)
            {
                // Standard SubmersedVR arrangement: one more circle on the right.
                position = new Vector3(bounds.max.x + Gap + radius, bounds.center.y, 0f);
            }
            else
            {
                position = new Vector3(bounds.center.x, bounds.min.y - Gap - radius, 0f);
            }

            rootRect.localPosition = position;
        }

        private static bool TryGetSurvivalBarsBounds(out Bounds result)
        {
            result = new Bounds();
            var hasBounds = false;

            AddBounds(barsRect.GetComponentInChildren<uGUI_HealthBar>(true), ref result, ref hasBounds);
            AddBounds(barsRect.GetComponentInChildren<uGUI_OxygenBar>(true), ref result, ref hasBounds);
            AddBounds(barsRect.GetComponentInChildren<uGUI_FoodBar>(true), ref result, ref hasBounds);
            AddBounds(barsRect.GetComponentInChildren<uGUI_WaterBar>(true), ref result, ref hasBounds);

            // Fallback for a UI variant where the concrete bar components cannot
            // be found. Use the panel's own rect without including our new child.
            if (!hasBounds)
            {
                var rect = barsRect.rect;
                result = new Bounds(rect.center, new Vector3(rect.width, rect.height, 0f));
                hasBounds = rect.width > 0f || rect.height > 0f;
            }

            return hasBounds;
        }

        private static void AddBounds(Component component, ref Bounds combined, ref bool hasBounds)
        {
            if (component == null)
            {
                return;
            }

            var rect = component.GetComponent<RectTransform>();
            if (rect == null)
            {
                return;
            }

            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(barsRect, rect);
            if (!hasBounds)
            {
                combined = bounds;
                hasBounds = true;
                return;
            }

            combined.Encapsulate(bounds.min);
            combined.Encapsulate(bounds.max);
        }

        private static Image FindTemplateImage(RectTransform parent)
        {
            var images = parent.GetComponentsInChildren<Image>(true);
            foreach (var image in images)
            {
                if (image != null && image.gameObject != root)
                {
                    return image;
                }
            }
            return null;
        }

        private static TextMeshProUGUI FindTemplateText(RectTransform parent)
        {
            var texts = parent.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var text in texts)
            {
                if (text != null && (root == null || !text.transform.IsChildOf(root.transform)))
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
            texture.name = "WristDepthIndicatorCircle";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

            var pixels = new Color32[TextureSize * TextureSize];
            var center = (TextureSize - 1) * 0.5f;
            var outerRadius = TextureSize * 0.48f;
            var ringInnerRadius = TextureSize * 0.395f;

            var ringColor = new Color32(74, 218, 239, 245);
            var fillColor = new Color32(4, 26, 35, 210);
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
            circleSprite.name = "WristDepthIndicatorCircleSprite";
            circleSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;

            return circleSprite;
        }
    }
}
