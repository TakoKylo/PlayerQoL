using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
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

        // Sibling element that hosts a row's rendered emoji content, and the USS class UIChatMessage
        // toggles on the row's Label when a message expires (mirrored onto the wrapper - see SyncBlurState).
        internal const string WrapperName = "ponce_emoji_wrapper";
        private const string BlurredClass = "blurred";

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

        public static bool TryApplyInlineEmojis(Label label, string sourceText, object uiChat = null)
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

            // Remove any previous inline wrapper we built for this row (defensive; a row is
            // normally processed once).
            for (int i = row.childCount - 1; i >= 0; i--)
            {
                if (row[i] is VisualElement prevWrapper && prevWrapper.name == WrapperName)
                    prevWrapper.RemoveFromHierarchy();
            }

            int labelIdx = row.IndexOf(label);
            if (labelIdx < 0)
                return false;

            // The emoji content MUST live in a plain VisualElement, NOT inside the Label: Yoga drops
            // a node's text-measure function once it has children, so a Label hosting elements
            // collapses the row and messages overlap. Build a wrapper that content-sizes correctly.
            var wrapper = new VisualElement { name = WrapperName };
            wrapper.userData = label;                    // blur target - see SyncBlurState
            wrapper.style.flexDirection = FlexDirection.Row;
            wrapper.style.flexWrap = Wrap.Wrap;
            wrapper.style.alignItems = Align.Center;
            wrapper.style.flexGrow = 1;
            wrapper.style.flexShrink = 1;

            // Plan the row as plain data first. Nothing is detached until the plan is complete, so a
            // failure while decoding an emoji can't strand the row's label and blank the message.
            // Each text segment is re-balanced so a rich-text span that straddles an emoji token
            // ("<b>hi :catjam: bye</b>", or an @mention wrapping the line in <mark>) keeps its
            // formatting instead of orphaning the open/close tag on different segments.
            var parts = new List<(string text, CustomEmojiAsset asset)>();
            int cursor = 0;
            foreach (var (start, end, asset) in orderedMatches)
            {
                if (start > cursor)
                    parts.Add((BalanceRichTextSegment(sourceText, cursor, start), null));
                parts.Add((null, asset));
                cursor = end;
            }
            if (cursor < sourceText.Length)
                parts.Add((BalanceRichTextSegment(sourceText, cursor, sourceText.Length), null));

            // Commit. The game's own Label is re-homed as the wrapper's FIRST text run rather than
            // hidden: it stays a leaf, so it keeps its text measure and sizes normally, and it stays
            // real and visible - which matters because other mods reach into the row for it.
            // UnifiedTagMod pulls its [[G|..]] / [[N|..]] markers out of this label's text and then
            // re-renders that text every frame to animate the tag. Emptying the label (the previous
            // approach) left it nothing to find, so tags showed as raw markup on any message that
            // also carried an emoji. The marker lives in the message prefix, so it always lands in
            // this first segment - CollectOrderedMatches refuses to split a marker.
            try
            {
                label.RemoveFromHierarchy();
                bool labelPlaced = false;
                foreach (var (text, asset) in parts)
                {
                    if (asset != null)
                    {
                        wrapper.Add(MakeEmojiImage(asset));
                    }
                    else if (!labelPlaced)
                    {
                        labelPlaced = true;
                        label.text = text;
                        wrapper.Add(label);
                    }
                    else
                    {
                        wrapper.Add(MakeSegmentLabel(label, text));
                    }
                }

                // Emoji-only message: no text segment claimed the label, but it still has to be in
                // the tree (and be the row's first Label) for UIChatMessage and the tag lookup.
                if (!labelPlaced)
                {
                    label.text = string.Empty;
                    wrapper.Insert(0, label);
                }

                row.Insert(labelIdx, wrapper);
            }
            catch (Exception ex)
            {
                // Put the row back the way we found it rather than leaving an empty message.
                Debug.LogWarning($"[CustomEmoji] Inline render failed, restoring plain row: {ex.Message}");
                wrapper.RemoveFromHierarchy();
                label.RemoveFromHierarchy();
                label.text = sourceText;
                row.Insert(Mathf.Clamp(labelIdx, 0, row.childCount), label);
                return false;
            }

            // b1117+ drives row/container height + autoscroll from the label's GeometryChangedEvent
            // (UIChat.RefreshContentAndScroll). Nudge it once more after the wrapper settles, since
            // the images can resolve their size a pass later. No-op if no UIChat instance was passed.
            HookContentRefresh(wrapper, uiChat);
            return true;
        }

        /// <summary>
        /// UIChatMessage.Focus/Blur toggle the ".blurred" USS class on the row's own Label to fade
        /// expired messages. Our emoji content lives in a sibling wrapper (a Label can't host child
        /// elements - Yoga drops the text measure once a node has children, which collapses the row),
        /// so the class never reaches it and emoji messages would stay fully opaque forever. Mirror
        /// the label's current state onto the wrapper; called from the UIChatMessage patches.
        /// </summary>
        internal static void SyncBlurState(VisualElement row)
        {
            var wrapper = row?.Q<VisualElement>(WrapperName);
            if (!(wrapper?.userData is Label label)) return;

            bool blurred = label.ClassListContains(BlurredClass);
            // Mirror onto the siblings we created, NOT onto the wrapper: UIChatMessage already
            // blurs the re-homed label itself, so blurring their shared parent would fade that one
            // twice (wrapper opacity * label opacity) and leave the text darker than the images.
            foreach (var child in wrapper.Children())
            {
                if (child != label)
                    child.EnableInClassList(BlurredClass, blurred);
            }
        }

        private static MethodInfo _refreshContentAndScroll;
        private static bool _refreshResolved;

        // Re-invoke UIChat's private RefreshContentAndScroll so the message container re-measures
        // and autoscrolls after we swap in the emoji wrapper.
        private static void HookContentRefresh(VisualElement anchor, object uiChat)
        {
            if (uiChat == null) return;
            if (!_refreshResolved)
            {
                _refreshResolved = true;
                // Match the no-arg overload explicitly: a future build that adds a parameter would
                // otherwise resolve here and then throw TargetParameterCountException on every row.
                _refreshContentAndScroll = uiChat.GetType().GetMethod(
                    "RefreshContentAndScroll",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                if (_refreshContentAndScroll == null)
                    Debug.LogWarning("[CustomEmoji] UIChat.RefreshContentAndScroll(): not found - " +
                                     "emoji rows won't resize the chat container on this build.");
            }
            var mi = _refreshContentAndScroll;
            if (mi == null) return;

            void Refresh()
            {
                try { mi.Invoke(uiChat, null); }
                catch (Exception ex) { Debug.LogWarning($"[CustomEmoji] RefreshContentAndScroll failed: {ex.Message}"); }
            }

            // Refresh once, as soon as the wrapper has a real height, then never again: the refresh
            // re-measures every row and restarts a scroll tween, so leaving this live would re-run
            // the whole thing on every later layout pass of every emoji row still in chat.
            bool refreshed = false;
            anchor.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (refreshed || anchor.contentRect.height <= 0f) return;
                refreshed = true;
                Refresh();
            });
            // And once on the next layout pass in case geometry never changes again.
            anchor.schedule.Execute(() => { if (!refreshed) { refreshed = true; Refresh(); } });
        }

        // Spans owned by UnifiedTagMod's animated tags. A :shortcode: inside one would split the
        // marker across two segment labels, and TagMod parses a marker out of a single label's text
        // - so the tag would silently stop rendering. Leave anything inside these alone.
        private static readonly Regex TagMarkerRegex = new Regex(@"\[\[[A-Za-z]\|.*?\]\]", RegexOptions.Compiled);

        private static List<(int start, int end, CustomEmojiAsset asset)> CollectOrderedMatches(string text)
        {
            var result = new List<(int, int, CustomEmojiAsset)>();
            var markers = TagMarkerRegex.Matches(text);

            foreach (Match m in TokenRegex.Matches(text))
            {
                bool insideMarker = false;
                foreach (Match marker in markers)
                {
                    if (m.Index >= marker.Index && m.Index < marker.Index + marker.Length)
                    {
                        insideMarker = true;
                        break;
                    }
                }
                if (insideMarker)
                    continue;

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

        // Matches the rich-text tags the mod emits: <b> <i> <u> <s> <mark=..> <color=..> and closes.
        private static readonly Regex RichTagRegex = new Regex(@"<(/?)([a-zA-Z]+)(=[^>]*)?>", RegexOptions.Compiled);

        // Re-open the rich-text spans that were open at position 'a' and close the spans still open
        // at position 'b', so a segment cut out of the middle of the message is self-balanced.
        private static string BalanceRichTextSegment(string full, int a, int b)
        {
            if (a < 0 || b > full.Length || a >= b)
                return string.Empty;

            var openAtA = ScanOpenTags(full, a);
            var openAtB = ScanOpenTags(full, b);
            string body = full.Substring(a, b - a);
            if (openAtA.Count == 0 && openAtB.Count == 0)
                return body;

            var sb = new StringBuilder();
            foreach (var t in openAtA) sb.Append(t.open);
            sb.Append(body);
            for (int i = openAtB.Count - 1; i >= 0; i--)
                sb.Append("</").Append(openAtB[i].name).Append('>');
            return sb.ToString();
        }

        // The tags left open (unclosed) just before position 'pos', in open order.
        private static List<(string name, string open)> ScanOpenTags(string full, int pos)
        {
            var stack = new List<(string name, string open)>();
            foreach (Match m in RichTagRegex.Matches(full))
            {
                if (m.Index >= pos) break;
                string name = m.Groups[2].Value.ToLowerInvariant();
                if (m.Groups[1].Value == "/")
                {
                    for (int i = stack.Count - 1; i >= 0; i--)
                        if (stack[i].name == name) { stack.RemoveAt(i); break; }
                }
                else
                {
                    stack.Add((name, m.Value));
                }
            }
            return stack;
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
