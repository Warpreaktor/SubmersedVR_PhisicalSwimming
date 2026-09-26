using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SubmersedVR.PDAInteraction
{
    /// <summary>
    /// The stock Subnautica options sidebar has a fixed-height tab list and no scrolling.
    /// SubmersedVR now adds several top-level sections, so wrap only the tab list in a
    /// ScrollRect while keeping the Back button fixed below it.
    /// </summary>
    internal static class OptionsTabScroll
    {
        private const string ViewportName = "SubmersedVR_OptionsTabsScrollViewport";
        private const float MinimumSidebarWidth = 285f;
        private const float ExtraSidebarWidth = 140f;
        private const float MainPanelGap = 18f;

        public static void Setup(uGUI_OptionsPanel panel)
        {
            if (panel == null || HasExistingViewport(panel.transform))
            {
                return;
            }

            RectTransform content = GetMemberTransform(panel, "tabsContainer") as RectTransform;
            if (content == null || content.parent == null)
            {
                Mod.logger.LogWarning("Could not enable options-tab scrolling: tabsContainer was not found.");
                return;
            }

            RectTransform parent = content.parent as RectTransform;
            if (parent == null)
            {
                return;
            }

            RectTransform back = GetMemberTransform(panel, "buttonBack") as RectTransform;
            if (back != null && back.IsChildOf(content))
            {
                // Preserve the stock Back button outside the scrollable content.
                back.SetParent(parent, true);
                back.SetAsLastSibling();
            }

            // On the very first opening Subnautica has not completed the options layout yet. Force
            // one stock pass before measuring anything, otherwise the sidebar is widened while the
            // main content still reports its pre-layout rectangle and overlaps for the whole first
            // visit. The later updater remains as protection against delayed rebuilds.
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(parent);

            int siblingIndex = content.GetSiblingIndex();
            Vector2 originalAnchoredPosition = content.anchoredPosition;
            Vector2 originalAnchorMin = content.anchorMin;
            Vector2 originalAnchorMax = content.anchorMax;
            Vector2 originalPivot = content.pivot;
            Vector2 originalSize = content.rect.size;
            Vector2 originalOffsetMin = content.offsetMin;
            Vector2 originalOffsetMax = content.offsetMax;

            GameObject viewportGo = new GameObject(ViewportName, typeof(RectTransform), typeof(RectMask2D), typeof(ScrollRect));
            RectTransform viewport = viewportGo.GetComponent<RectTransform>();
            viewport.SetParent(parent, false);
            viewport.SetSiblingIndex(siblingIndex);
            viewport.anchorMin = originalAnchorMin;
            viewport.anchorMax = originalAnchorMax;
            viewport.pivot = originalPivot;
            viewport.anchoredPosition = originalAnchoredPosition;
            viewport.offsetMin = originalOffsetMin;
            viewport.offsetMax = originalOffsetMax;
            viewport.localScale = Vector3.one;

            // The stock sidebar is too narrow for the extra VR tabs. In particular, names like
            // "Physical Swimming" and "Troubleshooting" become a stack of awkward line breaks.
            // Keep the left edge where Subnautica placed it and grow the list to the right.
            float measuredWidth = Mathf.Max(originalSize.x, MeasureWidestChild(content));
            float targetWidth = Mathf.Max(measuredWidth + ExtraSidebarWidth, MinimumSidebarWidth);
            SetWidthPreserveLeftEdge(viewport, targetWidth);
            if (back != null)
            {
                SetWidthPreserveLeftEdge(back, targetWidth);
            }
            ReflowMainContentPanels(parent, viewport, back);

            // Reserve a fixed strip for the stock Back button. The original tab list often
            // extended behind it, which is why our last custom tabs were barely visible.
            float backReserve = back != null ? Mathf.Max(80f, back.rect.height + 18f) : 100f;
            float availableHeight = Mathf.Max(180f, originalSize.y - backReserve);
            viewport.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, availableHeight);
            viewport.anchoredPosition += Vector2.up * (backReserve * 0.5f);

            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.localScale = Vector3.one;
            content.offsetMin = new Vector2(0f, content.offsetMin.y);
            content.offsetMax = new Vector2(0f, content.offsetMax.y);

            EnsureTabButtonWidths(content, viewport);
            EnsureContentHeight(content, viewport, originalSize.y);

            ScrollRect scroll = viewportGo.GetComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;
            scroll.scrollSensitivity = 55f;

            Scrollbar scrollbar = CreateScrollbar(viewport);
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.verticalScrollbarSpacing = -2f;
            scroll.verticalNormalizedPosition = 1f;
        }


        private static bool HasExistingViewport(Transform root)
        {
            if (root == null)
            {
                return false;
            }

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == ViewportName)
                {
                    return true;
                }
            }
            return false;
        }

        private static Transform GetMemberTransform(object owner, string memberName)
        {
            if (owner == null)
            {
                return null;
            }

            Type type = owner.GetType();
            FieldInfo field = AccessTools.Field(type, memberName);
            object value = field != null ? field.GetValue(owner) : null;
            if (value == null)
            {
                PropertyInfo property = AccessTools.Property(type, memberName);
                value = property != null && property.CanRead ? property.GetValue(owner, null) : null;
            }

            if (value is Transform transform)
            {
                return transform;
            }
            if (value is Component component)
            {
                return component.transform;
            }
            if (value is GameObject gameObject)
            {
                return gameObject.transform;
            }
            return null;
        }

        internal static void SetWidthPreserveLeftEdge(RectTransform rect, float width)
        {
            if (rect == null || width <= 1f)
            {
                return;
            }

            float oldWidth = rect.rect.width;
            if (Mathf.Abs(oldWidth - width) < 0.5f)
            {
                return;
            }

            // At AddTabs time Unity can still report a zero-sized rect. Set the requested width
            // anyway; only apply the positional correction when the previous width is meaningful.
            float delta = width - Mathf.Max(0f, oldWidth);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            if (oldWidth > 1f)
            {
                rect.anchoredPosition += Vector2.right * (delta * rect.pivot.x);
            }
        }

        private static float MeasureWidestChild(RectTransform content)
        {
            if (content == null)
            {
                return 0f;
            }

            float width = 0f;
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform child = content.GetChild(i) as RectTransform;
                if (child != null && child.gameObject.activeSelf)
                {
                    width = Mathf.Max(width, child.rect.width);
                }
            }
            return width;
        }


        private static float GetLeftEdge(RectTransform rect)
        {
            return rect.anchoredPosition.x - (rect.rect.width * rect.pivot.x);
        }

        private static float GetRightEdge(RectTransform rect)
        {
            return GetLeftEdge(rect) + rect.rect.width;
        }

        private static void SetLeftEdgePreserveRightEdge(RectTransform rect, float leftEdge)
        {
            if (rect == null)
            {
                return;
            }

            float currentWidth = Mathf.Max(1f, rect.rect.width);
            float currentRight = GetRightEdge(rect);
            float newWidth = Mathf.Max(80f, currentRight - leftEdge);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, newWidth);
            rect.anchoredPosition = new Vector2(leftEdge + (newWidth * rect.pivot.x), rect.anchoredPosition.y);
        }

        internal static void ReflowMainContentPanels(RectTransform parent, RectTransform viewport, RectTransform back)
        {
            if (parent == null || viewport == null)
            {
                return;
            }

            float sidebarRight = GetRightEdge(viewport);
            float desiredLeft = sidebarRight + MainPanelGap;

            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child == null || child == viewport || child == back)
                {
                    continue;
                }

                if (child.GetComponent<Scrollbar>() != null)
                {
                    continue;
                }

                if (child.rect.width < 160f || child.rect.height < 120f)
                {
                    continue;
                }

                float childRight = GetRightEdge(child);
                if (childRight <= desiredLeft + 40f)
                {
                    continue;
                }

                float childLeft = GetLeftEdge(child);
                if (childLeft >= desiredLeft - 1f)
                {
                    continue;
                }

                SetLeftEdgePreserveRightEdge(child, desiredLeft);
            }
        }

        internal static void EnsureTabButtonWidths(RectTransform content, RectTransform viewport)
        {
            if (content == null || viewport == null)
            {
                return;
            }

            float width = Mathf.Max(1f, viewport.rect.width - 24f);
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform child = content.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf)
                {
                    continue;
                }

                child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);

                LayoutElement layout = child.GetComponent<LayoutElement>();
                if (layout != null)
                {
                    layout.minWidth = width;
                    layout.preferredWidth = width;
                }
            }
        }

        internal static void EnsureContentHeight(RectTransform content, RectTransform viewport, float originalHeight)
        {
            if (content == null || viewport == null)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            float preferredHeight = LayoutUtility.GetPreferredHeight(content);

            if (preferredHeight <= 1f)
            {
                VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
                float spacing = layout != null ? layout.spacing : 8f;
                float calculated = layout != null ? layout.padding.top + layout.padding.bottom : 0f;
                int visibleChildren = 0;

                for (int i = 0; i < content.childCount; i++)
                {
                    RectTransform child = content.GetChild(i) as RectTransform;
                    if (child == null || !child.gameObject.activeSelf)
                    {
                        continue;
                    }
                    calculated += Mathf.Max(1f, child.rect.height);
                    visibleChildren++;
                }
                if (visibleChildren > 1)
                {
                    calculated += spacing * (visibleChildren - 1);
                }
                preferredHeight = calculated;
            }

            float height = Mathf.Max(viewport.rect.height, originalHeight, preferredHeight);
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        }

        private static Scrollbar CreateScrollbar(RectTransform viewport)
        {
            GameObject barGo = new GameObject("SubmersedVR_OptionsTabsScrollbar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Scrollbar));
            RectTransform barRect = barGo.GetComponent<RectTransform>();
            barRect.SetParent(viewport, false);
            barRect.anchorMin = new Vector2(1f, 0f);
            barRect.anchorMax = new Vector2(1f, 1f);
            barRect.pivot = new Vector2(1f, 0.5f);
            barRect.anchoredPosition = new Vector2(-3f, 0f);
            barRect.sizeDelta = new Vector2(18f, -10f);
            barRect.SetAsLastSibling();

            Image background = barGo.GetComponent<Image>();
            background.color = new Color(0.03f, 0.25f, 0.43f, 0.65f);

            GameObject slidingGo = new GameObject("Sliding Area", typeof(RectTransform));
            RectTransform slidingRect = slidingGo.GetComponent<RectTransform>();
            slidingRect.SetParent(barRect, false);
            slidingRect.anchorMin = Vector2.zero;
            slidingRect.anchorMax = Vector2.one;
            slidingRect.offsetMin = new Vector2(2f, 2f);
            slidingRect.offsetMax = new Vector2(-2f, -2f);

            GameObject handleGo = new GameObject("Handle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform handleRect = handleGo.GetComponent<RectTransform>();
            handleRect.SetParent(slidingRect, false);
            handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one;
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;

            Image handleImage = handleGo.GetComponent<Image>();
            handleImage.color = new Color(0.15f, 0.85f, 1f, 0.95f);

            Scrollbar scrollbar = barGo.GetComponent<Scrollbar>();
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.size = 0.25f;
            scrollbar.value = 1f;
            return scrollbar;
        }

        internal static void PrepareBeforeTabs(uGUI_OptionsPanel instance)
        {
            throw new NotImplementedException();
        }
    }
}
