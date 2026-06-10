// Chat.GlyphRenderer.cs - Render emoji / kaomoji strings to Texture2D via System.Drawing.
//
// The game's UI Toolkit font has no glyphs for most emoji and many kaomoji characters, so they
// show as tofu boxes (□) in plain Labels. Windows ships fonts that DO cover them, so we render
// the glyph offscreen with GDI+ and hand the result to UI Toolkit as an Image. White-on-transparent
// so it reads on any background.
//
//   * Emoji      -> "Segoe UI Emoji"  (monochrome but fully shaped)
//   * Kaomoji    -> "Segoe UI" (broad script coverage incl. combining marks), or
//                   "MS Gothic" when the face uses box-drawing chars (┻ ━ ╬ …)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using SD = System.Drawing;
using SDImaging = System.Drawing.Imaging;

namespace PoncePuck.LocalMute
{
    internal static class GlyphRenderer
    {
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        /// <summary>Render (and cache) a glyph string to a white-on-transparent texture, or null on failure.</summary>
        public static Texture2D Get(string text, bool isEmoji, int pixelSize)
        {
            if (string.IsNullOrEmpty(text)) return null;

            string key = (isEmoji ? "e" : "k") + pixelSize + ":" + text;
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            Texture2D tex = null;
            try
            {
                string font = isEmoji ? "Segoe UI Emoji" : PickKaomojiFont(text);
                tex = Render(text, font, pixelSize);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GlyphRenderer] render failed for '" + text + "': " + e.Message);
            }

            _cache[key] = tex; // cache null too, so we don't retry a failing glyph every frame
            return tex;
        }

        private static string PickKaomojiFont(string s)
        {
            foreach (char c in s)
                if (c >= '─' && c <= '╿') // box-drawing block
                    return "MS Gothic";
            return "Segoe UI";
        }

        private static Texture2D Render(string text, string fontName, int px)
        {
            using (var font = new SD.Font(fontName, px, SD.GraphicsUnit.Pixel))
            {
                SD.SizeF size;
                using (var probe = new SD.Bitmap(1, 1))
                using (var pg = SD.Graphics.FromImage(probe))
                {
                    pg.TextRenderingHint = SD.Text.TextRenderingHint.AntiAlias;
                    size = pg.MeasureString(text, font);
                }

                int w = Math.Max(4, (int)Math.Ceiling(size.Width));
                int h = Math.Max(4, (int)Math.Ceiling(size.Height));

                using (var bmp = new SD.Bitmap(w, h, SDImaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = SD.Graphics.FromImage(bmp))
                    {
                        g.Clear(SD.Color.Transparent);
                        g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAlias; // grayscale AA on transparent bg
                        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                        using (var brush = new SD.SolidBrush(SD.Color.White))
                            g.DrawString(text, font, brush, 0, 0);
                    }
                    return BitmapToTexture(bmp);
                }
            }
        }

        private static Texture2D BitmapToTexture(SD.Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var rect = new SD.Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, SDImaging.ImageLockMode.ReadOnly, SDImaging.PixelFormat.Format32bppArgb);
            var bytes = new byte[w * h * 4];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bmp.UnlockBits(data);

            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int src = y * w;
                int dst = (h - 1 - y) * w; // GDI+ is top-down, Texture2D is bottom-up
                for (int x = 0; x < w; x++)
                {
                    int si = (src + x) * 4; // BGRA
                    pixels[dst + x] = new Color32(bytes[si + 2], bytes[si + 1], bytes[si], bytes[si + 3]);
                }
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply(false);
            return tex;
        }
    }
}
