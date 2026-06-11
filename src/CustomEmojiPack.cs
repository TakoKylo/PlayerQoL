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
using UnityEngine.Networking;
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
        // Kaomoji rendered to textures on demand (game font can't draw them). null = no kaomoji for token.
        private static readonly Dictionary<string, CustomEmojiAsset> _kaomojiByToken = new Dictionary<string, CustomEmojiAsset>(StringComparer.OrdinalIgnoreCase);
        // One representative (token, asset) per loaded file - drives the picker's Custom tab.
        private static readonly List<KeyValuePair<string, CustomEmojiAsset>> _primaryTokens = new List<KeyValuePair<string, CustomEmojiAsset>>();
        private static readonly Regex TokenRegex = new Regex(@":[a-zA-Z0-9_\-]+:", RegexOptions.Compiled);
        private static bool _loaded;

        // URL emojis from links.txt waiting to be downloaded (SprayMod-style "Name = url" lines).
        // Drained by PumpPendingDownloads from LocalMuteRunner.Update once a coroutine host exists.
        private static readonly List<KeyValuePair<string, string>> _pendingDownloads = new List<KeyValuePair<string, string>>(); // url -> cachePath

        /// <summary>One entry per loaded custom emoji file: (insertToken, thumbnail texture).</summary>
        public static List<KeyValuePair<string, Texture2D>> GetPickerItems()
        {
            EnsureLoaded();
            var result = new List<KeyValuePair<string, Texture2D>>(_primaryTokens.Count);
            foreach (var kv in _primaryTokens)
            {
                if (kv.Value?.StaticFrame != null)
                    result.Add(new KeyValuePair<string, Texture2D>(kv.Key, kv.Value.StaticFrame));
            }
            return result;
        }

        public static bool TryApplyInlineEmojis(Label label, string sourceText)
        {
            if (label == null || string.IsNullOrEmpty(sourceText))
                return false;

            EnsureLoaded();

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
                else if (TryGetKaomojiAsset(m.Value, out var kaomoji))
                    result.Add((m.Index, m.Index + m.Length, kaomoji));
            }
            return result;
        }

        // Lazily render a kaomoji shortcode (e.g. :tableflip:, :kao_shrug:) to a texture and cache it.
        private static bool TryGetKaomojiAsset(string token, out CustomEmojiAsset asset)
        {
            if (_kaomojiByToken.TryGetValue(token, out asset))
                return asset != null;

            asset = null;
            if (KaomojiSystem.TryGetKaomojiGlyph(token, out var glyph))
            {
                var tex = GlyphRenderer.Get(glyph, false, 40);
                if (tex != null)
                    asset = new CustomEmojiAsset { StaticFrame = tex };
            }

            _kaomojiByToken[token] = asset; // cache misses too, so we don't recompute every message
            return asset != null;
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
            const float height = 20f;
            var tex = asset.StaticFrame;
            // Square for picture emoji; preserve aspect for wide kaomoji textures.
            float width = (tex != null && tex.height > 0) ? height * ((float)tex.width / tex.height) : height;

            var image = new UnityEngine.UIElements.Image();
            image.AddToClassList("ponce-custom-emoji");
            image.style.width = width;
            image.style.height = height;
            image.style.marginLeft = 1f;
            image.style.marginRight = 1f;
            image.style.flexShrink = 0;
            image.image = tex;
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

            try { LoadLinkEmojis(); }
            catch (Exception ex) { Debug.LogWarning($"[CustomEmoji] links.txt load failed: {ex.Message}"); }

            Debug.Log($"[CustomEmoji] Loaded {_emojiByToken.Count} custom emoji aliases ({_pendingDownloads.Count} link downloads pending)");
        }

        // ------------------------------------------------------------------
        // URL-backed emojis (SprayMod-style): a links.txt in any emoji folder,
        // one entry per line, either "name = https://..." or a bare URL.
        // Downloads are cached on disk so each link is fetched only once.
        // ------------------------------------------------------------------
        private static string GetCacheDir()
        {
            string gameDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(gameDir, "config", "ModHub", "PlayerQoL", "EmojiCache");
        }

        private static void LoadLinkEmojis()
        {
            bool sawAnyLinksFile = false;

            foreach (string root in GetSearchRoots())
            {
                string linksPath = Path.Combine(root, "links.txt");
                if (!File.Exists(linksPath))
                    continue;

                sawAnyLinksFile = true;
                foreach (string rawLine in File.ReadAllLines(linksPath))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                        continue;

                    string name = null, url = line;
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        name = line.Substring(0, eq).Trim();
                        url = line.Substring(eq + 1).Trim();
                    }

                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        continue;

                    QueueLinkEmoji(name, url);
                }
            }

            // First run convenience: drop a commented template next to the image emojis.
            if (!sawAnyLinksFile)
            {
                try
                {
                    string gameDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                    string dir = Path.Combine(gameDir, "Plugins", "PlayerQoL", "Emojis");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "links.txt"),
                        "# Custom emoji links - one per line, same style as SprayMod:\n" +
                        "#   name = https://example.com/image.png\n" +
                        "#   https://example.com/other.gif   (name taken from the file name)\n" +
                        "# Supported: png, jpg, gif (animated gifs work). Downloads are cached.\n");
                }
                catch { }
            }
        }

        private static void QueueLinkEmoji(string name, string url)
        {
            try
            {
                var uri = new Uri(url);

                string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".gif")
                    ext = ".png"; // LoadImage copes with png/jpg; unknown types most often resolve to png

                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);

                string normalized = NormalizeName(name);
                if (string.IsNullOrEmpty(normalized))
                    return;

                string cachePath = Path.Combine(GetCacheDir(), normalized + ext);
                if (File.Exists(cachePath))
                {
                    RegisterFromFile(cachePath); // cached from a previous session
                    return;
                }

                _pendingDownloads.Add(new KeyValuePair<string, string>(url, cachePath));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CustomEmoji] Bad link entry '{url}': {ex.Message}");
            }
        }

        /// <summary>Start queued link downloads. Called from LocalMuteRunner.Update so a
        /// coroutine host is guaranteed; no-op when the queue is empty.</summary>
        public static void PumpPendingDownloads()
        {
            if (_pendingDownloads.Count == 0 || LocalMuteRunner.Instance == null)
                return;

            var batch = _pendingDownloads.ToList();
            _pendingDownloads.Clear();
            foreach (var kv in batch)
                LocalMuteRunner.Run(DownloadEmoji(kv.Key, kv.Value));
        }

        private static IEnumerator DownloadEmoji(string url, string cachePath)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 25;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[CustomEmoji] Download failed: {req.error} ({url})");
                    yield break;
                }

                byte[] bytes = req.downloadHandler.data;
                if (bytes == null || bytes.Length == 0)
                {
                    Debug.LogWarning($"[CustomEmoji] Link returned no data: {url}");
                    yield break;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                    File.WriteAllBytes(cachePath, bytes);
                    RegisterFromFile(cachePath);
                    Debug.Log($"[CustomEmoji] Downloaded {Path.GetFileName(cachePath)} from {url}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CustomEmoji] Failed caching '{url}': {ex.Message}");
                }
            }
        }

        private static IEnumerable<string> GetSearchRoots()
        {
            string gameDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            yield return Path.Combine(gameDir, "Plugins", "PlayerQoL", "Emojis");
            yield return Path.Combine(gameDir, "config", "ModHub", "PlayerQoL", "Emojis");
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

                // Pick the cleanest token as the picker-facing name (drop "2579-" style ID prefixes).
                string primary = Regex.Replace(normalized, @"^\d+[_-]*", string.Empty);
                if (string.IsNullOrWhiteSpace(primary)) primary = normalized;
                string primaryToken = $":{primary}:";
                if (!_primaryTokens.Any(kv => string.Equals(kv.Key, primaryToken, StringComparison.OrdinalIgnoreCase)))
                    _primaryTokens.Add(new KeyValuePair<string, CustomEmojiAsset>(primaryToken, asset));
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
