// Chat.RichTextToggle.cs - Patch to strip rich text from PLAYER chat messages when disabled

using HarmonyLib;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEngine;

namespace PoncePuck.Keybinds
{
    [HarmonyPatch(typeof(UIChat), "AddChatMessage")]
    public static class ChatRichTextPatch
    {
        // B310: AddChatMessage(ChatMessage, Units, bool) - ChatMessage has FixedString512Bytes Content
        static void Prefix(ref ChatMessage chatMessage)
        {
            try
            {
                string message = chatMessage.Content.ToString();
                
                // Only process if rich text is disabled
                var runner = Object.FindFirstObjectByType<KeybindRunner>();
                if (runner != null)
                {
                    // Use reflection to access private _cmd field
                    var cmdField = typeof(KeybindRunner).GetField("_cmd", 
                        System.Reflection.BindingFlags.Instance | 
                        System.Reflection.BindingFlags.NonPublic);
                    
                    if (cmdField != null)
                    {
                        var cmd = cmdField.GetValue(runner) as CommandKeybindConfig;
                        if (cmd != null && !cmd.enableChatRichText)
                        {
                            // Only strip rich text from player messages (those with team colors and player numbers)
                            // System messages don't have the team color wrapper
                            if (IsPlayerMessage(message))
                            {
                                // Strip only the message content, preserve team color and player info
                                message = StripPlayerMessageRichText(message);
                                chatMessage.Content = new FixedString512Bytes(message);
                            }
                        }
                    }
                }
            }
            catch { }
        }
        
        private static bool IsPlayerMessage(string message)
        {
            // Player messages have this format: <b><color=#...><noparse>#X PlayerName</noparse></color></b>: message content
            // Check if it contains player formatting
            return message.Contains("<noparse>") && message.Contains("</noparse>") && message.Contains(": ");
        }
        
        private static string StripPlayerMessageRichText(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            
            // Split at ": " to separate player name from message content
            int colonIndex = message.IndexOf(": ");
            if (colonIndex > 0)
            {
                string playerPart = message.Substring(0, colonIndex + 2); // Keep ": "
                string messagePart = message.Substring(colonIndex + 2);
                
                // Strip rich text only from the message part
                messagePart = Regex.Replace(messagePart, @"<[^>]+>", "");
                
                return playerPart + messagePart;
            }
            
            return message;
        }
    }
}
