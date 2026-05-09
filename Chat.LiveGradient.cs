// Chat.LiveGradient.cs
// Intercepts UIChat.AddChatMessage to detect rolling-gradient and neon markers
// emitted by the TagMod server plugin, then animates/renders the relevant chat labels.
//
// Rolling gradient marker: [[G|tagText|#hex1,#hex2,...|speed[|mode]]]
//   mode "flame" — brightness flicker per char (no embers)
//   mode "ice"   — periodic freeze blend + rare random-char glint
//   mode "holo"  — gradient phase driven by local camera yaw (direction-based)
//
// Neon marker (static):   [[N|tagText|#hex]]
// Neon marker (rolling):  [[N|tagText|#c1,#c2[,...]|speed]]
// Neon marker (pulse):    [[N|tagText|#hex|pulse|speed]]
//
// Clients WITHOUT this mod see raw marker text (minor cosmetic degradation).
// Clients WITH this mod get the full animated / split-label effect.

using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoncePuck.Keybinds
{
    internal enum RenderMode { Normal, Flame, Ice, Holo, Pulse }
    internal enum NeonMode   { Static, Rolling, Pulse }

    internal static class LiveGradientSystem
    {
        private const string OPEN       = "[[G|";
        private const string CLOSE      = "]]";
        private const string NEON_OPEN  = "[[N|";
        private const string NEON_CLOSE = "]]";

        private static LiveGradientRunner _runner;

        internal static void Initialize(GameObject host)
        {
            if (_runner == null)
                _runner = host.AddComponent<LiveGradientRunner>();
        }

        internal static void Shutdown() { _runner = null; }

        // Called from the UIChat.AddChatMessage postfix with the last-rendered Label.
        internal static void TryRegister(Label label)
        {
            if (!IsEnabled()) return;
            if (label == null) return;

            string text = label.text;
            int markerStart = text.IndexOf(OPEN, StringComparison.Ordinal);
            if (markerStart < 0) return;

            int innerStart = markerStart + OPEN.Length;
            int markerEnd  = text.IndexOf(CLOSE, innerStart, StringComparison.Ordinal);
            if (markerEnd < 0) return;

            // inner = "tagText|#hex1,#hex2,...|speed[|mode]"
            string inner = text.Substring(innerStart, markerEnd - innerStart);
            int sep1 = inner.IndexOf('|');
            if (sep1 < 0) return;
            int sep2 = inner.IndexOf('|', sep1 + 1);
            if (sep2 < 0) return;
            int sep3 = inner.IndexOf('|', sep2 + 1); // optional mode field

            string tagText   = inner.Substring(0, sep1);
            string colorsRaw = inner.Substring(sep1 + 1, sep2 - sep1 - 1);
            string speedRaw  = sep3 < 0
                ? inner.Substring(sep2 + 1)
                : inner.Substring(sep2 + 1, sep3 - sep2 - 1);
            string modeRaw   = sep3 >= 0 ? inner.Substring(sep3 + 1).Trim() : null;

            string[] hexes = colorsRaw.Split(',');
            float speed;
            if (!float.TryParse(speedRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out speed))
                speed = 0.1f;

            var colors = new Color[hexes.Length];
            for (int i = 0; i < hexes.Length; i++)
            {
                Color c;
                if (!ColorUtility.TryParseHtmlString(hexes[i].Trim(), out c)) return;
                colors[i] = c;
            }
            if (colors.Length < 2) return;

            RenderMode mode = RenderMode.Normal;
            if (modeRaw != null)
            {
                if      (modeRaw.Equals("flame", StringComparison.OrdinalIgnoreCase)) mode = RenderMode.Flame;
                else if (modeRaw.Equals("ice",   StringComparison.OrdinalIgnoreCase)) mode = RenderMode.Ice;
                else if (modeRaw.Equals("holo",  StringComparison.OrdinalIgnoreCase)) mode = RenderMode.Holo;
                else if (modeRaw.Equals("pulse", StringComparison.OrdinalIgnoreCase)) mode = RenderMode.Pulse;
            }

            string prefix = text.Substring(0, markerStart);
            string suffix = text.Substring(markerEnd + CLOSE.Length);

            var entry = new GradEntry(label, prefix, suffix, tagText, colors, speed, mode);
            label.text = entry.Render(Time.unscaledTime);
            label.enableRichText = true;
            _runner?.Add(entry);
        }

        // Handles [[N|...]] neon markers — replaces the Label with a flex-row container:
        //   tag sub-label (white fill + CSS outline) + rest sub-label (normal text).
        // Three neon variants detected from field count:
        //   static   [[N|text|#hex]]                → single color, no animation
        //   rolling  [[N|text|#c1,#c2[,...]|speed]] → colors field contains ','
        //   pulse    [[N|text|#hex|pulse|speed]]     → keyword "pulse" in field 3
        internal static void TryRegisterNeon(Label label)
        {
            if (!IsEnabled()) return;
            if (label == null) return;

            string text = label.text;
            int markerStart = text.IndexOf(NEON_OPEN, StringComparison.Ordinal);
            if (markerStart < 0) return;

            int innerStart = markerStart + NEON_OPEN.Length;
            int markerEnd  = text.IndexOf(NEON_CLOSE, innerStart, StringComparison.Ordinal);
            if (markerEnd < 0) return;

            string inner = text.Substring(innerStart, markerEnd - innerStart);
            int sep1 = inner.IndexOf('|');
            if (sep1 < 0) return;
            int sep2 = inner.IndexOf('|', sep1 + 1);

            string tagText = inner.Substring(0, sep1);
            Color   outlineColor;
            Color[] rollingColors = null;
            float   speed         = 1.0f;
            NeonMode neonMode     = NeonMode.Static;

            if (sep2 < 0)
            {
                // Static: "tagText|#hex"
                if (!ColorUtility.TryParseHtmlString(inner.Substring(sep1 + 1).Trim(), out outlineColor)) return;
            }
            else
            {
                string field2    = inner.Substring(sep1 + 1, sep2 - sep1 - 1);
                string field3rest = inner.Substring(sep2 + 1);

                if (field2.Contains(","))
                {
                    // Rolling: "tagText|#c1,#c2[,#c3...]|speed"
                    string[] hexes = field2.Split(',');
                    if (hexes.Length < 2) return;
                    rollingColors = new Color[hexes.Length];
                    for (int i = 0; i < hexes.Length; i++)
                    {
                        Color c;
                        if (!ColorUtility.TryParseHtmlString(hexes[i].Trim(), out c)) return;
                        rollingColors[i] = c;
                    }
                    if (!float.TryParse(field3rest.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out speed))
                        speed = 0.6f;
                    outlineColor = rollingColors[0];
                    neonMode = NeonMode.Rolling;
                }
                else
                {
                    // Pulse: "tagText|#hex|pulse[|speed]" — field3rest = "pulse[|speed]"
                    if (!ColorUtility.TryParseHtmlString(field2.Trim(), out outlineColor)) return;
                    int sep3 = field3rest.IndexOf('|');
                    string keyword  = (sep3 < 0 ? field3rest : field3rest.Substring(0, sep3)).Trim();
                    string speedStr = sep3 >= 0 ? field3rest.Substring(sep3 + 1).Trim() : "1";
                    if (!keyword.Equals("pulse", StringComparison.OrdinalIgnoreCase)) return;
                    if (!float.TryParse(speedStr, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out speed))
                        speed = 1.0f;
                    rollingColors = new[] { outlineColor }; // [0] = base color for pulse
                    neonMode = NeonMode.Pulse;
                }
            }

            // Determine wrap style from prefix
            string prefix = text.Substring(0, markerStart);
            bool isLegacy = !prefix.Contains("<size=70%>");

            // Leading space matches the single-space offset other tag types get from
            // TagInjector — without it neon tags read flush against the chat panel edge.
            string tagMarkup = isLegacy
                ? $"<b><color=#FFFFFF> {tagText}</color></b>"
                : $"<size=70%><b><i><color=#FFFFFF> {tagText}</color></i></b></size>";

            int afterMarker = markerEnd + NEON_CLOSE.Length;
            string restText = StripLeadingCloseTag(text.Substring(afterMarker), isLegacy);

            var parent = label.parent;
            if (parent == null) return;
            int idx = -1;
            for (int i = 0; i < parent.childCount; i++)
                if (parent[i] == label) { idx = i; break; }
            if (idx < 0) return;

            // Build flex-row replacement container
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Row;
            container.style.flexShrink    = new StyleFloat(1f);
            container.style.flexGrow      = new StyleFloat(0f);

            var tagLabel = new Label(tagMarkup);
            tagLabel.enableRichText   = true;
            tagLabel.style.flexShrink = new StyleFloat(0f);
            try
            {
                tagLabel.style.unityTextOutlineWidth = new StyleFloat(1.5f);
                tagLabel.style.unityTextOutlineColor = new StyleColor(outlineColor);
            }
            catch { /* older Unity without CSS outline support */ }

            var restLabel = new Label(restText);
            restLabel.enableRichText   = true;
            restLabel.style.flexShrink = new StyleFloat(1f);

            container.Add(tagLabel);
            container.Add(restLabel);

            parent.Remove(label);
            parent.Insert(idx, container);

            // Pass tagLabel + colors for rolling/pulse; null for static (cleanup tracking only)
            bool needsAnimation = neonMode != NeonMode.Static;
            _runner?.AddNeon(new NeonEntry(container,
                needsAnimation ? tagLabel   : null,
                needsAnimation ? rollingColors : null,
                speed, neonMode));
        }

        private static string StripLeadingCloseTag(string s, bool isLegacy)
        {
            const string legacyClose   = "</b>";
            const string standardClose = "</i></b></size>";
            string close = isLegacy ? legacyClose : standardClose;
            return s.StartsWith(close, StringComparison.Ordinal) ? s.Substring(close.Length) : s;
        }

        private static bool IsEnabled()
        {
            try
            {
                var runner = UnityEngine.Object.FindFirstObjectByType<KeybindRunner>();
                if (runner == null) return true;
                var f = typeof(KeybindRunner).GetField("_cmd",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var cmd = f?.GetValue(runner) as CommandKeybindConfig;
                return cmd?.enableLiveGradients ?? true;
            }
            catch { return true; }
        }
    }

    // Holds everything needed to re-render one animated label each frame.
    internal sealed class GradEntry
    {
        internal readonly Label      Label;
        internal readonly string     Prefix;
        internal readonly string     Suffix;
        internal readonly string     Text;
        internal readonly Color[]    Colors;
        internal readonly float      Speed;
        internal readonly RenderMode Mode;

        private readonly StringBuilder _sb;

        internal GradEntry(Label label, string prefix, string suffix,
                           string text, Color[] colors, float speed,
                           RenderMode mode = RenderMode.Normal)
        {
            Label  = label;  Prefix = prefix; Suffix = suffix;
            Text   = text;   Colors = colors; Speed  = speed;  Mode = mode;
            _sb = new StringBuilder(prefix.Length + text.Length * 28 + suffix.Length + 32);
        }

        // Allocation-free deterministic glint for Ice mode — replaces new System.Random() per char per frame.
        private static bool IceGlint(int bucket, int charIdx, int n)
        {
            uint h = (uint)(bucket * 997 + charIdx * 13 + 7);
            h ^= h >> 16; h *= 0x45d9f3bu; h ^= h >> 16;
            return (int)(h % (uint)Math.Max(1, n * 5)) == 0;
        }

        // cameraYaw is pre-fetched by LiveGradientRunner once per update tick (avoids Camera.main search per call).
        internal string Render(float t, float cameraYaw = 0f)
        {
            _sb.Clear();
            _sb.Append(Prefix);

            int n = Text.Length;

            // Pulse: smooth sin-wave lerp between two colors, applied uniformly to the whole tag.
            // Mirrors the rewards-page preview (lerpColor(c1,c2,(sin(t*2π*speed)+1)/2)).
            if (Mode == RenderMode.Pulse && Colors.Length >= 2)
            {
                float wave = (Mathf.Sin(t * Speed * Mathf.PI * 2f) + 1f) * 0.5f;
                Color c = Color.Lerp(Colors[0], Colors[1], wave);
                _sb.Append("<color=#");
                _sb.Append(ColorUtility.ToHtmlStringRGB(c));
                _sb.Append('>');
                _sb.Append(Text);
                _sb.Append("</color>");
                _sb.Append(Suffix);
                return _sb.ToString();
            }

            // Phase offset
            float phase;
            if (Mode == RenderMode.Holo)
            {
                // Direction-based: camera yaw drives gradient position.
                // Small time drift (0.02 cycles/s) keeps it subtly alive when stationary.
                phase = cameraYaw / 360f + t * 0.02f;
            }
            else
            {
                phase = t * Speed;
            }

            // Ice: pre-compute freeze factor (0 normally, ramps to 1 near peak of sine wave)
            float freezeFactor = 0f;
            if (Mode == RenderMode.Ice)
            {
                float wave = (Mathf.Sin(t * 0.38f) + 1f) * 0.5f;
                freezeFactor = Mathf.Clamp01((wave - 0.78f) / 0.22f);
            }

            for (int i = 0; i < n; i++)
            {
                float pos = ((float)i / Math.Max(n - 1, 1) + phase) % 1f;
                if (pos < 0f) pos += 1f;

                Color c;
                if (Colors.Length == 2)
                {
                    float sample = pos > 0.5f ? 1f - pos : pos;
                    c = Color.Lerp(Colors[0], Colors[1], sample * 2f);
                }
                else
                {
                    float scaled = pos * (Colors.Length - 1);
                    int lo = (int)scaled;
                    int hi = Math.Min(lo + 1, Colors.Length - 1);
                    c = Color.Lerp(Colors[lo], Colors[hi], scaled - lo);
                }

                switch (Mode)
                {
                    case RenderMode.Flame:
                        // Per-character brightness flicker — dancing fire movement
                        float flicker = Mathf.Sin(t * 9f + i * 1.7f) * 0.18f;
                        c = new Color(
                            Mathf.Clamp01(c.r + flicker),
                            Mathf.Clamp01(c.g + flicker * 0.55f),
                            Mathf.Clamp01(c.b * 0.15f)
                        );
                        break;

                    case RenderMode.Ice:
                        // Freeze: blend toward ice-white during peak of wave
                        if (freezeFactor > 0f)
                            c = Color.Lerp(c, new Color(0.83f, 0.94f, 1f), freezeFactor);
                        // Glint: rare per-char flash — allocation-free hash, no System.Random
                        if (IceGlint((int)(t * 4f), i, n))
                            c = Color.Lerp(c, Color.white, 0.88f);
                        break;

                    // Holo + Normal: no extra adjustment beyond palette walk
                }

                _sb.Append("<color=#");
                _sb.Append(ColorUtility.ToHtmlStringRGB(c));
                _sb.Append('>');
                _sb.Append(Text[i]);
                _sb.Append("</color>");
            }

            _sb.Append(Suffix);
            return _sb.ToString();
        }
    }

    // Tracks a neon split-label container; animates rolling or pulsing outline colour per frame.
    internal sealed class NeonEntry
    {
        internal readonly VisualElement Container;
        internal readonly Label         TagLabel;    // null for static neon
        internal readonly Color[]       Colors;      // null for static; [0] = base color for pulse
        internal readonly float         Speed;
        internal readonly NeonMode      Mode;

        internal NeonEntry(VisualElement container, Label tagLabel,
                           Color[] colors, float speed, NeonMode mode)
        {
            Container = container; TagLabel = tagLabel;
            Colors = colors; Speed = speed; Mode = mode;
        }
    }

    // MonoBehaviour that drives gradient animation every frame.
    internal sealed class LiveGradientRunner : MonoBehaviour
    {
        private readonly List<GradEntry> _active = new List<GradEntry>(16);
        private readonly List<NeonEntry> _neons  = new List<NeonEntry>(4);

        // Animate at ~20 fps max — gradients don't need per-frame precision.
        private const float UpdateInterval = 1f / 20f;
        // Keep at most this many animated labels; oldest are dropped when exceeded.
        private const int   MaxTracked     = 20;

        private float  _nextUpdateTime;
        private Camera _cachedCamera;
        private float  _cameraCacheExpiry;

        internal void Add(GradEntry e)
        {
            if (_active.Count >= MaxTracked) _active.RemoveAt(0);
            _active.Add(e);
        }

        internal void AddNeon(NeonEntry e)
        {
            if (_neons.Count >= MaxTracked) _neons.RemoveAt(0);
            _neons.Add(e);
        }

        private static bool IsChatFocused()
        {
            try
            {
                var chat = MonoBehaviourSingleton<UIManager>.Instance?.Chat;
                return chat != null && chat.IsFocused;
            }
            catch { return false; }
        }

        private void Update()
        {
            if (_active.Count == 0 && _neons.Count == 0) return;

            float now = Time.unscaledTime;
            if (now < _nextUpdateTime) return;   // throttle to ~20 fps
            _nextUpdateTime = now + UpdateInterval;

            bool chatFocused = IsChatFocused();

            // Refresh Camera.main cache at most once per second (tagged-object search is slow).
            if (now >= _cameraCacheExpiry)
            {
                try { _cachedCamera = Camera.main; } catch { _cachedCamera = null; }
                _cameraCacheExpiry = now + 1f;
            }
            float cameraYaw = 0f;
            try { cameraYaw = _cachedCamera?.transform?.eulerAngles.y ?? 0f; } catch { }

            // Prune dead gradient labels, then animate last 5 (or all if chat focused)
            for (int i = _active.Count - 1; i >= 0; i--)
                if (_active[i].Label == null || _active[i].Label.panel == null) _active.RemoveAt(i);
            int gradStart = chatFocused ? 0 : Math.Max(0, _active.Count - 5);
            for (int i = gradStart; i < _active.Count; i++)
                _active[i].Label.text = _active[i].Render(now, cameraYaw);

            // Prune dead neon containers, then animate last 5 (or all if chat focused)
            for (int i = _neons.Count - 1; i >= 0; i--)
                if (_neons[i].Container == null || _neons[i].Container.panel == null) _neons.RemoveAt(i);
            int neonStart = chatFocused ? 0 : Math.Max(0, _neons.Count - 5);
            for (int i = neonStart; i < _neons.Count; i++)
            {
                var n = _neons[i];
                if (n.TagLabel == null || n.Colors == null) continue; // static — no animation

                Color outline;

                if (n.Mode == NeonMode.Rolling)
                {
                    float pos = (now * n.Speed) % 1f;
                    if (pos < 0f) pos += 1f;
                    if (n.Colors.Length == 2)
                    {
                        float sample = pos > 0.5f ? 1f - pos : pos;
                        outline = Color.Lerp(n.Colors[0], n.Colors[1], sample * 2f);
                    }
                    else
                    {
                        float scaled = pos * (n.Colors.Length - 1);
                        int lo = (int)scaled;
                        outline = Color.Lerp(n.Colors[lo], n.Colors[Math.Min(lo + 1, n.Colors.Length - 1)], scaled - lo);
                    }
                }
                else // Pulse
                {
                    float wave = (Mathf.Sin(now * n.Speed * Mathf.PI * 2f) + 1f) * 0.5f;
                    Color b = n.Colors[0];
                    float brightness = 0.12f + 0.88f * wave;
                    outline = new Color(b.r * brightness, b.g * brightness, b.b * brightness);
                }

                try { n.TagLabel.style.unityTextOutlineColor = new StyleColor(outline); }
                catch { }
            }
        }

        private void OnDestroy() { _active.Clear(); _neons.Clear(); }
    }

    // Harmony postfix — runs after LocalMute and ChatRichTextPatch so we see the final text.
    [HarmonyPatch(typeof(UIChat), "AddChatMessage")]
    [HarmonyPriority(Priority.Last - 1)]
    public static class ChatLiveGradientPatch
    {
        static void Postfix(UIChat __instance)
        {
            try
            {
                var messagesRoot = AccessTools.Field(typeof(UIChat), "messages")
                    ?.GetValue(__instance) as VisualElement;

                if (messagesRoot == null)
                {
                    var sv = AccessTools.Field(typeof(UIChat), "scrollView")
                        ?.GetValue(__instance) as ScrollView;
                    messagesRoot = sv?.contentContainer;
                }

                if (messagesRoot == null || messagesRoot.childCount == 0) return;

                Label lastLabel = null;
                for (int i = messagesRoot.childCount - 1; i >= 0 && lastLabel == null; i--)
                {
                    var child = messagesRoot[i];
                    lastLabel = child as Label ?? child?.Q<Label>();
                }

                LiveGradientSystem.TryRegister(lastLabel);
                LiveGradientSystem.TryRegisterNeon(lastLabel);
            }
            catch { }
        }
    }
}
