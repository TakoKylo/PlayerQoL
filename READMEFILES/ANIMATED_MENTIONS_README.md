# Animated Talking Indicator & @Mention System

## Overview
This document describes the animated talking indicator and @mention highlighting system implemented in the mod.

## 1. Animated Talking Indicator

### Feature Description
Shows **"PlayerName is talking..."** text above a player's character with animated cycling dots when they're using voice chat.

### Animation Pattern
The text cycles through these frames every 0.3 seconds:
- `PlayerName is talking`
- `PlayerName is talking.`
- `PlayerName is talking..`
- `PlayerName is talking...`

### Technical Implementation
- **File**: `PlayerActivityIndicator.cs`
- **Attachment Point**: `PlayerBody.transform` (character model) at offset (0, 2.2f, 0)
- **Text Display**: TextMeshPro with billboard component (always faces camera)
- **Color**: Green (`RGB: 51, 255, 51`)
- **Font Size**: 0.8 (scaled for readability)
- **Performance**: 
  - Player caching (2 second intervals)
  - Animation updates only while player is talking
  - Indicators automatically destroyed when player stops talking

### Voice Detection
- Uses `PlayerVoiceRecorder.IsRecording` field
- Dual detection system:
  1. RPC hook: `Voice_Receive_Prefix` patches voice data reception
  2. Direct polling: `IsTalking()` checks `IsRecording` every 0.5 seconds

## 2. @Mention System

### Feature Description
Automatically highlights @mentions in chat messages when they're relevant to you.

### Supported Mention Types

| Mention | Triggers When | Example |
|---------|--------------|---------|
| `@playername` | Your username is mentioned | `@John nice shot!` |
| `@everyone` | Always highlights for all players | `@everyone good game` |
| `@here` | You're NOT spectating (on ice) | `@here let's go!` |
| `@red` | You're on Red team | `@red defend the goal` |
| `@blue` | You're on Blue team | `@blue nice pass` |
| `@spec` | You're spectating | `@spec who won?` |
| `@admin` | You have [Admin] or [A] tag | `@admin can you restart?` |
| `@donor` | You have [Donor] or [D] tag | `@donor thanks for support` |

### Visual Highlighting
- **Color**: Yellow (`#FFFF00`)
- **Format**: `<color=#FFFF00>@mention</color>`
- Only highlights mentions that apply to you
- Mentions that don't apply remain normal text color

### Technical Implementation
- **File**: `LocalMuteClientMod.cs`
- **Method**: `ProcessMentions(string message)`
- **Hook**: `Chat_AddMessage_Postfix` (Harmony Postfix patch on `UIChat.AddChatMessage`)
- **Matching**: Case-insensitive, alphanumeric + underscore
- **Username Stripping**: Uses `StripRichText()` to handle rich text usernames
- **Rich Text Injection**: Directly modifies the Label element after it's added to the chat ScrollView

### How It Works
1. **Message Added**: UIChat.AddChatMessage() adds a new Label to the chat ScrollView
2. **Postfix Intercept**: Our postfix patch runs immediately after
3. **Label Access**: Gets the last Label that was just added to the ScrollView
4. **Local Player Lookup**: Gets your Player object via `PlayerManager.Instance.GetLocalPlayer()`
5. **Role Detection**: Determines your team, spectator status, admin/donor tags
6. **Mention Scanning**: Finds all `@word` patterns in the Label's text
7. **Relevance Check**: Compares each mention against your roles
8. **Highlighting**: Wraps relevant mentions in yellow color tags and updates Label.text
9. **Rich Text Enable**: Ensures `enableRichText = true` on the Label to render color tags

### Edge Cases
- Mentions in rich text (colors, bold, etc.) are handled correctly
- Username comparison is case-insensitive
- Partial usernames don't match (must be full username)
- Multiple mentions in one message all processed correctly
- Mentions at start/middle/end of message work identically
- Game's `<noparse>` tags are bypassed by modifying the Label after it's created
- Works with both system messages and player chat messages

## Testing

### Talking Indicator Test
1. Join game server
2. Enable voice chat
3. Speak into microphone
4. Look at your character - should see "YourName is talking..." with cycling dots
5. Stop speaking - text should disappear
6. Look at other players speaking - should see their names with animated dots

### @Mention Test
Test each mention type by having someone send these messages in chat:

```
@yourname hello!          // Should highlight if your name is "yourname"
@everyone test            // Always highlights
@here anyone?             // Highlights if not spectating
@red go team!             // Highlights if on red team
@blue nice!               // Highlights if on blue team  
@spec watching?           // Highlights if spectating
@admin help needed        // Highlights if you have admin tag
@donor thank you          // Highlights if you have donor tag
```

### Performance Notes
- Mention processing is lightweight (string parsing only)
- No network traffic generated (client-side only)
- Player caching prevents repeated FindObjectsByType calls
- Animation updates use deltaTime for smooth frame transitions

## Code Locations

### Animated Indicator
- `PlayerActivityIndicator.cs` - Lines 1-260
  - `IndicatorData` class: Stores TextMeshPro, player name, animation state
  - `UpdateIndicators()`: Main update loop
  - `CreateIndicator()`: Creates animated text above player
  - `UpdateIndicatorAnimation()`: Cycles through dot frames
  - Animation frames array: `_animationFrames`
  - Animation speed: `_animationSpeed = 0.3f`

### @Mention System
- `LocalMuteClientMod.cs` - Lines ~530-815
  - `Chat_AddMessage_Postfix()`: Harmony Postfix that intercepts Label creation
  - `ProcessMentions()`: Main mention processing logic  
  - `Patch_Chat_AddMessage()`: Applies the Harmony patch to UIChat.AddChatMessage
  - Special mention checks for: everyone, here, red, blue, spec, admin, donor
  - Username matching with case-insensitive comparison
  - Yellow highlight application via direct Label.text modification
  - Enables rich text on the Label element

## Known Limitations
1. Typing indicators not supported (game doesn't network typing state)
2. @mention highlighting is client-side only (sender sees normal text, only you see your relevant mentions highlighted)
3. Voice detection requires PlayerVoiceRecorder component (standard in game)
4. Indicator position fixed at 2.2 units above player (not customizable in-game)
5. Animation speed fixed at 0.5 seconds per frame (not configurable)
6. @mentions only highlight for the recipient - each player sees different highlights based on their own role

## Future Enhancements
Potential improvements if needed:
- Configurable indicator height/size
- Adjustable animation speed
- Custom highlight colors per mention type
- Sound notification for @username mentions
- Message history with persistent highlights
- @reply system to quote/respond to mentions
