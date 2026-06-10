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

        // Curated, de-duplicated, ordered lists used by the right-click chat picker.
        // Each entry is (insertToken, displayGlyph). The picker writes insertToken into the
        // chat box (shortcodes travel over the wire); ProcessKaomoji renders it locally.
        private static List<KeyValuePair<string, string>> _emojiPickerItems;
        private static List<KeyValuePair<string, string>> _kaomojiPickerItems;

        // Emoji map sorted longest-token-first, cached once at init so ProcessKaomoji
        // doesn't re-sort ~1500 entries on every chat message.
        private static List<KeyValuePair<string, string>> _sortedEmoji;

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
                { ":pregnant_man:", "🫃" },

                // ---- Expanded set: faces ----
                { ":sweat_smile:", "😅" },
                { ":lmao:", "🤣" },
                { ":upside_down:", "🙃" },
                { ":relieved:", "😌" },
                { ":smirk:", "😏" },
                { ":unamused:", "😒" },
                { ":rolling_eyes:", "🙄" },
                { ":grimace:", "😬" },
                { ":pensive:", "😔" },
                { ":confused:", "😕" },
                { ":worried:", "😟" },
                { ":frown:", "☹️" },
                { ":fearful:", "😨" },
                { ":cold_sweat:", "😰" },
                { ":disappointed:", "😞" },
                { ":persevere:", "😣" },
                { ":tired_face:", "😫" },
                { ":weary:", "😩" },
                { ":no_mouth:", "😶" },
                { ":neutral:", "😐" },
                { ":expressionless:", "😑" },
                { ":hushed:", "😯" },
                { ":drooling:", "🤤" },
                { ":nauseated:", "🤢" },
                { ":vomit:", "🤮" },
                { ":sneezing:", "🤧" },
                { ":mask:", "😷" },
                { ":sick:", "🤒" },
                { ":head_bandage:", "🤕" },
                { ":dizzy_face:", "😵" },
                { ":exploding_head:", "🤯" },
                { ":cowboy:", "🤠" },
                { ":clown:", "🤡" },
                { ":nerd:", "🤓" },
                { ":monocle_face:", "🧐" },
                { ":zany:", "🤪" },
                { ":shush:", "🤫" },
                { ":hand_over_mouth:", "🤭" },
                { ":money_mouth:", "🤑" },
                { ":hug_face:", "🤗" },
                { ":star_struck:", "🤩" },
                { ":yum:", "😋" },
                { ":tongue_out:", "😛" },
                { ":tongue_wink:", "😜" },
                { ":tongue_closed:", "😝" },
                { ":zipper_mouth:", "🤐" },
                { ":smiling_imp:", "😈" },
                { ":imp:", "👿" },
                { ":halo:", "😇" },
                { ":robot:", "🤖" },
                { ":alien:", "👽" },

                // ---- Gestures / body ----
                { ":vulcan:", "🖖" },
                { ":call_me:", "🤙" },
                { ":crossed_fingers:", "🤞" },
                { ":metal:", "🤘" },
                { ":punch:", "👊" },
                { ":open_hands:", "👐" },
                { ":handshake:", "🤝" },
                { ":writing_hand:", "✍️" },
                { ":nail_polish:", "💅" },
                { ":selfie:", "🤳" },
                { ":middle_finger:", "🖕" },
                { ":raised_hand:", "✋" },
                { ":ear:", "👂" },
                { ":nose:", "👃" },
                { ":lips:", "💋" },
                { ":tongue:", "👅" },
                { ":brain:", "🧠" },
                { ":tooth:", "🦷" },
                { ":bone:", "🦴" },
                { ":footprints:", "👣" },

                // ---- Hearts / effects ----
                { ":brown_heart:", "🤎" },
                { ":revolving_hearts:", "💞" },
                { ":heartbeat:", "💓" },
                { ":heartpulse:", "💗" },
                { ":cupid:", "💘" },
                { ":gift_heart:", "💝" },
                { ":heart_exclamation:", "❣️" },
                { ":sweat_drops:", "💦" },
                { ":dizzy:", "💫" },
                { ":anger_symbol:", "💢" },
                { ":speech:", "💬" },
                { ":thought:", "💭" },
                { ":zzz:", "💤" },

                // ---- Animals ----
                { ":monkey:", "🐵" },
                { ":see_no_evil:", "🙈" },
                { ":hear_no_evil:", "🙉" },
                { ":speak_no_evil:", "🙊" },
                { ":fox:", "🦊" },
                { ":lion:", "🦁" },
                { ":tiger:", "🐯" },
                { ":panda:", "🐼" },
                { ":koala:", "🐨" },
                { ":pig:", "🐷" },
                { ":frog:", "🐸" },
                { ":chicken:", "🐔" },
                { ":penguin:", "🐧" },
                { ":duck:", "🦆" },
                { ":owl:", "🦉" },
                { ":bat:", "🦇" },
                { ":wolf:", "🐺" },
                { ":horse:", "🐴" },
                { ":unicorn:", "🦄" },
                { ":bee:", "🐝" },
                { ":butterfly:", "🦋" },
                { ":snail:", "🐌" },
                { ":snake:", "🐍" },
                { ":turtle:", "🐢" },
                { ":crab:", "🦀" },
                { ":octopus:", "🐙" },
                { ":whale:", "🐳" },
                { ":dolphin:", "🐬" },
                { ":shark:", "🦈" },
                { ":fish:", "🐟" },
                { ":goat:", "🐐" },
                { ":gorilla:", "🦍" },
                { ":hamster:", "🐹" },
                { ":mouse:", "🐭" },
                { ":rabbit:", "🐰" },
                { ":eagle:", "🦅" },
                { ":dragon:", "🐉" },

                // ---- Food / drink ----
                { ":pizza:", "🍕" },
                { ":burger:", "🍔" },
                { ":fries:", "🍟" },
                { ":hotdog:", "🌭" },
                { ":taco:", "🌮" },
                { ":burrito:", "🌯" },
                { ":popcorn:", "🍿" },
                { ":donut:", "🍩" },
                { ":cookie:", "🍪" },
                { ":cake:", "🍰" },
                { ":birthday:", "🎂" },
                { ":icecream:", "🍦" },
                { ":candy:", "🍬" },
                { ":chocolate:", "🍫" },
                { ":apple:", "🍎" },
                { ":banana:", "🍌" },
                { ":watermelon:", "🍉" },
                { ":grapes:", "🍇" },
                { ":strawberry:", "🍓" },
                { ":peach:", "🍑" },
                { ":eggplant:", "🍆" },
                { ":avocado:", "🥑" },
                { ":corn:", "🌽" },
                { ":hot_pepper:", "🌶️" },
                { ":egg:", "🥚" },
                { ":bacon:", "🥓" },
                { ":pancakes:", "🥞" },
                { ":bread:", "🍞" },
                { ":cheese:", "🧀" },
                { ":ramen:", "🍜" },
                { ":sushi:", "🍣" },
                { ":rice:", "🍚" },
                { ":coffee:", "☕" },
                { ":tea:", "🍵" },
                { ":beer:", "🍺" },
                { ":beers:", "🍻" },
                { ":wine:", "🍷" },
                { ":cocktail:", "🍸" },
                { ":tropical_drink:", "🍹" },
                { ":champagne:", "🍾" },
                { ":milk:", "🥛" },

                // ---- Sports / activities ----
                { ":goal:", "🥅" },
                { ":basketball:", "🏀" },
                { ":football:", "🏈" },
                { ":baseball:", "⚾" },
                { ":tennis:", "🎾" },
                { ":volleyball:", "🏐" },
                { ":8ball:", "🎱" },
                { ":golf:", "⛳" },
                { ":bowling:", "🎳" },
                { ":dart:", "🎯" },
                { ":gamepad:", "🎮" },
                { ":dice:", "🎲" },
                { ":first_place:", "🥇" },
                { ":second_place:", "🥈" },
                { ":third_place:", "🥉" },
                { ":running:", "🏃" },
                { ":weight_lift:", "🏋️" },
                { ":skate:", "⛸️" },
                { ":ski:", "🎿" },
                { ":guitar:", "🎸" },
                { ":microphone:", "🎤" },
                { ":headphones:", "🎧" },
                { ":drum:", "🥁" },
                { ":piano:", "🎹" },
                { ":trumpet:", "🎺" },
                { ":violin:", "🎻" },
                { ":art:", "🎨" },
                { ":clapper:", "🎬" },

                // ---- Objects ----
                { ":crown:", "👑" },
                { ":gem:", "💎" },
                { ":ring:", "💍" },
                { ":bell:", "🔔" },
                { ":key:", "🔑" },
                { ":lock:", "🔒" },
                { ":unlock:", "🔓" },
                { ":hammer:", "🔨" },
                { ":wrench:", "🔧" },
                { ":gear:", "⚙️" },
                { ":knife:", "🔪" },
                { ":gun:", "🔫" },
                { ":bomb:", "💣" },
                { ":shield:", "🛡️" },
                { ":crossed_swords:", "⚔️" },
                { ":bow_arrow:", "🏹" },
                { ":pill:", "💊" },
                { ":syringe:", "💉" },
                { ":microscope:", "🔬" },
                { ":telescope:", "🔭" },
                { ":flashlight:", "🔦" },
                { ":candle:", "🕯️" },
                { ":bulb:", "💡" },
                { ":battery:", "🔋" },
                { ":plug:", "🔌" },
                { ":computer:", "💻" },
                { ":keyboard:", "⌨️" },
                { ":phone:", "📱" },
                { ":camera:", "📷" },
                { ":tv:", "📺" },
                { ":alarm:", "⏰" },
                { ":hourglass:", "⌛" },
                { ":stopwatch:", "⏱️" },
                { ":money:", "💰" },
                { ":dollar:", "💵" },
                { ":credit_card:", "💳" },
                { ":chart_up:", "📈" },
                { ":chart_down:", "📉" },
                { ":package:", "📦" },
                { ":pencil:", "✏️" },
                { ":book:", "📖" },
                { ":books:", "📚" },
                { ":newspaper:", "📰" },
                { ":scissors:", "✂️" },
                { ":paperclip:", "📎" },
                { ":pushpin:", "📌" },
                { ":trash:", "🗑️" },
                { ":door:", "🚪" },
                { ":bed:", "🛏️" },

                // ---- Nature / weather / travel ----
                { ":cloud:", "☁️" },
                { ":partly_sunny:", "⛅" },
                { ":rain:", "🌧️" },
                { ":thunder:", "⛈️" },
                { ":snowman:", "⛄" },
                { ":wind:", "💨" },
                { ":droplet:", "💧" },
                { ":ocean:", "🌊" },
                { ":tornado:", "🌪️" },
                { ":umbrella:", "☂️" },
                { ":comet:", "☄️" },
                { ":earth:", "🌍" },
                { ":full_moon:", "🌕" },
                { ":star2:", "🌟" },
                { ":shooting_star:", "🌠" },
                { ":tree:", "🌳" },
                { ":palm:", "🌴" },
                { ":cactus:", "🌵" },
                { ":clover:", "🍀" },
                { ":maple_leaf:", "🍁" },
                { ":leaves:", "🍃" },
                { ":mushroom:", "🍄" },
                { ":rose:", "🌹" },
                { ":sunflower:", "🌻" },
                { ":tulip:", "🌷" },
                { ":blossom:", "🌸" },
                { ":bouquet:", "💐" },
                { ":seedling:", "🌱" },
                { ":volcano:", "🌋" },
                { ":mountain:", "⛰️" },
                { ":house:", "🏠" },
                { ":castle:", "🏰" },
                { ":stadium:", "🏟️" },
                { ":car:", "🚗" },
                { ":taxi:", "🚕" },
                { ":bus:", "🚌" },
                { ":truck:", "🚚" },
                { ":police_car:", "🚓" },
                { ":ambulance:", "🚑" },
                { ":fire_engine:", "🚒" },
                { ":bike:", "🚲" },
                { ":motorcycle:", "🏍️" },
                { ":train:", "🚆" },
                { ":airplane:", "✈️" },
                { ":helicopter:", "🚁" },
                { ":ship:", "🚢" },
                { ":sailboat:", "⛵" },
                { ":anchor:", "⚓" },
                { ":fuel:", "⛽" },
                { ":construction:", "🚧" },

                // ---- Flags / symbols / celebration ----
                { ":checkered_flag:", "🏁" },
                { ":red_flag:", "🚩" },
                { ":white_flag:", "🏳️" },
                { ":black_flag:", "🏴" },
                { ":pirate_flag:", "🏴‍☠️" },
                { ":radioactive:", "☢️" },
                { ":biohazard:", "☣️" },
                { ":recycle:", "♻️" },
                { ":peace_symbol:", "☮️" },
                { ":yin_yang:", "☯️" },
                { ":no_entry:", "⛔" },
                { ":prohibited:", "🚫" },
                { ":heavy_check:", "✔️" },
                { ":plus:", "➕" },
                { ":minus:", "➖" },
                { ":divide:", "➗" },
                { ":multiply:", "✖️" },
                { ":red_circle:", "🔴" },
                { ":blue_circle:", "🔵" },
                { ":balloon:", "🎈" },
                { ":confetti:", "🎊" },
                { ":gift:", "🎁" },
                { ":fireworks:", "🎆" },
                { ":christmas_tree:", "🎄" },
                { ":pumpkin:", "🎃" },
                { ":crystal_ball:", "🔮" },
                { ":santa:", "🎅" },

                // ---- People / fantasy ----
                { ":baby:", "👶" },
                { ":boy:", "👦" },
                { ":girl:", "👧" },
                { ":man:", "👨" },
                { ":woman:", "👩" },
                { ":old_man:", "👴" },
                { ":old_woman:", "👵" },
                { ":police:", "👮" },
                { ":detective:", "🕵️" },
                { ":zombie:", "🧟" },
                { ":vampire:", "🧛" },
                { ":mage:", "🧙" },
                { ":fairy:", "🧚" },
                { ":genie:", "🧞" },
                { ":mermaid:", "🧜" },
                { ":elf:", "🧝" }
            };

            // Snapshot the curated emoji set for the picker BEFORE we expand the
            // map with hundreds of auto-generated aliases. De-dup by glyph so each
            // emoji shows once (keeping the first/cleanest shortcode for insertion).
            _emojiPickerItems = BuildPickerItems(_emojiMap, null);

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
                { ":fistbump:", "╰(◕ヮ◕)つ¤=[]:::::::>" },

                // ---- Expanded set ----
                { ":uwu:", "UwU" },
                { ":owo:", "OwO" },
                { ":rageflip:", "(ノಠ益ಠ)ノ彡┻━┻" },
                { ":doubleflip:", "┻━┻︵ \\(°□°)/ ︵ ┻━┻" },
                { ":strong:", "ᕦ(ò_óˇ)ᕤ" },
                { ":gimme:", "༼ つ ◕_◕ ༽つ" },
                { ":why:", "ლ(ಠ益ಠლ)" },
                { ":weep:", "(ノ_<。)" },
                { ":happydance:", "♪♪ ヽ(ˇ∀ˇ )ゞ" },
                { ":finger_guns:", "(☞ﾟヮﾟ)☞" },
                { ":sparkleeyes:", "(☆▽☆)" },
                { ":inlove:", "(´∀｀)♡" },
                { ":singing:", "♪(´ε｀ )" },
                { ":cheers:", "( ^_^)o自自o(^_^ )" },
                { ":hide:", "|ω・）" },
                { ":peek:", "┬┴┬┴┤(･_├┬┴┬┴" },
                { ":judging:", "(¬¬ )" },
                { ":screaming:", "ヽ(ﾟДﾟ)ﾉ" },
                { ":whatever:", "╮(╯▽╰)╭" },
                { ":sorry:", "(シ_ _)シ" },
                { ":please:", "(人◕ω◕)" },
                { ":success:", "(•̀ᴗ•́)و" }
            };

            // Snapshot the kaomoji set for the picker (de-dup by glyph) BEFORE adding
            // the alias namespace below.
            _kaomojiPickerItems = BuildPickerItems(_kaomojiMap, ":kao_");

            // Many kaomoji shortcodes (:happy:, :shrug:, :wave:, ...) collide with emoji
            // shortcodes, and ProcessKaomoji applies the emoji map first - so those kaomoji
            // were unreachable. Register a collision-proof ":kao_<name>:" alias for every
            // kaomoji so the picker (and power users) can always force the text-face variant.
            foreach (var kvp in _kaomojiMap.ToList())
            {
                string core = kvp.Key.Trim(':');
                if (string.IsNullOrWhiteSpace(core)) continue;
                string alias = ":kao_" + core + ":";
                if (!_kaomojiMap.ContainsKey(alias))
                    _kaomojiMap[alias] = kvp.Value;
            }

            _sortedEmoji = _emojiMap.OrderByDescending(pair => pair.Key.Length).ToList();

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
        /// Replace EMOJI shortcodes with their unicode glyphs. Emoji render in colour via the game's
        /// panel emoji fallback. Kaomoji shortcodes are intentionally LEFT INTACT here: the game font
        /// can't draw them (they'd be tofu boxes), so CustomEmojiPack renders them as inline images
        /// instead - see TryGetKaomojiGlyph.
        /// </summary>
        public static string ProcessKaomoji(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;

            // Skip work when the message clearly contains no shortcode markers.
            if (!message.Contains(":") && !message.Contains("<"))
                return message;

            string result = message;

            // Longer tokens first so :broken_heart: wins over :heart: (list pre-sorted at init).
            var sorted = _sortedEmoji;
            if (sorted != null)
            {
                foreach (var kvp in sorted)
                {
                    if (result.Contains(kvp.Key))
                    {
                        result = result.Replace(kvp.Key, kvp.Value);
                    }
                }
            }

            return result;
        }

        /// <summary>Look up the kaomoji face for a shortcode token (incl. the :kao_*: namespace).</summary>
        public static bool TryGetKaomojiGlyph(string token, out string glyph)
        {
            glyph = null;
            if (_kaomojiMap == null || string.IsNullOrEmpty(token))
                return false;
            return _kaomojiMap.TryGetValue(token, out glyph);
        }

        /// <summary>
        /// Get all available kaomoji for display/help
        /// </summary>
        public static Dictionary<string, string> GetAllKaomoji()
        {
            return new Dictionary<string, string>(_kaomojiMap);
        }

        /// <summary>
        /// Build an ordered, glyph-de-duplicated picker list from a shortcode map.
        /// When <paramref name="insertPrefix"/> is set (e.g. ":kao_"), the insert token is
        /// rewritten to that collision-proof namespace; otherwise the original key is used.
        /// </summary>
        private static List<KeyValuePair<string, string>> BuildPickerItems(
            Dictionary<string, string> source, string insertPrefix)
        {
            var items = new List<KeyValuePair<string, string>>();
            if (source == null) return items;

            var seenGlyphs = new HashSet<string>();
            foreach (var kvp in source)
            {
                if (string.IsNullOrEmpty(kvp.Value) || !seenGlyphs.Add(kvp.Value))
                    continue;

                string token = kvp.Key;
                if (!string.IsNullOrEmpty(insertPrefix))
                    token = insertPrefix + kvp.Key.Trim(':') + ":";

                items.Add(new KeyValuePair<string, string>(token, kvp.Value));
            }
            return items;
        }

        /// <summary>Curated emoji entries for the right-click picker: (insertToken, glyph).</summary>
        public static List<KeyValuePair<string, string>> GetEmojiPickerItems()
        {
            return _emojiPickerItems != null
                ? new List<KeyValuePair<string, string>>(_emojiPickerItems)
                : new List<KeyValuePair<string, string>>();
        }

        /// <summary>Curated kaomoji entries for the right-click picker: (insertToken, glyph).</summary>
        public static List<KeyValuePair<string, string>> GetKaomojiPickerItems()
        {
            return _kaomojiPickerItems != null
                ? new List<KeyValuePair<string, string>>(_kaomojiPickerItems)
                : new List<KeyValuePair<string, string>>();
        }
    }
}
