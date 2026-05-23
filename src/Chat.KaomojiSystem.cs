// Chat.KaomojiSystem.cs - Text-based emoticons (kaomoji) replacement system
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PoncePuck.LocalMute
{
    public static class KaomojiSystem
    {
        private static Dictionary<string, string> _kaomojiMap;
        private static Dictionary<string, string> _emojiMap;

        public static void Initialize()
        {
            // Native emoji shortcodes (B310 chat supports emoji rendering).
            // If a shortcode exists in both maps, emoji takes precedence.
            _emojiMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "<3", "❤️" },
                { "</3", "💔" },
                { ":heart:", "❤️" },
                { ":hearts:", "💕" },
                { ":broken_heart:", "💔" },
                { ":sparkling_heart:", "💖" },
                { ":orange_heart:", "🧡" },
                { ":yellow_heart:", "💛" },
                { ":green_heart:", "💚" },
                { ":blue_heart:", "💙" },
                { ":purple_heart:", "💜" },
                { ":black_heart:", "🖤" },
                { ":white_heart:", "🤍" },
                { ":cry:", "😭" },
                { ":tears:", "😭" },
                { ":fire:", "🔥" },
                { ":100:", "💯" },
                { ":joy:", "😂" },
                { ":laugh:", "😆" },
                { ":happy:", "😄" },
                { ":smile:", "🙂" },
                { ":grinning:", "😀" },
                { ":grin:", "😁" },
                { ":love:", "😍" },
                { ":kiss:", "😘" },
                { ":blush:", "😊" },
                { ":flushed:", "😳" },
                { ":sad:", "😢" },
                { ":sob:", "😭" },
                { ":plead:", "🥺" },
                { ":angry:", "😠" },
                { ":rage:", "😡" },
                { ":mad:", "😤" },
                { ":shocked:", "😱" },
                { ":surprised:", "😮" },
                { ":thinking:", "🤔" },
                { ":hmm:", "🤨" },
                { ":wink:", "😉" },
                { ":cool:", "😎" },
                { ":sleep:", "😴" },
                { ":sleepy:", "🥱" },
                { ":party:", "🥳" },
                { ":celebrate:", "🎉" },
                { ":gg:", "🏒" },
                { ":puck:", "🏒" },
                { ":trophy:", "🏆" },
                { ":medal:", "🏅" },
                { ":shrug:", "🤷" },
                { ":facepalm:", "🤦" },
                { ":wave:", "👋" },
                { ":pray:", "🙏" },
                { ":thumbsup:", "👍" },
                { ":thumbsdown:", "👎" },
                { ":ok:", "👌" },
                { ":clap:", "👏" },
                { ":eyes:", "👀" },
                { ":muscle:", "💪" },
                { ":flex:", "💪" },
                { ":fist:", "✊" },
                { ":peace:", "✌️" },
                { ":point_up:", "☝️" },
                { ":point_down:", "👇" },
                { ":point_left:", "👈" },
                { ":point_right:", "👉" },
                { ":raised_hands:", "🙌" },
                { ":wave_hi:", "👋" },
                { ":male:", "♂️" },
                { ":female:", "♀️" },
                { ":gay:", "🏳️‍🌈" },
                { ":pride:", "🏳️‍🌈" },
                { ":rainbow:", "🌈" },
                { ":trans:", "🏳️‍⚧️" },
                { ":bi:", "🩷💜💙" },
                { ":lesbian:", "🧡🤍🩷" },
                { ":bear_emoji:", "🐻" },
                { ":cat_emoji:", "🐱" },
                { ":dog_emoji:", "🐶" },
                { ":ghost:", "👻" },
                { ":skull:", "💀" },
                { ":poop:", "💩" },
                { ":rocket:", "🚀" },
                { ":star:", "⭐" },
                { ":sparkles:", "✨" },
                { ":boom:", "💥" },
                { ":lightning:", "⚡" },
                { ":check:", "✅" },
                { ":x:", "❌" },
                { ":warning:", "⚠️" },
                { ":question:", "❓" },
                { ":exclamation:", "❗" },
                { ":soccer:", "⚽" },
                { ":ice:", "🧊" },
                { ":snowflake:", "❄️" },
                { ":sun:", "☀️" },
                { ":moon:", "🌙" },
                { ":pregnant_man:", "🫃" }
            };

            ExpandEmojiAliases();

            _kaomojiMap = new Dictionary<string, string>
            {
                // Happy/Positive
                { ":happy:", "(^▽^)" },
                { ":joy:", "(^◇^)" },
                { ":smile:", "(◕‿◕)" },
                { ":grin:", "(≧◡≦)" },
                { ":laugh:", "(^o^)" },
                { ":excited:", "\\(^o^)/" },
                { ":yay:", "ヽ(^o^)丿" },
                { ":dance:", "ヾ(⌐■_■)ノ♪" },
                { ":party:", "ヽ(°〇°)ﾉ" },
                { ":celebrate:", "٩(◕‿◕｡)۶" },
                
                // Love/Affection
                { ":love:", "(♥‿♥)" },
                { ":heart:", "(♡‿♡)" },
                { ":kiss:", "(づ ￣ ³￣)づ" },
                { ":hug:", "(づ｡◕‿‿◕｡)づ" },
                { ":blush:", "(⁄ ⁄•⁄ω⁄•⁄ ⁄)" },
                { ":flushed:", "(⁄ ⁄•⁄ω⁄•⁄ ⁄)" },
                { ":cute:", "(｡◕‿◕｡)" },
                { ":sparkle:", "(✿◠‿◠)" },
                
                // Sad/Crying
                { ":sad:", "(╥﹏╥)" },
                { ":cry:", "(ಥ﹏ಥ)" },
                { ":sob:", "(ಥ_ಥ)" },
                { ":tears:", "(༎ຶ ෴ ༎ຶ)" },
                { ":depressed:", "(︶︹︺)" },
                { ":disappointed:", "(ー_ー)" },
                
                // Angry/Annoyed
                { ":angry:", "(ಠ_ಠ)" },
                { ":rage:", "(╬ Ò ‸ Ó)" },
                { ":mad:", "(¬_¬)" },
                { ":annoyed:", "(︶︿︶)" },
                { ":grumpy:", "(◣_◢)" },
                { ":glare:", "(눈_눈)" },
                
                // Surprised/Shocked
                { ":shocked:", "(°ロ°)" },
                { ":surprised:", "(o_O)" },
                { ":ohno:", "(⊙_⊙)" },
                { ":omg:", "Σ(O_O)" },
                { ":gasp:", "(⊙ω⊙)" },
                { ":wow:", "(◎_◎)" },
                
                // Confused/Thinking
                { ":confused:", "(・_・?)" },
                { ":thinking:", "(¬‿¬)" },
                { ":hmm:", "(¬_¬ )" },
                { ":doubt:", "(¬､¬)" },
                
                // Wink/Smirk
                { ":wink:", "(^_~)" },
                { ":smirk:", "(¬‿¬)" },
                { ":cool:", "(⌐■_■)" },
                { ":dealwithit:", "(⌐■-■)" },
                { ":sunglasses:", "(⌐▀͡ ̯ʖ▀)" },
                
                // Sleep/Tired
                { ":sleep:", "(-.-)Zzz..." },
                { ":sleepy:", "(─.─)" },
                { ":tired:", "(=_=)" },
                { ":yawn:", "(>_<)" },
                { ":zzz:", "(-_-)zzz" },
                
                // Food/Eating
                { ":nom:", "(っ˘ڡ˘ς)" },
                { ":yum:", "(っ˘ڡ˘)" },
                { ":hungry:", "(￣﹃￣)" },
                { ":drool:", "(¯﹃¯)" },
                
                // Actions/Gestures
                { ":shrug:", "¯\\_(ツ)_/¯" },
                { ":tableflip:", "(╯°□°)╯︵ ┻━┻" },
                { ":unflip:", "┬─┬ノ( º _ ºノ)" },
                { ":facepalm:", "(－‸ლ)" },
                { ":wave:", "(°ー°)ノ" },
                { ":salute:", "(￣^￣)ゞ" },
                { ":bow:", "m(_ _)m" },
                { ":run:", "ε=ε=ε=┌(;*´Д`)ﾉ" },
                { ":fight:", "(ง'̀-'́)ง" },
                { ":punch:", "O=('-'Q)" },
                { ":shoot:", "︻デ═一" },
                { ":sword:", "o(>< )o" },
                
                // Animals
                { ":bear:", "ʕ•ᴥ•ʔ" },
                { ":cat:", "(=^･ω･^=)" },
                { ":dog:", "U・ω・U" },
                { ":rabbit:", "／(≧ x ≦)＼" },
                { ":pig:", "(´(oo)｀)" },
                { ":cow:", "( ´ ▽ ` ).｡ｏ♡" },
                { ":fish:", ">゜))))彡" },
                { ":bird:", "⊱(◕‿◕)つ⊰" },
                
                // Objects/Symbols
                { ":stars:", "☆*:.｡.o(≧▽≦)o.｡.:*☆" },
                { ":sparkles:", "✧･ﾟ: *✧･ﾟ:*" },
                { ":music:", "♪┏(・o･)┛♪" },
                { ":musicnotes:", "♫♪♬" },
                { ":beer:", "d(˘▾˘)b" },
                { ":coffee:", "c[_]" },
                { ":tea:", "( ˘▽˘)っ♨" },
                
                // Memes/Internet Culture
                { ":lenny:", "( ͡° ͜ʖ ͡°)" },
                { ":lennyshrug:", "¯\\( ͡° ͜ʖ ͡°)/¯" },
                { ":disapprove:", "ಠ_ಠ" },
                { ":wat:", "(ಠ_ಠ)" },
                { ":doge:", "ᕙ(⇀‸↼‶)ᕗ" },
                
                // Special/Fancy
                { ":magic:", "(ﾉ◕ヮ◕)ﾉ*:･ﾟ✧" },
                { ":wizard:", "(∩ ｀-´)⊃━☆ﾟ.*･｡ﾟ" },
                { ":nyan:", "~=[,,_,,]:3" },
                { ":zombie:", "[¬º-°]¬" },
                { ":ghost:", "‹(•¿•)›" },
                { ":devil:", "ψ(｀∇´)ψ" },
                { ":angel:", "☜(⌒▽⌒)☞" },
                
                // Other Emotions
                { ":nervous:", "(ᗒᗣᗕ)՞" },
                { ":embarrassed:", "(*/ω＼*)" },
                { ":shy:", "(⁄ ⁄>⁄ ▽ ⁄<⁄ ⁄)" },
                { ":worried:", "(´･_･`)" },
                { ":panic:", "Σ(っ°Д °;)っ" },
                { ":sweat:", "(ᗒᗩᗕ)" },
                { ":weary:", "(,,>﹏<,,)" },
                { ":confident:", "(·•᷄•᷅ )" },
                { ":relief:", "(´｡• ᵕ •｡`)" },
                { ":determined:", "ψ(._. )>" },
                { ":smug:", "(￣ω￣)" },
                { ":innocent:", "(◡‿◡✿)" },
                
                // Random/Fun
                { ":dead:", "x_x" },
                { ":pirate:", "P(⊙_⊙)P" },
                { ":monocle:", "(⌐ ͡■ ͜ʖ ͡■)" },
                { ":mustache:", "ಠ_ರೃ" },
                { ":glasses:", "(⌐▀͡ ̯ʖ▀)" },
                { ":sniper:", "▄︻̷̿┻̿═━一" },
                { ":trollface:", "ಠ◡ಠ" },
                { ":derp:", "•_•" },
                { ":hype:", "༼ つ ಥ_ಥ ༽つ" },
                { ":pray:", "人(-ω-)人" },
                { ":peace:", "v(-‿-)v" },
                { ":highfive:", "( '▽')／＼(▽' )" },
                { ":fistbump:", "╰(◕ヮ◕)つ¤=[]:::::::>" }
            };
            
            Debug.Log($"[KaomojiSystem] Initialized with {_emojiMap.Count} emoji + {_kaomojiMap.Count} kaomoji fallback entries");
        }

        private static void ExpandEmojiAliases()
        {
            foreach (var kvp in _emojiMap.ToList())
            {
                AddAutoAliases(kvp.Key, kvp.Value);
            }

            // Popular shortcode variants used across Discord/GitHub communities.
            AddAlias(":+1:", "👍");
            AddAlias(":-1:", "👎");
            AddAlias(":thumbs_up:", "👍");
            AddAlias(":thumbs-down:", "👎");
            AddAlias(":thumbs_down:", "👎");
            AddAlias(":ok_hand:", "👌");
            AddAlias(":clapping:", "👏");
            AddAlias(":raised_hands:", "🙌");
            AddAlias(":muscle:", "💪");
            AddAlias(":fire:", "🔥");
            AddAlias(":hot:", "🔥");
            AddAlias(":poo:", "💩");
            AddAlias(":shit:", "💩");
            AddAlias(":explode:", "💥");
            AddAlias(":boom:", "💥");
            AddAlias(":zap:", "⚡");
            AddAlias(":warning_sign:", "⚠️");
            AddAlias(":question_mark:", "❓");
            AddAlias(":exclamation_mark:", "❗");
            AddAlias(":check_mark:", "✅");
            AddAlias(":white_check_mark:", "✅");
            AddAlias(":cross_mark:", "❌");
            AddAlias(":x_mark:", "❌");

            // Common heart aliases.
            AddAlias(":red_heart:", "❤️");
            AddAlias(":pink_heart:", "💕");
            AddAlias(":heartbreak:", "💔");

            // Common mood aliases.
            AddAlias(":lol:", "😂");
            AddAlias(":rofl:", "😂");
            AddAlias(":tears_of_joy:", "😂");
            AddAlias(":grinning_face:", "😀");
            AddAlias(":beaming_face:", "😁");
            AddAlias(":smiley:", "😄");
            AddAlias(":smiley_face:", "😄");
            AddAlias(":crying:", "😭");
            AddAlias(":loudly_crying:", "😭");
            AddAlias(":angry_face:", "😠");
            AddAlias(":rage_face:", "😡");
            AddAlias(":mind_blown:", "🤯");
            AddAlias(":thinking_face:", "🤔");
            AddAlias(":face_with_raised_eyebrow:", "🤨");

            // Flags and identity aliases currently in your map.
            AddAlias(":rainbow_flag:", "🏳️‍🌈");
            AddAlias(":trans_flag:", "🏳️‍⚧️");
            AddAlias(":bisexual_flag:", "🩷💜💙");
            AddAlias(":lesbian_flag:", "🧡🤍🩷");
        }

        private static void AddAutoAliases(string token, string emoji)
        {
            string core = token.Trim(':');
            if (string.IsNullOrWhiteSpace(core))
                return;

            AddAlias(token, emoji);

            string snake = core.Replace('-', '_');
            string kebab = core.Replace('_', '-');
            string compact = snake.Replace("_", string.Empty);

            AddAlias($":{snake}:", emoji);
            AddAlias($":{kebab}:", emoji);
            AddAlias($":{compact}:", emoji);

            if (snake.EndsWith("_emoji", StringComparison.OrdinalIgnoreCase))
            {
                string noSuffix = snake.Substring(0, snake.Length - "_emoji".Length);
                AddAlias($":{noSuffix}:", emoji);
                AddAlias($":{noSuffix.Replace("_", "-")}:", emoji);
            }

            if (snake.EndsWith("_face", StringComparison.OrdinalIgnoreCase))
            {
                string noFace = snake.Substring(0, snake.Length - "_face".Length);
                AddAlias($":{noFace}:", emoji);
            }
        }

        private static void AddAlias(string token, string emoji)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(emoji))
                return;

            if (!token.StartsWith(":")) token = ":" + token;
            if (!token.EndsWith(":")) token += ":";

            _emojiMap[token] = emoji;
        }

        /// <summary>
        /// Process kaomoji shortcodes in a message
        /// </summary>
        public static string ProcessKaomoji(string message)
        {
            if (string.IsNullOrEmpty(message) || _kaomojiMap == null)
                return message;

            // Skip work when the message clearly contains no shortcode markers.
            if (!message.Contains(":") && !message.Contains("<"))
                return message;

            string result = message;

            // Process longer tokens first so :broken_heart: wins over :heart:.
            if (_emojiMap != null)
            {
                foreach (var kvp in _emojiMap.OrderByDescending(pair => pair.Key.Length))
                {
                    if (result.Contains(kvp.Key))
                    {
                        result = result.Replace(kvp.Key, kvp.Value);
                    }
                }
            }
            
            // 2) Kaomoji fallback for shortcode tokens that do not have emoji mappings.
            foreach (var kvp in _kaomojiMap.OrderByDescending(pair => pair.Key.Length))
            {
                if (result.Contains(kvp.Key))
                {
                    result = result.Replace(kvp.Key, kvp.Value);
                }
            }
            
            return result;
        }

        /// <summary>
        /// Get all available kaomoji for display/help
        /// </summary>
        public static Dictionary<string, string> GetAllKaomoji()
        {
            return new Dictionary<string, string>(_kaomojiMap);
        }
    }
}
