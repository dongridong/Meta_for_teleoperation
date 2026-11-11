using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HandTrackingViz
{
    // ====================================================================
    // Control panel building blocks (uGUI, world space).
    //
    // Everything is built in code — like StatusIndicator — so the feature
    // needs no prefab and no scene edit, and both APKs get it from the same
    // PoseFramePublisher that already owns the HUD.
    //
    // Layout units: the panel canvas is scaled so that 1 unit = 0.1 mm. All
    // positions/sizes below are in those units, anchored bottom-left.
    // uGUI's EventSystem is NOT used: input comes from PanelPointer, which
    // hit-tests PanelButton rects directly (fingertip poke, controller ray).
    //
    // Text is TextMeshPro (SDF), not legacy uGUI Text. Legacy text is a
    // bitmap rasterised at fontSize pixels and then shrunk many times over on
    // a head-locked panel, which reads as broken dots in the headset; SDF
    // stays sharp at any size and distance. The font is TMP's default
    // (LiberationSans SDF, from Assets/TextMesh Pro).
    //
    // Shapes are rounded with one procedural circle sprite, 9-sliced so the
    // same texture gives pills, rounded plates and round knobs.
    // ====================================================================
    internal static class PanelUi
    {
        public static RectTransform Rect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>A filled rectangle; <paramref name="radius"/> &gt; 0 rounds its corners.</summary>
        public static Image Box(string name, Transform parent, Vector2 position, Vector2 size,
                                Color color, float radius = 0f)
        {
            var image = Rect(name, parent, position, size).gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            if (radius > 0f) Round(image, radius);
            return image;
        }

        /// <summary>A filled circle of the given diameter.</summary>
        public static Image Circle(string name, Transform parent, Vector2 position, float diameter, Color color)
        {
            var image = Rect(name, parent, position, new Vector2(diameter, diameter)).gameObject.AddComponent<Image>();
            image.sprite = PanelSprites.Circle;
            image.type = Image.Type.Simple;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>Rounds an image's corners to <paramref name="radius"/> layout units.</summary>
        public static void Round(Image image, float radius)
        {
            image.sprite = PanelSprites.Circle;
            image.type = Image.Type.Sliced;
            image.fillCenter = true;
            // A sliced corner is (border px) / (sprite PPU / canvas reference PPU
            // * multiplier) units wide; both PPUs are 100, so the multiplier
            // alone sets the radius.
            image.pixelsPerUnitMultiplier = PanelSprites.CircleRadiusPx / radius;
        }

        public static TextMeshProUGUI Label(string name, Transform parent, Vector2 position, Vector2 size,
                                            float fontSize, TextAlignmentOptions alignment, Color color,
                                            string text = "", bool bold = false, float minFontSize = 0f)
        {
            var label = Rect(name, parent, position, size).gameObject.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color;
            label.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            if (minFontSize > 0f)
            {
                // Shrinks (never grows) to fit, e.g. "255.255.255.255:65535".
                label.enableAutoSizing = true;
                label.fontSizeMax = fontSize;
                label.fontSizeMin = minFontSize;
            }
            return label;
        }
    }

    /// <summary>Procedural sprites: an anti-aliased circle and a gear icon.</summary>
    internal static class PanelSprites
    {
        public const float CircleRadiusPx = 64f;
        private const int CircleSize = 128;
        private const int GearSize = 128;
        private const int Supersample = 4;

        private static Sprite s_Circle;
        private static Sprite s_Gear;

        /// <summary>White circle, 9-slice borders on all sides (see PanelUi.Round).</summary>
        public static Sprite Circle
        {
            get
            {
                if (s_Circle == null)
                {
                    float c = CircleSize * 0.5f;
                    Texture2D tex = Bake(CircleSize, (x, y) =>
                    {
                        float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                        return Mathf.Clamp01(c - d + 0.5f);   // 1 px anti-aliased edge
                    }, 1);
                    float b = CircleRadiusPx;
                    s_Circle = Sprite.Create(tex, new Rect(0, 0, CircleSize, CircleSize), new Vector2(0.5f, 0.5f),
                                             100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
                }
                return s_Circle;
            }
        }

        /// <summary>White 8-tooth gear with a centre hole.</summary>
        public static Sprite Gear
        {
            get
            {
                if (s_Gear == null)
                {
                    const int teeth = 8;
                    const float rHole = 0.30f, rBody = 0.66f, rTip = 0.94f;
                    float half = GearSize * 0.5f;
                    Texture2D tex = Bake(GearSize, (x, y) =>
                    {
                        float dx = (x - half) / half, dy = (y - half) / half;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        if (r < rHole || r > rTip) return 0f;
                        if (r <= rBody) return 1f;
                        // Trapezoid teeth: wide at the root, narrower at the tip.
                        float phase = Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI) * teeth, 1f);
                        float halfWidth = Mathf.Lerp(0.25f, 0.16f, (r - rBody) / (rTip - rBody));
                        return Mathf.Abs(phase - 0.5f) <= halfWidth ? 1f : 0f;
                    }, Supersample);
                    s_Gear = Sprite.Create(tex, new Rect(0, 0, GearSize, GearSize), new Vector2(0.5f, 0.5f), 100f);
                }
                return s_Gear;
            }
        }

        // Renders a white texture whose alpha is the shape's coverage, averaged
        // over samples x samples points per pixel (anti-aliasing for shapes
        // whose coverage function is binary). Mipmapped and trilinear: the
        // panel is seen at a fraction of the texture's size, and without mips
        // a minified edge shimmers exactly like the old text did.
        private static Texture2D Bake(int size, Func<float, float, float> coverage, int samples)
        {
            var pixels = new Color32[size * size];
            float step = 1f / samples;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float sum = 0f;
                    for (int sy = 0; sy < samples; sy++)
                        for (int sx = 0; sx < samples; sx++)
                            sum += coverage(x + (sx + 0.5f) * step, y + (sy + 0.5f) * step);
                    byte a = (byte)Mathf.RoundToInt(255f * sum / (samples * samples));
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "PanelSprite",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }
    }

    /// <summary>A pressable rectangle. PanelPointer hit-tests <see cref="Rect"/>.</summary>
    internal sealed class PanelButton
    {
        // Two hands and two controllers can all reach the panel; one physical
        // press must not register twice (e.g. finger and ray together).
        private const float RepeatGuardSeconds = 0.3f;

        public readonly RectTransform Rect;
        public readonly Image Background;
        public readonly TextMeshProUGUI Caption;
        public Action OnClick;

        private Color baseColor;
        private bool hovered;
        private float lastClickTime = -999f;

        public PanelButton(string name, Transform parent, Vector2 position, Vector2 size,
                           Color color, float radius, string caption = null, float fontSize = 64f)
        {
            Background = PanelUi.Box(name, parent, position, size, color, radius);
            Rect = Background.rectTransform;
            baseColor = color;
            if (caption != null)
                Caption = PanelUi.Label("Caption", Rect, Vector2.zero, size, fontSize,
                                        TextAlignmentOptions.Center, Color.white, caption, bold: true);
        }

        public bool Visible => Rect.gameObject.activeInHierarchy;

        public void SetColor(Color color)
        {
            baseColor = color;
            Repaint();
        }

        public void SetHovered(bool value)
        {
            if (hovered == value) return;
            hovered = value;
            Repaint();
        }

        public bool Contains(Vector3 worldPoint, float marginUnits)
        {
            Vector3 local = Rect.InverseTransformPoint(worldPoint);
            Rect r = Rect.rect;
            return local.x >= r.xMin - marginUnits && local.x <= r.xMax + marginUnits
                && local.y >= r.yMin - marginUnits && local.y <= r.yMax + marginUnits;
        }

        public void Click()
        {
            float now = Time.unscaledTime;
            if (now - lastClickTime < RepeatGuardSeconds) return;
            lastClickTime = now;
            OnClick?.Invoke();
        }

        private void Repaint()
        {
            Background.color = hovered ? Color.Lerp(baseColor, Color.white, 0.25f) : baseColor;
        }
    }

    /// <summary>An iOS-style switch: a pill track with a round knob that slides
    /// across and a track that fades to the accent colour when on.</summary>
    internal sealed class PanelToggle
    {
        private static readonly Color TrackOff = new Color(0.30f, 0.31f, 0.35f, 0.95f);
        private static readonly Color TrackOn = new Color(0.20f, 0.78f, 0.45f, 0.98f);
        private static readonly Color KnobShadow = new Color(0f, 0f, 0f, 0.35f);
        private const float KnobInset = 14f;
        private const float AnimSeconds = 0.15f;

        public readonly PanelButton Button;
        public event Action<bool> Changed;

        private readonly RectTransform knob;
        private readonly RectTransform shadow;
        private readonly Vector2 size;
        private float t;   // 0 = off position, 1 = on position (animated)

        public bool On { get; private set; }

        public PanelToggle(string name, Transform parent, Vector2 position, Vector2 size)
        {
            this.size = size;
            Button = new PanelButton(name, parent, position, size, TrackOff, size.y * 0.5f);
            float k = size.y - 2f * KnobInset;
            shadow = PanelUi.Circle("KnobShadow", Button.Rect, Vector2.zero, k + 8f, KnobShadow).rectTransform;
            knob = PanelUi.Circle("Knob", Button.Rect, Vector2.zero, k, Color.white).rectTransform;
            Button.OnClick = () => Set(!On);
            Apply();
        }

        public void Set(bool on)
        {
            if (On == on) return;
            On = on;
            Changed?.Invoke(on);
        }

        /// <summary>Advances the slide animation; called every frame.</summary>
        public void Tick(float dt)
        {
            float target = On ? 1f : 0f;
            if (Mathf.Approximately(t, target)) return;
            t = Mathf.MoveTowards(t, target, dt / AnimSeconds);
            Apply();
        }

        private void Apply()
        {
            float k = size.y - 2f * KnobInset;
            float e = t * t * (3f - 2f * t);   // smoothstep easing
            float x = Mathf.Lerp(KnobInset, size.x - KnobInset - k, e);
            knob.anchoredPosition = new Vector2(x, KnobInset);
            shadow.anchoredPosition = new Vector2(x - 4f, KnobInset - 10f);
            Button.SetColor(Color.Lerp(TrackOff, TrackOn, e));
        }
    }
}
