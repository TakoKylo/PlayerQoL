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

        // Default emoji set shipped with the mod - same set and hosting as SprayMod's
        // default sprays (config/default_sprays.json), so fresh installs get a working
        // Custom tab with zero setup. Seeded into links.txt on first run only; users
        // can delete lines they don't want and they won't come back.
        private static readonly KeyValuePair<string, string>[] DefaultLinkEmojis = new[]
        {
            new KeyValuePair<string, string>("cronchycat",     "https://files.catbox.moe/55gz6v.gif"),
            new KeyValuePair<string, string>("cat-yipee",      "https://files.catbox.moe/a194zv.gif"),
            new KeyValuePair<string, string>("thevoices",      "https://files.catbox.moe/jzaaeh.gif"),
            new KeyValuePair<string, string>("bocchioverload", "https://files.catbox.moe/wdzwr7.gif"),
            new KeyValuePair<string, string>("catJAM",         "https://files.catbox.moe/6xy3ks.gif"),
            new KeyValuePair<string, string>("anyayay",        "https://files.catbox.moe/xksm7t.gif"),
            new KeyValuePair<string, string>("dead",           "https://files.catbox.moe/n2yn0e.gif"),
            new KeyValuePair<string, string>("huh",            "https://files.catbox.moe/4d0k8y.gif"),
            new KeyValuePair<string, string>("plink",          "https://files.catbox.moe/gr6t6v.gif"),
            new KeyValuePair<string, string>("verycat",        "https://files.catbox.moe/qtr3dp.gif"),
            new KeyValuePair<string, string>("catkiss",        "https://files.catbox.moe/g5pw76.gif")
        };

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

            // Remove any previous inline wrapper we built for this label (defensive; a row is
            // normally processed once).
            for (int i = label.childCount - 1; i >= 0; i--)
            {
                if (label[i] is VisualElement prev && prev.name == "ponce_emoji_wrapper")
                    prev.RemoveFromHierarchy();
            }

            // b1117 rework: UIChat.AddChatMessage registers a GeometryChangedEvent ON THIS LABEL
            // (OnRowGeometryChanged -> PinLabelHeight + RefreshContentAndScroll) to size the row
            // and autoscroll, and UIChatMessage's expiry blur toggles the ".blurred" USS class on
            // THIS LABEL. The old approach (detach the label into a sibling wrapper) broke both:
            // the row never re-measured/scrolled and emoji messages never faded.
            // Fix: keep the label in place, clear its text, and host the emoji content as the
            // label's own children. The game's geometry callback then re-fires (empty text makes
            // PinLabelHeight a no-op, RefreshContentAndScroll runs) and ".blurred" fades our content.
            label.text = string.Empty;
            label.style.flexDirection = FlexDirection.Row;
            label.style.flexWrap = Wrap.Wrap;
            label.style.alignItems = Align.Center;
            label.style.height = StyleKeyword.Auto;   // let the row grow to the emoji content

            var wrapper = new VisualElement { name = "ponce_emoji_wrapper" };
            wrapper.style.flexDirection = FlexDirection.Row;
            wrapper.style.flexWrap = Wrap.Wrap;
            wrapper.style.alignItems = Align.Center;
            wrapper.style.flexGrow = 1;
            wrapper.style.flexShrink = 1;

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

            label.Add(wrapper);
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

        private static string GetEmojisDir()
        {
            string gameDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(gameDir, "config", "ModHub", "PlayerQoL", "Emojis");
        }

        private const string LinksHeader =
            "# Custom emoji links - one per line, same style as SprayMod:\n" +
            "#   name = https://example.com/image.png\n" +
            "#   https://example.com/other.gif   (name taken from the file name)\n" +
            "# Supported: png, jpg, gif (animated gifs work). Downloads are cached.\n" +
            "# Delete lines you don't want - this file is only created once.\n";

        /// <summary>Open links.txt in the system editor (creates it with the header if missing).</summary>
        public static void OpenLinksFile()
        {
            try
            {
                string dir = GetEmojisDir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "links.txt");
                // Recreate with header only - a user who deleted entries shouldn't get defaults back.
                if (!File.Exists(path))
                    File.WriteAllText(path, LinksHeader);
                Application.OpenURL("file:///" + path.Replace('\\', '/'));
            }
            catch (Exception ex) { Debug.LogWarning($"[CustomEmoji] OpenLinksFile failed: {ex.Message}"); }
        }

        /// <summary>Open the custom emoji folder in the file explorer.</summary>
        public static void OpenEmojiFolder()
        {
            try
            {
                string dir = GetEmojisDir();
                Directory.CreateDirectory(dir);
                Application.OpenURL("file:///" + dir.Replace('\\', '/'));
            }
            catch (Exception ex) { Debug.LogWarning($"[CustomEmoji] OpenEmojiFolder failed: {ex.Message}"); }
        }

        private static void LoadLinkEmojis()
        {
            // One-time migration: earlier builds seeded links.txt next to the DLL in
            // Plugins/PlayerQoL/Emojis; user-editable files belong under config/.
            try
            {
                string gameDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string oldLinks = Path.Combine(gameDir, "Plugins", "PlayerQoL", "Emojis", "links.txt");
                string newDir = Path.Combine(gameDir, "config", "ModHub", "PlayerQoL", "Emojis");
                string newLinks = Path.Combine(newDir, "links.txt");
                if (File.Exists(oldLinks) && !File.Exists(newLinks))
                {
                    Directory.CreateDirectory(newDir);
                    File.Move(oldLinks, newLinks);
                    Debug.Log("[CustomEmoji] Moved links.txt to config/ModHub/PlayerQoL/Emojis");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CustomEmoji] links.txt migration failed: {ex.Message}");
            }

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

            // First run: write links.txt seeded with the default emoji set and load it now.
            if (!sawAnyLinksFile)
            {
                try
                {
                    string dir = GetEmojisDir();
                    Directory.CreateDirectory(dir);

                    var sb = new StringBuilder(LinksHeader);
                    sb.AppendLine();
                    foreach (var kv in DefaultLinkEmojis)
                        sb.AppendLine($"{kv.Key} = {kv.Value}");
                    File.WriteAllText(Path.Combine(dir, "links.txt"), sb.ToString());

                    foreach (var kv in DefaultLinkEmojis)
                        QueueLinkEmoji(kv.Key, kv.Value);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CustomEmoji] Failed seeding default links.txt: {ex.Message}");
                }
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

                // A local image file with the same name already provides this emoji -
                // don't waste a download (files are scanned before links).
                if (_emojiByToken.ContainsKey($":{normalized}:"))
                    return;

                // Cached from a previous session? The stored extension may differ from the
                // URL's (DownloadEmoji corrects it from the image magic bytes), so try all.
                string cacheDir = GetCacheDir();
                foreach (string knownExt in new[] { ext, ".png", ".gif", ".jpg", ".jpeg" })
                {
                    string candidate = Path.Combine(cacheDir, normalized + knownExt);
                    if (File.Exists(candidate))
                    {
                        RegisterFromFile(candidate);
                        return;
                    }
                }

                _pendingDownloads.Add(new KeyValuePair<string, string>(url, Path.Combine(cacheDir, normalized + ext)));
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
            if (LocalMuteRunner.Instance == null)
                return;

            // Kick the one-time emoji load here (flag-guarded) so first-run seeding and
            // default downloads happen at startup, not on the first chat message.
            EnsureLoaded();

            if (_pendingDownloads.Count == 0)
                return;

            var batch = _pendingDownloads.ToList();
            _pendingDownloads.Clear();
            foreach (var kv in batch)
                LocalMuteRunner.Run(DownloadEmoji(kv.Key, kv.Value));
        }

        /// <summary>Image type from magic bytes - the only trustworthy signal for a download
        /// (URLs can lack extensions and error pages arrive with HTTP 200).</summary>
        private static string DetectImageExtension(byte[] b)
        {
            if (b == null || b.Length < 4) return null;
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
            if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return ".gif";
            if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
            return null;
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

                // Never cache non-image payloads (hosts return HTML error pages with HTTP 200);
                // a cached junk file would block re-downloading forever.
                string realExt = DetectImageExtension(bytes);
                if (realExt == null)
                {
                    Debug.LogWarning($"[CustomEmoji] Link is not a png/gif/jpg image, skipped: {url}");
                    yield break;
                }

                try
                {
                    cachePath = Path.ChangeExtension(cachePath, realExt);
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
            // Documented home: config, like every other user-editable PlayerQoL file.
            yield return Path.Combine(gameDir, "config", "ModHub", "PlayerQoL", "Emojis");
            // Legacy location kept readable so existing installs don't lose emojis.
            yield return Path.Combine(gameDir, "Plugins", "PlayerQoL", "Emojis");
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
