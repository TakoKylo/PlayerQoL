using System.Reflection;
using Unity.Collections;
using UnityEngine;

namespace PoncePuck.Keybinds
{
    public partial class KeybindRunner
    {
        private void SafeEcho(string t)
        {
            Debug.Log("[PPKB] " + t);
            try
            {
                // B310: Use ChatManager.AddChatMessage(ChatMessage)
                var chatMgr = NetworkBehaviourSingleton<ChatManager>.Instance;
                if (chatMgr != null)
                {
                    var msg = new ChatMessage();
                    msg.Content = new FixedString512Bytes(t);
                    msg.IsSystem = true;
                    chatMgr.AddChatMessage(msg);
                    return;
                }
                // Fallback: reflection on UIChat
                var ui = GetUIChatObj();
                var add = ui != null ? ui.GetType().GetMethod("AddChatMessage",
                              BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                if (add != null) add.Invoke(ui, new object[] { t });
            }
            catch { }
        }
    }
}
