using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoncePuck.LocalMute
{
    internal static class CustomEmojiPack
    {
        private sealed class CustomEmojiAsset
        {
            public Texture2D StaticFrame;
            public List<Texture2D> Frames;
            public List<float> Delays;
            public bool IsAnimated => Frames != null && Frames.Count > 1;
        }

        private static readonly Dictionary<string, CustomEmojiAsset> _emojiByToken = new Dictionary<string, CustomEmojiAsset>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex TokenRegex = new Regex(@":[a-zA-Z0-9_\-]+:", RegexOptions.Compiled);
        private static bool _loaded;

        public static bool TryApplyInlineEmojis(Label label, string sourceText)
        {
            if (label == null || string.IsNullOrEmpty(sourceText))
                return false;

            EnsureLoaded();
            if (_emojiByToken.Count == 0)
                return false;

            var row = label.parent;
            if (row == null)
                return false;

            // Collect matches in order; bail early if none of the tokens are custom emoji.
            var orderedMatches = CollectOrderedMatches(sourceText);
            if (orderedMatches.Count == 0)
                return false;

            // Remove any previous inline wrapper we created for this row.
            for (int i = row.childCount - 1; i >= 0; i--)
            {
                if (row[i].name == "ponce_emoji_wrapper")
                    row[i].RemoveFromHierarchy();
            }

            // Find the label's slot in the parent so we can insert the wrapper at the same position.
            int labelIdx = -1;
            for (int i = 0; i < row.childCount; i++)
            {
                if (row[i] == label) { labelIdx = i; break; }
            }
            if (labelIdx < 0)
                return false;

            // Build a flex-row wrapper that will replace the label visually.
            // The original label is kept inside the wrapper (hidden, zero-size) so that
            // UIChatMessage's blur/expiry tween still has a valid layout target.
            var wrapper = new VisualElement { name = "ponce_emoji_wrapper" };
            wrapper.style.flexDirection = FlexDirection.Row;
            wrapper.style.flexWrap = Wrap.Wrap;
            wrapper.style.alignItems = Align.Center;
            wrapper.style.flexGrow = 1;
            wrapper.style.flexShrink = 1;

            // Blank and shrink the original label so it takes no space but stays reachable for tweens.
            label.text = string.Empty;
            label.style.position = Position.Absolute;
            label.style.width = 0;
            label.style.height = 0;
            label.style.minWidth = 0;
            label.style.minHeight = 0;
            label.style.overflow = Overflow.Hidden;

            // Build interleaved Label + Image children from the parsed segments.
            int cursor = 0;
            foreach (var (start, end, asset) in orderedMatches)
            {
                if (start > cursor)
                    wrapper.Add(MakeSegmentLabel(label, sourceText.Substring(cursor, start - cursor)));

                wrapper.Add(MakeEmojiImage(asset));
                cursor = end;
            }
            if (cursor < sourceText.Length)
                wrapper.Add(MakeSegmentLabel(label, sourceText.Substring(cursor)));

            // Add the original (now hidden) label as the last child so the tween target stays valid.
            label.RemoveFromHierarchy();
            wrapper.Add(label);

            // Insert wrapper exactly where the label was.
            row.Insert(labelIdx, wrapper);

            return true;
        }

        private static List<(int start, int end, CustomEmojiAsset asset)> CollectOrderedMatches(string text)
        {
            var result = new List<(int, int, CustomEmojiAsset)>();
            foreach (Match m in TokenRegex.Matches(text))
            {
                if (_emojiByToken.TryGetValue(m.Value, out var asset))
                    result.Add((m.Index, m.Index + m.Length, asset));
            }
            return result;
        }

        private static Label MakeSegmentLabel(Label template, string text)
        {
            var lbl = new Label(text);
            lbl.enableRichText = template.enableRichText;
            // Copy USS classes so font, colour, and size match the game's chat style.
            foreach (var cls in template.GetClasses())
                lbl.AddToClassList(cls);
            lbl.style.flexShrink = 1;
            lbl.style.whiteSpace = WhiteSpace.Normal;
            return lbl;
        }

        private static UnityEngine.UIElements.Image MakeEmojiImage(CustomEmojiAsset asset)
        {
            const float size = 20f;
            var image = new UnityEngine.UIElements.Image();
            image.AddToClassList("ponce-custom-emoji");
            image.style.width = size;
            image.style.height = size;
            image.style.marginLeft = 1f;
            image.style.marginRight = 1f;
            image.style.flexShrink = 0;
            image.image = asset.StaticFrame;
            if (asset.IsAnimated)
                LocalMuteRunner.Run(AnimateGif(image, asset));
            return image;
        }

        private static IEnumerator AnimateGif(UnityEngine.UIElements.Image image, CustomEmojiAsset asset)
        {
            // Wait one frame so the image is attached to the panel hierarchy before the loop checks image.panel.
            yield return null;

            int frameIndex = 0;

            while (image != null && image.panel != null && asset.IsAnimated)
            {
                if (frameIndex >= asset.Frames.Count)
                    frameIndex = 0;

                image.image = asset.Frames[frameIndex];

                float delay = 0.08f;
                if (asset.Delays != null && frameIndex < asset.Delays.Count)
                {
                    delay = Mathf.Max(0.03f, asset.Delays[frameIndex]);
                }

                frameIndex++;
                yield return new WaitForSecondsRealtime(delay);
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;

            foreach (string root in GetSearchRoots())
            {
                if (!Directory.Exists(root))
                    continue;

                try
                {
                    var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                        .Where(path =>
                        {
                            string ext = Path.GetExtension(path).ToLowerInvariant();
                            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif";
                        });

                    foreach (string file in files)
                    {
                        RegisterFromFile(file);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CustomEmoji] Failed scanning '{root}': {ex.Message}");
                }
            }

            Debug.Log($"[CustomEmoji] Loaded {_emojiByToken.Count} custom emoji aliases");
        }

        private static IEnumerable<string> GetSearchRoots()
        {
            string gameDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            yield return Path.Combine(gameDir, "Plugins", "PlayerQoL", "Emojis");
            yield return Path.Combine(gameDir, "config", "ModHub", "PlayerQoL", "Emojis");

            // Dev convenience path used in the current workspace.
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "OneDrive", "Desktop", "Desk", "Development", "Archive", "Puck stuff", "emojigg-collection");
        }

        private static void RegisterFromFile(string filePath)
        {
            try
            {
                var asset = LoadAsset(filePath);
                if (asset == null || asset.StaticFrame == null)
                    return;

                string baseName = Path.GetFileNameWithoutExtension(filePath);
                string normalized = NormalizeName(baseName);
                if (string.IsNullOrEmpty(normalized))
                    return;

                AddAliases(normalized, asset);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CustomEmoji] Failed loading '{filePath}': {ex.Message}");
            }
        }

        private static CustomEmojiAsset LoadAsset(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".gif")
            {
                return LoadGifAsset(filePath);
            }

            byte[] bytes = File.ReadAllBytes(filePath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes, false))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            return new CustomEmojiAsset { StaticFrame = texture };
        }

        private static CustomEmojiAsset LoadGifAsset(string filePath)
        {
            using (var gifImage = System.Drawing.Image.FromFile(filePath))
            {
                var frameDimension = new FrameDimension(gifImage.FrameDimensionsList[0]);
                int frameCount = gifImage.GetFrameCount(frameDimension);

                if (frameCount <= 0)
                    return null;

                PropertyItem delayItem = null;
                try
                {
                    delayItem = gifImage.GetPropertyItem(0x5100);
                }
                catch
                {
                    delayItem = null;
                }

                var frames = new List<Texture2D>();
                var delays = new List<float>();

                for (int i = 0; i < frameCount; i++)
                {
                    gifImage.SelectActiveFrame(frameDimension, i);

                    using (var ms = new MemoryStream())
                    using (var frameBitmap = new Bitmap(gifImage))
                    {
                        frameBitmap.Save(ms, ImageFormat.Png);
                        byte[] pngData = ms.ToArray();

                        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (texture.LoadImage(pngData, false))
                        {
                            texture.wrapMode = TextureWrapMode.Clamp;
                            texture.filterMode = FilterMode.Bilinear;
                            frames.Add(texture);
                        }
                        else
                        {
                            UnityEngine.Object.Destroy(texture);
                        }
                    }

                    float delay = 0.1f;
                    if (delayItem != null && delayItem.Value != null && delayItem.Value.Length >= (i + 1) * 4)
                    {
                        int rawDelay = BitConverter.ToInt32(delayItem.Value, i * 4);
                        delay = Mathf.Max(0.03f, rawDelay / 100f);
                    }
                    delays.Add(delay);
                }

                if (frames.Count == 0)
                    return null;

                return new CustomEmojiAsset
                {
                    StaticFrame = frames[0],
                    Frames = frames,
                    Delays = delays
                };
            }
        }

        private static void AddAliases(string normalized, CustomEmojiAsset asset)
        {
            AddToken($":{normalized}:", asset);

            string snake = normalized.Replace('-', '_');
            string kebab = normalized.Replace('_', '-');
            string compact = normalized.Replace("_", string.Empty).Replace("-", string.Empty);

            AddToken($":{snake}:", asset);
            AddToken($":{kebab}:", asset);
            AddToken($":{compact}:", asset);

            if (snake.EndsWith("_emoji", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = snake.Substring(0, snake.Length - "_emoji".Length);
                AddToken($":{trimmed}:", asset);
                AddToken($":{trimmed.Replace("_", "-")}:", asset);
            }

            if (snake.EndsWith("_gif", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = snake.Substring(0, snake.Length - "_gif".Length);
                AddToken($":{trimmed}:", asset);
                AddToken($":{trimmed.Replace("_", "-")}:", asset);
            }

            // Common dump names often start with numeric IDs (e.g. 2579-cat-yipee).
            string noNumericPrefix = Regex.Replace(snake, @"^\d+[_-]*", string.Empty);
            if (!string.IsNullOrWhiteSpace(noNumericPrefix) && !string.Equals(noNumericPrefix, snake, StringComparison.Ordinal))
            {
                AddToken($":{noNumericPrefix}:", asset);
                AddToken($":{noNumericPrefix.Replace("_", "-")}:", asset);
                AddToken($":{noNumericPrefix.Replace("_", string.Empty)}:", asset);
            }
        }

        private static void AddToken(string token, CustomEmojiAsset asset)
        {
            _emojiByToken[token] = asset;
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (char ch in value.ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    sb.Append(ch);
                }
                else if (ch == '_' || ch == '-' || ch == ' ')
                {
                    sb.Append('_');
                }
            }

            string normalized = sb.ToString();
            while (normalized.Contains("__")) normalized = normalized.Replace("__", "_");
            return normalized.Trim('_');
        }
    }
}
