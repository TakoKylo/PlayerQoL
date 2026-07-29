// Chat.EmojiBlurSync.cs - keep inline custom-emoji content fading in step with its chat row.

using HarmonyLib;
using PoncePuck.LocalMute;

namespace PoncePuck.Keybinds
{
    // UIChatMessage fades an expired message by toggling the ".blurred" USS class on the row's own
    // Label. Rows that contain custom emoji render their content into a sibling wrapper instead
    // (see CustomEmojiPack.TryApplyInlineEmojis - a Label cannot host child elements without
    // collapsing the row), so the class lands on a hidden element and the emoji content would stay
    // at full opacity long after every other message has faded. Mirror the label's class onto the
    // wrapper after each Focus/Blur.
    //
    // Reading the class back off the label rather than inferring it from which method ran is
    // deliberate: Blur() returns early and only restarts its expiry tween when the message isn't
    // expired yet, so the method called is not the same as the state applied.

    [HarmonyPatch(typeof(UIChatMessage), nameof(UIChatMessage.Focus))]
    internal static class UIChatMessageFocusPatch
    {
        private static void Postfix(UIChatMessage __instance)
        {
            CustomEmojiPack.SyncBlurState(__instance?.VisualElement);
        }
    }

    [HarmonyPatch(typeof(UIChatMessage), nameof(UIChatMessage.Blur))]
    internal static class UIChatMessageBlurPatch
    {
        private static void Postfix(UIChatMessage __instance)
        {
            CustomEmojiPack.SyncBlurState(__instance?.VisualElement);
        }
    }
}
