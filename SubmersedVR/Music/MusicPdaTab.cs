using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SubmersedVR.Music
{
    internal class RadioPdaTab : uGUI_PDATab
    {
        internal const PDATab RadioTabId = (PDATab)100;

        private MusicPdaPanel panel;

        public override void Open()
        {
            base.Open();

            Mod.logger.LogInfo("[RADIO TAB] Open");
            panel?.Show();
        }

        public override void Close()
        {
            panel?.Hide();
            Mod.logger.LogInfo("[RADIO TAB] Close");

            base.Close();
        }

        private void InitializePanel(uGUI_PDA pda)
        {
            if (panel != null)
            {
                return;
            }

            panel = MusicPdaPanel.Create(pda, transform);
            panel.Hide();
        }

        [HarmonyPatch(typeof(uGUI_PDA), nameof(uGUI_PDA.SetTabs))]
        private static class AddRadioPdaTab
        {
            private static readonly FieldInfo TabsField = AccessTools.Field(typeof(uGUI_PDA), "tabs");
            private static readonly FieldInfo CurrentTabsField = AccessTools.Field(typeof(uGUI_PDA), "currentTabs");
            private static readonly FieldInfo ToolbarTooltipsField = AccessTools.Field(typeof(uGUI_PDA), "toolbarTooltips");

            public static void Prefix(uGUI_PDA __instance, List<PDATab> tabs)
            {
                // The game deliberately uses SetTabs(null) for special PDA states
                // such as Time Capsule. Never inject our normal tab there.
                if (tabs == null)
                {
                    return;
                }

                Dictionary<PDATab, uGUI_PDATab> registeredTabs =
                    TabsField?.GetValue(__instance) as Dictionary<PDATab, uGUI_PDATab>;

                if (registeredTabs == null)
                {
                    Mod.logger.LogWarning("[RADIO TAB] uGUI_PDA.tabs was not found");
                    return;
                }

                if (!registeredTabs.TryGetValue(RadioTabId, out uGUI_PDATab existingTab))
                {
                    uGUI_PDATab inventoryTab = __instance.GetTab(PDATab.Inventory);
                    RectTransform inventoryRect = inventoryTab != null
                        ? inventoryTab.GetComponent<RectTransform>()
                        : null;

                    if (inventoryTab == null || inventoryRect == null)
                    {
                        Mod.logger.LogWarning("[RADIO TAB] Inventory tab/RectTransform was not found");
                        return;
                    }

                    GameObject go = new GameObject(
                        "RadioTab",
                        typeof(RectTransform),
                        typeof(RadioPdaTab)
                    );

                    RectTransform radioRect = go.GetComponent<RectTransform>();
                    radioRect.SetParent(inventoryRect.parent, false);
                    CopyRect(inventoryRect, radioRect);

                    RadioPdaTab radioTab = go.GetComponent<RadioPdaTab>();
                    radioTab.Register(__instance);
                    radioTab.InitializePanel(__instance);

                    registeredTabs.Add(RadioTabId, radioTab);

                    Mod.logger.LogInfo("[RADIO TAB] Registered as PDATab 100");
                }
                else if (existingTab is RadioPdaTab radioTab)
                {
                    radioTab.InitializePanel(__instance);
                }

                if (!tabs.Contains(RadioTabId))
                {
                    tabs.Add(RadioTabId);
                    Mod.logger.LogInfo("[RADIO TAB] Added to current tabs");
                }
            }

            public static void Postfix(uGUI_PDA __instance, List<PDATab> tabs)
            {
                if (tabs == null)
                {
                    return;
                }

                // CacheToolbarTooltips() has already run inside the original SetTabs().
                // Replace the fallback "Tab100" label with the user-facing tab name.
                List<PDATab> currentTabs = CurrentTabsField?.GetValue(__instance) as List<PDATab>;
                List<string> tooltips = ToolbarTooltipsField?.GetValue(__instance) as List<string>;

                if (currentTabs == null || tooltips == null)
                {
                    return;
                }

                int index = currentTabs.IndexOf(RadioTabId);
                if (index >= 0 && index < tooltips.Count)
                {
                    tooltips[index] = "Radio";
                }
            }
        }

        [HarmonyPatch(typeof(Language), nameof(Language.Get), new[] { typeof(string) })]
        private static class RadioTabNamePatch
        {
            public static void Postfix(string __0, ref string __result)
            {
                if (__0 == "Tab100")
                {
                    __result = "Radio";
                }
            }
        }

        private static Sprite radioTabSprite;
        private const int RadioTabIconSize = 128;

        // The stock SetTabs() asks SpriteManager for "Tab100". Provide a custom
        // RADIO icon that matches the PDA style better than the temporary Log-book fallback.
        [HarmonyPatch(
            typeof(SpriteManager),
            nameof(SpriteManager.Get),
            new[]
            {
                typeof(SpriteManager.Group),
                typeof(string),
                typeof(Sprite)
            }
        )]
        private static class RadioTabIconPatch
        {
            public static void Postfix(string __1, ref Sprite __result)
            {
                if (__1 == "Tab100")
                {
                    __result = GetOrCreateRadioTabSprite();
                }
            }
        }

        private static Sprite GetOrCreateRadioTabSprite()
        {
            if (radioTabSprite == null)
            {
                radioTabSprite = CreateRadioTabSprite();
            }

            return radioTabSprite;
        }

        private static Sprite CreateRadioTabSprite()
        {
            Texture2D texture = new Texture2D(RadioTabIconSize, RadioTabIconSize, TextureFormat.RGBA32, false);
            texture.name = "RadioTabIconTexture";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

            Color32[] pixels = new Color32[RadioTabIconSize * RadioTabIconSize];
            Color32 transparent = new Color32(0, 0, 0, 0);

            float feather = 1.5f / RadioTabIconSize;

            for (int y = 0; y < RadioTabIconSize; y++)
            {
                for (int x = 0; x < RadioTabIconSize; x++)
                {
                    float sampleX = ((x + 0.5f) / RadioTabIconSize) * 2f - 1f;
                    float sampleY = ((y + 0.5f) / RadioTabIconSize) * 2f - 1f;

                    float alpha = 0f;
                    alpha = Mathf.Max(alpha, GetPlayTriangleAlpha(sampleX, sampleY, feather));
                    alpha = Mathf.Max(alpha, GetRadioWaveAlpha(sampleX, sampleY, 0.00f, 0.00f, 0.34f, 0.10f, 52f, feather));
                    alpha = Mathf.Max(alpha, GetRadioWaveAlpha(sampleX, sampleY, 0.00f, 0.00f, 0.54f, 0.10f, 52f, feather));

                    if (alpha <= 0f)
                    {
                        pixels[y * RadioTabIconSize + x] = transparent;
                    }
                    else
                    {
                        byte value = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255);
                        pixels[y * RadioTabIconSize + x] = new Color32(74, 218, 239, value);
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0, 0, RadioTabIconSize, RadioTabIconSize),
                new Vector2(0.5f, 0.5f),
                100f
            );
            sprite.name = "RadioTabIconSprite";
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }

        private static float GetPlayTriangleAlpha(float sampleX, float sampleY, float feather)
        {
            Vector2 a = new Vector2(-0.58f, -0.34f);
            Vector2 b = new Vector2(-0.58f, 0.34f);
            Vector2 c = new Vector2(-0.02f, 0.00f);
            Vector2 point = new Vector2(sampleX, sampleY);

            if (IsPointInsideTriangle(point, a, b, c))
            {
                return 1f;
            }

            float distance = Mathf.Min(
                DistanceToSegment(point, a, b),
                Mathf.Min(
                    DistanceToSegment(point, b, c),
                    DistanceToSegment(point, c, a)
                )
            );

            return Mathf.Clamp01(1f - distance / feather);
        }

        private static bool IsPointInsideTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            bool hasNeg = false;
            bool hasPos = false;

            float d1 = TriangleSign(point, a, b);
            float d2 = TriangleSign(point, b, c);
            float d3 = TriangleSign(point, c, a);

            hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
            hasPos = d1 > 0f || d2 > 0f || d3 > 0f;

            return !(hasNeg && hasPos);
        }

        private static float TriangleSign(Vector2 point1, Vector2 point2, Vector2 point3)
        {
            return (point1.x - point3.x) * (point2.y - point3.y)
                - (point2.x - point3.x) * (point1.y - point3.y);
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 segment = b - a;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared <= Mathf.Epsilon)
            {
                return Vector2.Distance(point, a);
            }

            float t = Mathf.Clamp01(Vector2.Dot(point - a, segment) / lengthSquared);
            Vector2 projection = a + segment * t;
            return Vector2.Distance(point, projection);
        }

        private static float GetRadioWaveAlpha(
            float sampleX,
            float sampleY,
            float centerX,
            float centerY,
            float radius,
            float thickness,
            float maxAngleDegrees,
            float feather)
        {
            float dx = sampleX - centerX;
            float dy = sampleY - centerY;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            float angle = Mathf.Abs(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg);

            float halfThickness = thickness * 0.5f;
            float radialDistance = Mathf.Abs(distance - radius);
            float radialAlpha = Mathf.Clamp01(1f - (radialDistance - halfThickness) / feather);
            if (radialAlpha <= 0f)
            {
                return 0f;
            }

            const float angleFeather = 6f;
            float angleAlpha = Mathf.Clamp01(1f - (angle - maxAngleDegrees) / angleFeather);
            return radialAlpha * angleAlpha;
        }

        private static void CopyRect(RectTransform source, RectTransform destination)
        {
            destination.anchorMin = source.anchorMin;
            destination.anchorMax = source.anchorMax;
            destination.pivot = source.pivot;
            destination.anchoredPosition = source.anchoredPosition;
            destination.sizeDelta = source.sizeDelta;
            destination.localScale = source.localScale;
            destination.localRotation = source.localRotation;
        }
    }

    /// <summary>
    /// Visual content of the native RADIO PDA tab.
    /// The UI is built directly inside RadioTab from cloned stock PDA controls.
    /// No opaque overlay, uGUI_Dialog or fake launcher is used.
    /// </summary>
    internal sealed class MusicPdaPanel : MonoBehaviour
    {
        private uGUI_PDA pda;
        private GameObject contentRoot;

        private TextMeshProUGUI stationValue;
        private TextMeshProUGUI trackValue;
        private TextMeshProUGUI playPauseCaption;
        private TextMeshProUGUI shuffleButtonLabel;
        private TextMeshProUGUI volumeValue;

        private Button playPauseButton;

        private float nextRefreshTime;

        public static MusicPdaPanel Create(uGUI_PDA pda, Transform parent)
        {
            MusicPdaPanel controller = parent.gameObject.AddComponent<MusicPdaPanel>();
            controller.pda = pda;
            controller.Build();
            return controller;
        }

        public void Show()
        {
            if (contentRoot == null)
            {
                return;
            }

            contentRoot.SetActive(true);
            RefreshLabels();
        }

        public void Hide()
        {
            if (contentRoot != null)
            {
                contentRoot.SetActive(false);
            }
        }

        private void Build()
        {
            CreateNativeContent();
            RefreshLabels();
        }

        private void Update()
        {
            if (contentRoot == null ||
                !contentRoot.activeSelf ||
                Time.unscaledTime < nextRefreshTime)
            {
                return;
            }

            nextRefreshTime = Time.unscaledTime + 0.25f;
            RefreshLabels();
        }

        private void CreateNativeContent()
        {
            Transform galleryRoot = pda.tabGallery != null ? pda.tabGallery.transform : null;
            Transform logRoot = pda.tabLog != null ? pda.tabLog.transform : null;

            if (galleryRoot == null || logRoot == null)
            {
                throw new InvalidOperationException(
                    "Could not find Gallery/Log PDA tabs used as native RADIO UI templates."
                );
            }

            Transform titleTemplate = FindRequired(
                galleryRoot,
                "Content/GalleryLabel"
            );

            Transform separatorTemplate = FindRequired(
                galleryRoot,
                "Content/Thumbnails/Separator"
            );

            Transform textButtonTemplate = FindRequired(
                galleryRoot,
                "Content/FullScreen/ButtonBack"
            );

            Transform playButtonTemplate = FindRequired(
                logRoot,
                "Content/ScrollView/Viewport/ScrollCanvas/LogEntry(Clone)/ButtonContainer/Button"
            );

            Transform captionTemplate = FindRequired(
                logRoot,
                "Content/ScrollView/Viewport/ScrollCanvas/LogGroupLabel(Clone)"
            );

            Transform valueTemplate = FindRequired(
                logRoot,
                "Content/ScrollView/Viewport/ScrollCanvas/LogEntry(Clone)/Text"
            );

            contentRoot = new GameObject(
                "Content",
                typeof(RectTransform)
            );

            RectTransform contentRect = contentRoot.GetComponent<RectTransform>();
            contentRect.SetParent(transform, false);
            Stretch(contentRect);

            CreateTitle(titleTemplate, contentRoot.transform);
            CreateSeparator(separatorTemplate, contentRoot.transform, new Vector2(1f, 283f));
            CreateSeparator(separatorTemplate, contentRoot.transform, new Vector2(1f, -429f));

            CreateMetadata(
                captionTemplate,
                valueTemplate,
                contentRoot.transform
            );

            CreatePlaybackControls(
                textButtonTemplate,
                playButtonTemplate,
                valueTemplate,
                contentRoot.transform
            );

            CreateStationControls(
                textButtonTemplate,
                contentRoot.transform
            );

            CreateUtilityControls(
                textButtonTemplate,
                valueTemplate,
                contentRoot.transform
            );

            contentRoot.SetActive(false);

            Mod.logger.LogInfo(
                "[RADIO TAB] Native PDA content built from Gallery/Log controls"
            );
        }

        private void CreateTitle(Transform template, Transform parent)
        {
            GameObject title = Instantiate(
                template.gameObject,
                parent,
                false
            );

            title.name = "RadioLabel";
            title.SetActive(true);

            RectTransform rect = title.GetComponent<RectTransform>();
            rect.anchoredPosition = new Vector2(0f, 329.5f);

            TextMeshProUGUI text = title.GetComponent<TextMeshProUGUI>();
            if (text != null)
            {
                text.text = "RADIO";
            }
        }

        private void CreateSeparator(
            Transform template,
            Transform parent,
            Vector2 position)
        {
            GameObject separator = Instantiate(
                template.gameObject,
                parent,
                false
            );

            separator.name = "RadioSeparator";
            separator.SetActive(true);

            RectTransform rect = separator.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(994f, 4f);
        }

        private void CreateMetadata(
            Transform captionTemplate,
            Transform valueTemplate,
            Transform parent)
        {
            CreateText(
                captionTemplate,
                parent,
                "StationCaption",
                "STATION",
                new Vector2(-430f, 185f),
                new Vector2(180f, 42f),
                TextAlignmentOptions.Left
            );

            stationValue = CreateText(
                valueTemplate,
                parent,
                "StationValue",
                "",
                new Vector2(85f, 185f),
                new Vector2(820f, 48f),
                TextAlignmentOptions.Left
            );

            CreateText(
                captionTemplate,
                parent,
                "TrackCaption",
                "TRACK",
                new Vector2(-430f, 120f),
                new Vector2(180f, 42f),
                TextAlignmentOptions.Left
            );

            trackValue = CreateText(
                valueTemplate,
                parent,
                "TrackValue",
                "",
                new Vector2(85f, 120f),
                new Vector2(820f, 48f),
                TextAlignmentOptions.Left
            );

        }

        private void CreatePlaybackControls(
            Transform textButtonTemplate,
            Transform playButtonTemplate,
            Transform valueTemplate,
            Transform parent)
        {
            CreateTextButton(
                textButtonTemplate,
                parent,
                "PreviousTrack",
                "< TRACK",
                new Vector2(-260f, -70f),
                new Vector2(190f, 60f),
                () => Player?.PlayPrevious(),
                out _
            );

            playPauseButton = CreateIconButton(
                playButtonTemplate,
                parent,
                "PlayPause",
                new Vector2(0f, -70f),
                () => Player?.TogglePlayPause()
            );

            CreateTextButton(
                textButtonTemplate,
                parent,
                "NextTrack",
                "TRACK >",
                new Vector2(260f, -70f),
                new Vector2(190f, 60f),
                () => Player?.PlayNext(),
                out _
            );

            playPauseCaption = CreateText(
                valueTemplate,
                parent,
                "PlayPauseCaption",
                "PLAY",
                new Vector2(0f, -127f),
                new Vector2(150f, 30f),
                TextAlignmentOptions.Center
            );

            playPauseCaption.fontSize = 18f;
        }

        private void CreateStationControls(
            Transform textButtonTemplate,
            Transform parent)
        {
            CreateTextButton(
                textButtonTemplate,
                parent,
                "PreviousStation",
                "< STATION",
                new Vector2(-190f, -180f),
                new Vector2(220f, 60f),
                () => Player?.PreviousStation(),
                out _
            );

            CreateTextButton(
                textButtonTemplate,
                parent,
                "NextStation",
                "STATION >",
                new Vector2(190f, -180f),
                new Vector2(220f, 60f),
                () => Player?.NextStation(),
                out _
            );
        }

        private void CreateUtilityControls(
            Transform textButtonTemplate,
            Transform valueTemplate,
            Transform parent)
        {
            CreateTextButton(
                textButtonTemplate,
                parent,
                "Stop",
                "STOP",
                new Vector2(-260f, -270f),
                new Vector2(180f, 60f),
                () => Player?.StopRadio(),
                out _
            );

            CreateTextButton(
                textButtonTemplate,
                parent,
                "Shuffle",
                "SHUFFLE",
                new Vector2(0f, -270f),
                new Vector2(210f, 60f),
                ToggleShuffle,
                out shuffleButtonLabel
            );

            CreateTextButton(
                textButtonTemplate,
                parent,
                "Rescan",
                "RESCAN",
                new Vector2(260f, -270f),
                new Vector2(180f, 60f),
                () => Player?.Rescan(),
                out _
            );

            CreateTextButton(
                textButtonTemplate,
                parent,
                "VolumeDown",
                "VOL -",
                new Vector2(-180f, -340f),
                new Vector2(150f, 50f),
                () => AdjustVolume(-0.05f),
                out _
            );

            volumeValue = CreateText(
                valueTemplate,
                parent,
                "VolumeValue",
                "VOLUME: 100%",
                new Vector2(0f, -340f),
                new Vector2(190f, 42f),
                TextAlignmentOptions.Center
            );

            volumeValue.fontSize = 18f;

            CreateTextButton(
                textButtonTemplate,
                parent,
                "VolumeUp",
                "VOL +",
                new Vector2(180f, -340f),
                new Vector2(150f, 50f),
                () => AdjustVolume(0.05f),
                out _
            );

        }

        private TextMeshProUGUI CreateText(
            Transform template,
            Transform parent,
            string name,
            string initialText,
            Vector2 position,
            Vector2 size,
            TextAlignmentOptions alignment)
        {
            GameObject go = Instantiate(
                template.gameObject,
                parent,
                false
            );

            go.name = $"Radio_{name}";
            go.SetActive(true);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;

            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            if (text == null)
            {
                throw new InvalidOperationException(
                    $"Native RADIO text template '{template.name}' has no TextMeshProUGUI."
                );
            }

            text.text = initialText;
            text.alignment = alignment;
            text.raycastTarget = false;

            return text;
        }

        private Button CreateTextButton(
            Transform template,
            Transform parent,
            string name,
            string initialText,
            Vector2 position,
            Vector2 size,
            Action action,
            out TextMeshProUGUI label)
        {
            GameObject go = Instantiate(
                template.gameObject,
                parent,
                false
            );

            go.name = $"Radio_{name}";
            go.SetActive(true);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;

            Transform arrow = go.transform.Find("Arrow");
            if (arrow != null)
            {
                arrow.gameObject.SetActive(false);
            }

            label = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null)
            {
                throw new InvalidOperationException(
                    $"Native RADIO button template '{template.name}' has no TextMeshProUGUI."
                );
            }

            label.text = initialText;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;

            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.pivot = new Vector2(0.5f, 0.5f);
            labelRect.anchoredPosition = Vector2.zero;
            labelRect.sizeDelta = new Vector2(-20f, 0f);

            Button button = go.GetComponent<Button>();
            if (button == null)
            {
                throw new InvalidOperationException(
                    $"Native RADIO button template '{template.name}' has no Button."
                );
            }

            // Replace the cloned UnityEvent itself, not just runtime listeners. Native PDA
            // templates can contain persistent Inspector callbacks that RemoveAllListeners()
            // deliberately leaves intact.
            button.onClick = new Button.ButtonClickedEvent();
            button.interactable = true;

            if (action != null)
            {
                button.onClick.AddListener(() => action());
            }

            return button;
        }

        private Button CreateIconButton(
            Transform template,
            Transform parent,
            string name,
            Vector2 position,
            Action action)
        {
            GameObject go = Instantiate(
                template.gameObject,
                parent,
                false
            );

            go.name = $"Radio_{name}";
            go.SetActive(true);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(80f, 78f);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;

            Button button = go.GetComponent<Button>();
            if (button == null)
            {
                throw new InvalidOperationException(
                    $"Native RADIO icon template '{template.name}' has no Button."
                );
            }

            // The native PDA icon template carries its own uGUI_ButtonSound. For the
            // RADIO Play/Pause button that stock sound is unrelated to music playback
            // and can trigger PDA voice lines, so explicitly strip it from the clone.
            uGUI_ButtonSound buttonSound = go.GetComponent<uGUI_ButtonSound>();
            if (buttonSound != null)
            {
                buttonSound.enabled = false;
                Destroy(buttonSound);
            }

            // Replace the cloned UnityEvent itself, not just runtime listeners. Native PDA
            // templates can contain persistent Inspector callbacks that RemoveAllListeners()
            // deliberately leaves intact.
            button.onClick = new Button.ButtonClickedEvent();
            button.interactable = true;

            if (action != null)
            {
                button.onClick.AddListener(() => action());
            }

            return button;
        }

        private static Transform FindRequired(Transform root, string path)
        {
            Transform result = root.Find(path);
            if (result == null)
            {
                throw new InvalidOperationException(
                    $"Could not find native PDA UI template '{root.name}/{path}'."
                );
            }

            return result;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
        }

        private MusicPlayerController Player => MusicPlayerController.instance;

        private void ToggleShuffle()
        {
            MusicPlayerSettings.Shuffle = !MusicPlayerSettings.Shuffle;
            RefreshLabels();
        }

        private void AdjustVolume(float delta)
        {
            float volume = Mathf.Clamp01(
                MusicPlayerSettings.Volume + delta
            );

            MusicPlayerSettings.Volume = volume;
            Player?.SetVolume(volume);
            RefreshLabels();
        }

        private void RefreshLabels()
        {
            if (stationValue == null)
            {
                return;
            }

            MusicPlayerController player = Player;
            if (player == null)
            {
                stationValue.text = "Initializing...";
                trackValue.text = "";
                playPauseCaption.text = "PLAY";
                shuffleButtonLabel.text =
                    $"SHUFFLE: {(MusicPlayerSettings.Shuffle ? "ON" : "OFF")}";
                volumeValue.text =
                    $"VOLUME: {Mathf.RoundToInt(MusicPlayerSettings.Volume * 100f)}%";
                return;
            }

            stationValue.text = player.CurrentStationName;
            trackValue.text = player.CurrentTrackName;

            playPauseCaption.text =
                !player.IsRadioActive || player.IsPaused
                    ? "PLAY"
                    : "PAUSE";

            shuffleButtonLabel.text =
                $"SHUFFLE: {(MusicPlayerSettings.Shuffle ? "ON" : "OFF")}";

            volumeValue.text =
                $"VOLUME: {Mathf.RoundToInt(MusicPlayerSettings.Volume * 100f)}%";
        }

    }

    // Keep the runtime diagnostics for now. They are intentionally read-only and
    // can be removed once the native RADIO tab has survived a few real sessions.
    internal static class MusicPdaDiagnostics
    {
        [HarmonyPatch(typeof(uGUI_Toolbar), nameof(uGUI_Toolbar.Initialize))]
        private static class PdaToolbarInitializeDebug
        {
            public static void Prefix(
                uGUI_IToolbarManager manager,
                UnityEngine.Object[] content,
                Font font,
                int fontSize)
            {
                // uGUI_Toolbar is shared by other game UI. Only dump the PDA toolbar.
                if (!(manager is uGUI_PDA))
                {
                    return;
                }

                Mod.logger.LogInfo("========== TOOLBAR INITIALIZE ==========");
                Mod.logger.LogInfo($"manager = {manager.GetType().FullName}");

                if (content == null)
                {
                    Mod.logger.LogInfo("content = NULL");
                }
                else
                {
                    Mod.logger.LogInfo($"content.Length = {content.Length}");

                    for (int i = 0; i < content.Length; i++)
                    {
                        UnityEngine.Object item = content[i];
                        Mod.logger.LogInfo(
                            item == null
                                ? $"content[{i}] = NULL"
                                : $"content[{i}] = {item.GetType().FullName} | name={item.name} | value={item}"
                        );
                    }
                }

                Mod.logger.LogInfo($"font = {(font == null ? "NULL" : font.name)}");
                Mod.logger.LogInfo($"fontSize = {fontSize}");
                Mod.logger.LogInfo("========================================");
            }
        }

        [HarmonyPatch(typeof(uGUI_PDA), nameof(uGUI_PDA.SetTabs))]
        private static class PdaSetTabsDebug
        {
            public static void Postfix(List<PDATab> tabs)
            {
                if (tabs == null)
                {
                    Mod.logger.LogInfo("[PDA SETTABS] NULL");
                    return;
                }

                Mod.logger.LogInfo("[PDA SETTABS] " + string.Join(", ", tabs));
            }
        }

        [HarmonyPatch(typeof(uGUI_PDA), nameof(uGUI_PDA.OpenTab))]
        private static class PdaOpenTabDebug
        {
            public static void Prefix(PDATab tabId)
            {
                Mod.logger.LogInfo($"[PDA OPENTAB] {tabId} ({(int)tabId})");
            }
        }

        [HarmonyPatch(typeof(uGUI_PDA), nameof(uGUI_PDA.OnOpenPDA))]
        private static class PdaOpenDebug
        {
            public static void Prefix(PDATab tabId)
            {
                Mod.logger.LogInfo($"[PDA ONOPEN] requested={tabId} ({(int)tabId})");
            }
        }
    }

    [HarmonyPatch(typeof(uGUI_PDA), nameof(uGUI_PDA.OnOpenPDA))]
    static class PdaNativeUiTreeDebug
    {
        private static bool dumped;

        public static void Postfix(uGUI_PDA __instance)
        {
            if (dumped || __instance == null)
            {
                return;
            }

            dumped = true;

            Mod.logger.LogInfo("========== PDA NATIVE UI TREE ==========");

            DumpTab("PING TAB", __instance.tabPing);
            DumpTab("GALLERY TAB", __instance.tabGallery);
            DumpTab("LOG TAB", __instance.tabLog);

            Mod.logger.LogInfo("========================================");
        }

        private static void DumpTab(string title, object tabObject)
        {
            Mod.logger.LogInfo("");
            Mod.logger.LogInfo($"===== {title} =====");

            GameObject root = AsGameObject(tabObject);

            if (root == null)
            {
                Mod.logger.LogWarning(
                    $"[PDA UI DEBUG] Could not resolve GameObject for {title}"
                );
                return;
            }

            DumpGameObject(root, 0);
        }

        private static void DumpGameObject(GameObject go, int depth)
        {
            if (go == null)
            {
                return;
            }

            string indent = new string(' ', depth * 2);

            Mod.logger.LogInfo(
                $"{indent}[GO] {go.name} | " +
                $"activeSelf={go.activeSelf} | " +
                $"activeInHierarchy={go.activeInHierarchy}"
            );

            RectTransform rect = go.GetComponent<RectTransform>();

            if (rect != null)
            {
                Mod.logger.LogInfo(
                    $"{indent}  [RectTransform] " +
                    $"anchorMin={Format(rect.anchorMin)} | " +
                    $"anchorMax={Format(rect.anchorMax)} | " +
                    $"pivot={Format(rect.pivot)} | " +
                    $"anchoredPosition={Format(rect.anchoredPosition)} | " +
                    $"sizeDelta={Format(rect.sizeDelta)} | " +
                    $"localScale={Format(rect.localScale)}"
                );
            }

            Image image = go.GetComponent<Image>();

            if (image != null)
            {
                string spriteName =
                    image.sprite == null
                        ? "NULL"
                        : image.sprite.name;

                Mod.logger.LogInfo(
                    $"{indent}  [Image] " +
                    $"sprite={spriteName} | " +
                    $"color={Format(image.color)} | " +
                    $"raycastTarget={image.raycastTarget} | " +
                    $"type={image.type}"
                );
            }

            Button button = go.GetComponent<Button>();

            if (button != null)
            {
                string targetGraphicName =
                    button.targetGraphic == null
                        ? "NULL"
                        : button.targetGraphic.gameObject.name;

                Mod.logger.LogInfo(
                    $"{indent}  [Button] " +
                    $"interactable={button.interactable} | " +
                    $"transition={button.transition} | " +
                    $"targetGraphic={targetGraphicName}"
                );
            }

            TextMeshProUGUI tmp = go.GetComponent<TextMeshProUGUI>();

            if (tmp != null)
            {
                string fontName =
                    tmp.font == null
                        ? "NULL"
                        : tmp.font.name;

                Mod.logger.LogInfo(
                    $"{indent}  [TextMeshProUGUI] " +
                    $"text=\"{Sanitize(tmp.text)}\" | " +
                    $"font={fontName} | " +
                    $"fontSize={tmp.fontSize} | " +
                    $"alignment={tmp.alignment} | " +
                    $"color={Format(tmp.color)} | " +
                    $"raycastTarget={tmp.raycastTarget}"
                );
            }

            Text legacyText = go.GetComponent<Text>();

            if (legacyText != null)
            {
                string fontName =
                    legacyText.font == null
                        ? "NULL"
                        : legacyText.font.name;

                Mod.logger.LogInfo(
                    $"{indent}  [Text] " +
                    $"text=\"{Sanitize(legacyText.text)}\" | " +
                    $"font={fontName} | " +
                    $"fontSize={legacyText.fontSize} | " +
                    $"alignment={legacyText.alignment} | " +
                    $"color={Format(legacyText.color)} | " +
                    $"raycastTarget={legacyText.raycastTarget}"
                );
            }

            Component[] components = go.GetComponents<Component>();

            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                Type type = component.GetType();

                if (type == typeof(Transform) ||
                    type == typeof(RectTransform) ||
                    type == typeof(Image) ||
                    type == typeof(Button) ||
                    type == typeof(TextMeshProUGUI) ||
                    type == typeof(Text))
                {
                    continue;
                }

                if (type.Name.StartsWith("uGUI_", StringComparison.OrdinalIgnoreCase))
                {
                    Mod.logger.LogInfo(
                        $"{indent}  [Component] {type.FullName}"
                    );
                }
            }

            Transform transform = go.transform;

            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);

                if (child != null)
                {
                    DumpGameObject(child.gameObject, depth + 1);
                }
            }
        }

        private static GameObject AsGameObject(object value)
        {
            if (value is GameObject go)
            {
                return go;
            }

            if (value is Component component)
            {
                return component.gameObject;
            }

            return null;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            return value
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string Format(Vector2 value)
        {
            return $"({value.x:0.##},{value.y:0.##})";
        }

        private static string Format(Vector3 value)
        {
            return $"({value.x:0.##},{value.y:0.##},{value.z:0.##})";
        }

        private static string Format(Color value)
        {
            return $"({value.r:0.###},{value.g:0.###},{value.b:0.###},{value.a:0.###})";
        }
    }
}
