# Puck 202 → 310 API Migration Guide

> **File counts:** 202 had 266 .cs files, 310 has 393 .cs files (+127 new files, -53 removed)

---

## Table of Contents

1. [Critical Breaking Changes (Fix These First)](#1-critical-breaking-changes)
2. [Architectural Shifts](#2-architectural-shifts)
3. [Renamed Classes & Files](#3-renamed-classes--files)
4. [Removed Classes](#4-removed-classes)
5. [New Classes & Systems](#5-new-classes--systems)
6. [EventManager Overhaul](#6-eventmanager-overhaul)
7. [Player System Changes](#7-player-system-changes)
8. [Game Manager Changes](#8-game-manager-changes)
9. [Server & Networking Changes](#9-server--networking-changes)
10. [Settings & Configuration Changes](#10-settings--configuration-changes)
11. [UI System Changes](#11-ui-system-changes)
12. [Enum Changes](#12-enum-changes)
13. [Cosmetics System Changes](#13-cosmetics-system-changes)
14. [Mod System Changes](#14-mod-system-changes)
15. [New Game Mode System](#15-new-game-mode-system)
16. [Complete Event Name Migration Table](#16-complete-event-name-migration-table)

---

## 1. Critical Breaking Changes

These will cause compile errors immediately. Fix these first.

### EventManager: Instance → Static

The single most pervasive change. Every call site must be updated.

```csharp
// 202 ❌
MonoBehaviourSingleton<EventManager>.Instance.TriggerEvent("Event_OnFoo", msg);
MonoBehaviourSingleton<EventManager>.Instance.AddEventListener("Event_OnFoo", handler);
MonoBehaviourSingleton<EventManager>.Instance.RemoveEventListener("Event_OnFoo", handler);

// 310 ✅
EventManager.TriggerEvent("Event_OnFoo", msg);
EventManager.AddEventListener("Event_OnFoo", handler);
EventManager.RemoveEventListener("Event_OnFoo", handler);
```

**Also removed:** `AddAnyEventListener()` and `RemoveAnyEventListener()` — the "wildcard listener" feature is gone entirely.

### PlayerState: Enum → Struct (Complete Type Change)

```csharp
// 202 ❌ — was an enum
PlayerState.Play
PlayerState.TeamSelect
PlayerState.Spectate
player.State.Value  // NetworkVariable<PlayerState>

// 310 ✅ — now a struct (used for backend state)
// For the old enum values, use PlayerPhase instead:
PlayerPhase.Play
PlayerPhase.TeamSelect
PlayerPhase.Spectate
player.Phase         // property on Player (from GameState.Value.Phase)
```

**New `PlayerPhase` enum values:** `None`, `TeamSelect`, `PositionSelect`, `Play`, `Replay`, `Spectate`

### UIState: Enum → Struct (Complete Type Change)

```csharp
// 202 ❌
UIState.MainMenu
UIState.Play

// 310 ✅ — now a struct
GlobalStateManager.UIState.Phase  // UIPhase enum
// UIPhase replaces the old enum (check UIPhase.cs for values)
```

### PlayerTeam: Enum Value Order Changed

```csharp
// 202 — None=0, Spectator=1, Blue=2, Red=3
// 310 — None=0, Blue=1, Red=2, Spectator=3
```

**Breaking if you cast to/from int.** Named references (`PlayerTeam.Blue`) still work.

### MonoBehaviourSingleton.Instance Setter Removed

```csharp
// 202 ❌
MonoBehaviourSingleton<Foo>.Instance = someInstance;

// 310 ✅ — Instance is now get-only, assigned automatically in Awake()
// Cannot manually set Instance anymore
```

### MonoBehaviourSingleton.DestroyOnLoad() Renamed

```csharp
// 202 ❌
singleton.DestroyOnLoad();

// 310 ✅
singleton.AllowSceneDestruction();
```

---

## 2. Architectural Shifts

These patterns affect virtually every file in the codebase.

### Many Singletons Became Static Classes

| 202 (Instance-based) | 310 (Static) |
|---|---|
| `MonoBehaviourSingleton<EventManager>.Instance` | `EventManager` (static class) |
| `MonoBehaviourSingleton<InputManager>.Instance` | `InputManager` (static) |
| `MonoBehaviourSingleton<SettingsManager>.Instance` | `SettingsManager` (static) |
| `MonoBehaviourSingleton<ItemManager>.Instance` | `ItemManager` (static) |
| `MonoBehaviourSingleton<SceneManager>` (NetworkBehaviour) | `SceneManager` (static) |
| `WebSocketManager : MonoBehaviourSingleton<>` | `WebSocketManager` (static) |

**Access pattern change for all of these:**
```csharp
// 202 ❌
MonoBehaviourSingleton<SettingsManager>.Instance.CameraAngle

// 310 ✅
SettingsManager.CameraAngle
```

### Many Managers: NetworkBehaviour → MonoBehaviour

These no longer extend `NetworkBehaviour`:

| Class | 202 Base | 310 Base |
|---|---|---|
| `PlayerManager` | `NetworkBehaviourSingleton<>` | `MonoBehaviourSingleton<>` |
| `UIManager` | `NetworkBehaviourSingleton<>` | `MonoBehaviourSingleton<>` |
| `VoteManager` | `NetworkBehaviourSingleton<>` | `MonoBehaviourSingleton<>` |
| `SpectatorManager` | `NetworkBehaviourSingleton<>` | `MonoBehaviourSingleton<>` |
| `PuckManager` | `NetworkBehaviourSingleton<>` | `MonoBehaviourSingleton<>` |
| `GameManagerController` | `NetworkBehaviour` | `MonoBehaviour` |
| `PlayerManagerController` | `NetworkBehaviour` | `MonoBehaviour` |
| `ServerManagerController` | `NetworkBehaviour` | `MonoBehaviour` |
| `UIManagerController` | `NetworkBehaviourSingleton<>` | `MonoBehaviour` |

### Coroutines → DOTween

Throughout the codebase, `IEnumerator` coroutines have been replaced with DOTween `Tween` objects:

```csharp
// 202 ❌
private IEnumerator gameStateTickCoroutine;
StartCoroutine(IGameStateTick());

// 310 ✅
private Tween tickTween;
DOVirtual.DelayedCall(1f, OnTick).SetLoops(-1);
```

### NetworkVariable Lazy Initialization

NetworkVariables are no longer eagerly initialized at field declaration. They use a new pattern:

```csharp
// 202 ❌
public NetworkVariable<bool> IsReplay = new NetworkVariable<bool>(false, ...);

// 310 ✅
public NetworkVariable<bool> IsReplay;
private bool isNetworkVariablesInitialized;

public void InitializeNetworkVariables(bool isReplay = false) {
    if (isNetworkVariablesInitialized) return;
    isNetworkVariablesInitialized = true;
    IsReplay = new NetworkVariable<bool>(isReplay, ...);
}

protected override void OnNetworkPreSpawn(ref NetworkManager nm) {
    InitializeNetworkVariables();
}
```

---

## 3. Renamed Classes & Files

| 202 Name | 310 Name |
|---|---|
| `PlayerBodyV2` | `PlayerBody` |
| `PlayerBodyV2Controller` | `PlayerBodyController` |
| `ChangingRoomManager` | `LockerRoom` |
| `ChangingRoomManagerController` | `LockerRoomController` |
| `ChangingRoomPlayer` | `LockerRoomPlayer` |
| `ChangingRoomPlayerController` | `LockerRoomPlayerController` |
| `ChangingRoomStick` | `LockerRoomStick` |
| `ChangingRoomStickController` | `LockerRoomStickController` |
| `ModManagerV2` | `ModManager` |
| `ModManagerControllerV2` | `ModManagerController` |
| `ModConfiguration` | `ModConfig` |
| `NetworkObjectCollisionBuffer` | `NetworkObjectCollisionRecorder` |
| `PopupContentPassword` | `PopupPasswordContent` |
| `PopupContentText` | `PopupTextContent` |
| `PostProcessingManager` | `PostProcessing` |
| `PostProcessingManagerController` | `PostProcessingController` |
| `ServerConfiguration` | `ServerConfig` |
| `ServerBrowserServer` | `EndPoint` |
| `UIAnnouncement` | `UIAnnouncements` |
| `UIAnnouncementController` | `UIAnnouncementsController` |
| `UIComponent` | `UIView` |
| `UIServerLauncher` | `UINewServer` |
| `UIServerLauncherController` | `UINewServerController` |
| `LevelManager` | `Level` |
| `LevelManagerController` | `LevelController` |
| `StateManager` | `GlobalStateManager` (static) |
| `DependencyManager` | (removed — see EdgegapDependency) |
| `ServerConfigurationManager` | `ServerConfig` (plain class) |
| `KeyBindControl` | `KeyBindField` |
| `SortType` | `ServerSortType` |
| `Scene` (enum) | (removed — scenes loaded by string name now) |

---

## 4. Removed Classes

These files exist in 202 but have no equivalent in 310:

| Removed Class | What Happened |
|---|---|
| `PuckShooter` / `PuckShooterController` | Removed (debug shooting) |
| `PurchaseManager` / `PurchaseManagerController` | Replaced by new transaction system |
| `PlayerCompletePurchaseResponse` | Replaced by `PlayerFinalizeTransactionResponse` |
| `PlayerStartPurchaseResponse` | Replaced by `PlayerStartTransactionResponse` |
| `PlayerLaunchServerResponse` | Replaced by `PlayerDeployServerResponse` |
| `ServerLauncherLocation` / `ServerLauncherLocationsResponse` | Replaced by `Location` / `PlayerGetLocationsResponse` |
| `ServerBrowserServer` / `ServerBrowserServersResponse` | Replaced by `EndPoint` / `ServerBrowserEndPointsResponse` |
| `DependencyManager` / `DependencyManagerController` | Logic absorbed into `LifecycleManager` |
| `StateManager` / `StateManagerController` | Replaced by `GlobalStateManager` (static) + `BackendManager` (static) |
| `NetworkManagerEventEmitter` | Removed — events handled directly |
| `NetworkObjectCollisionBuffer` | Renamed to `NetworkObjectCollisionRecorder` |
| `UDPSocket` | Removed — TCP replaces UDP for server preview |
| `StartDependencyTrigger` | Removed — `LifecycleManager` handles boot order |
| `UIComponent.cs` / `UIComponent.2.cs` | Replaced by `UIView` / `UIViewController` |
| `UIManagerInputs` | Input handling moved into `UIManager` directly |
| `UIManagerStateController` | State management moved to `GlobalStateManager` |
| `RotatingImage` | Replaced by `Spinner` |
| `PlayerHeadType` | Removed — headgear system changed |
| `PlayerPositionManager` / `PlayerPositionManagerController` | Position management absorbed elsewhere |

---

## 5. New Classes & Systems

### Major New Systems

| New Class | Purpose |
|---|---|
| **`BaseGameMode<TConfig>`** | Base class for custom game modes — **huge for modders** |
| **`StandardGameMode<TConfig>`** | Full game flow (periods, scoring, overtime) |
| **`PublicGameMode`** | Default public lobby mode |
| **`CompetitiveGameMode`** | Ranked/competitive mode with team assignments |
| **`GameModeManager`** | Manages game mode selection and lifecycle |
| **`AdminManager`** | Admin Steam ID management with file hot-reload |
| **`BanManager`** | Ban management (Steam IDs + IP addresses) with file hot-reload |
| **`WhitelistManager`** | Whitelist management with file hot-reload |
| **`ConnectionApprovalManager`** | Centralized connection gatekeeping |
| **`TimeoutManager`** | Temporary Steam ID timeouts |
| **`BackendManager`** | Static class for player/server/transaction state |
| **`GlobalStateManager`** | Static class for UI state and connection state |
| **`ApplicationManager`** | Static class for display, quality, system settings |
| **`CameraManager`** | Static class for camera registration/activation |
| **`ChatManager`** | NetworkBehaviour for chat messages and commands |
| **`SaveManager`** | Static wrapper around PlayerPrefs |
| **`LifecycleManager`** | Bootstrap initialization of all managers |
| **`PatchManager`** | Harmony patch management |
| **`TCPServer` / `TCPClient`** | New TCP networking for server preview |
| **`CompressedNetworkVariable<TRaw, TNetwork>`** | Compress floats to shorts/bytes for networking |

### New Data Types

| New Type | Purpose |
|---|---|
| `PlayerGameState` (struct) | Combines Phase + Team + Role into one NetworkVariable |
| `PlayerCustomizationState` (struct) | All 23 cosmetic int IDs in one NetworkVariable |
| `PlayerPhase` (enum) | Replaces old `PlayerState` enum |
| `UIPhase` (enum) | Replaces old `UIState` enum |
| `ConnectionState` (struct) | Connection tracking (isConnecting, isConnected, etc.) |
| `ServerState` (struct) | Server backend state |
| `TransactionState` (struct) | Purchase transaction state |
| `EndPoint` | Replaces `ServerBrowserServer` (ipAddress + port) |
| `ModConfig` | Replaces `ModConfiguration` (id, enabled, clientRequired) |
| `Response<TSuccess, TError>` | Generic API response wrapper |
| `EdgegapDependency` (enum) | Edgegap dependency tracking |
| `ApplicationQuality` (enum) | Quality levels |
| `AuthenticationPhase` (enum) | Auth state tracking |
| `CameraType` (enum) | Camera type identification |
| `GameResult` | Game result data |
| `Item` | Item definition (replaces string-based items) |
| `Level` | Level/map definition (replaces `LevelManager`) |
| `Location` | Server location |
| `Country` / `CountryUtils` | Country data |
| `InMessage` / `OutMessage` | WebSocket message wrappers |
| `QuickChat` / `QuickChatCategory` | Quick chat system |
| `Headgear` / `HeadgearRole` | New headgear system |
| `Jersey` / `JerseyTeam` | New jersey system |
| `StickSkin` / `StickSkinTeam` / `StickTape` | New stick cosmetics |
| `Beard` / `Mustache` / `Flag` | Facial hair and flag cosmetics |
| `AvatarSize` | Avatar size options |
| `Units` (enum) | Metric/Imperial |
| `ServerSortType` / `ServerSortDirection` | Server browser sorting |
| `Friend` / `User` | Social features |
| `MatchData` / `MatchPlayer` / `PlayerMatchData` | Match/matchmaking data |
| `Mmr` | MMR rating |
| `PlayButton` / `Icon` / `IconButton` | UI components |
| `Spinner` / `SpinnerDirection` | Loading spinner (replaces `RotatingImage`) |

---

## 6. EventManager Overhaul

### Class Structure Change
```csharp
// 202: Instance-based MonoBehaviourSingleton
public class EventManager : MonoBehaviourSingleton<EventManager>

// 310: Pure static class
public static class EventManager
```

### New Static Lifecycle
```csharp
EventManager.Initialize();  // Call once at startup
EventManager.Dispose();     // Call on shutdown
```

### Event Naming Convention Change

All event names were reorganized with new prefixes:

| Prefix | Meaning |
|---|---|
| `Event_Everyone_On*` | Fires on all clients and server |
| `Event_Server_On*` | Fires only on server |
| `Event_On*` | Client-local UI/input events (was `Event_Client_On*`) |

The old `Event_Client_On*` prefix is largely replaced by just `Event_On*`.

### Removed Feature
```csharp
// 202 ❌ — "any event" wildcard listener removed
eventManager.AddAnyEventListener(handler);
eventManager.RemoveAnyEventListener(handler);
// No replacement in 310
```

---

## 7. Player System Changes

### Player.cs — NetworkVariable Consolidation

Individual NetworkVariables collapsed into compound structs:

```csharp
// 202 ❌ — Individual NetworkVariables
player.State.Value          // NetworkVariable<PlayerState>
player.Team.Value           // NetworkVariable<PlayerTeam>
player.Role.Value           // NetworkVariable<PlayerRole>

// 310 ✅ — Compound struct
player.GameState.Value.Phase  // PlayerPhase (was PlayerState)
player.GameState.Value.Team   // PlayerTeam
player.GameState.Value.Role   // PlayerRole
// Or use convenience properties:
player.Phase                  // shorthand for GameState.Value.Phase
player.Team                   // shorthand for GameState.Value.Team
player.Role                   // shorthand for GameState.Value.Role
```

### Cosmetics: Strings → Int IDs

```csharp
// 202 ❌ — FixedString32Bytes per skin
player.JerseyAttackerBlueSkin.Value  // FixedString32Bytes
player.GetPlayerJerseySkin()         // returns FixedString32Bytes

// 310 ✅ — Int IDs in compound struct
player.CustomizationState.Value.JerseyIDBlueAttacker  // int
player.GetPlayerJerseyID()                             // returns int
```

### Player Spawning Properties

```csharp
// 202 ❌
player.IsCharacterFullySpawned
player.IsCharacterPartiallySpawned

// 310 ✅
player.IsCharacterSpawned  // single property replaces both
```

### Player Body Type Renamed

```csharp
// 202 ❌
player.PlayerBody  // type: PlayerBodyV2

// 310 ✅
player.PlayerBody  // type: PlayerBody
```

### Player Ping Type Changed
```csharp
// 202: NetworkVariable<int> Ping
// 310: NetworkVariable<ulong> Ping
```

### New Player Properties (310)
- `float ChatTickets` — rate-limited chat
- `bool IsChatAvailable` — `ChatTickets >= 1f`
- `NetworkVariable<bool> IsMuted` — new mutable state

### Player RPC Pattern Change

```csharp
// 202 ❌ — Server-authoritative "set" RPCs
player.Client_SetPlayerStateRpc(PlayerState.Play);
player.Client_SetPlayerTeamRpc(PlayerTeam.Blue);
player.Client_PlayerSubscriptionRpc(/* huge signature with all skins */);

// 310 ✅ — Client "request" RPCs (server validates)
player.Client_RequestTeamRpc(PlayerTeam.Blue, rpcParams);
player.Client_RequestClaimPositionRpc(positionRef, rpcParams);
player.Client_RequestTeamSelectRpc(rpcParams);
player.Client_RequestPositionSelectRpc(rpcParams);
player.Client_RequestHandednessRpc(handedness, rpcParams);
```

### Player State Setting (Server-Side)

```csharp
// 202 ❌
player.Client_SetPlayerStateRpc(PlayerState.Play, delay);

// 310 ✅
player.Server_SetGameState(
    phase: PlayerPhase.Play,
    team: null,    // null = don't change
    role: null,    // null = don't change
    delay: 0f
);
```

### PlayerManager Changes

```csharp
// 202 ❌
PlayerManager.Instance.Server_SpawnPlayer(clientId);
PlayerManager.Instance.GetSpawnedFirstPlayer();
PlayerManager.Instance.GetSpawnedLastPlayer();
PlayerManager.Instance.GetSpawnedNextPlayerByClientId(clientId);
PlayerManager.Instance.GetSpawnedPreviousPlayerByClientId(clientId);
PlayerManager.Instance.IsEnoughPlayersForPlaying();

// 310 ✅ — SpawnPlayer now requires full initialization data
PlayerManager.Instance.Server_SpawnPlayer(clientId, gameState, customizationState,
    handedness, steamID, username, number, patreonLevel, adminLevel, isMuted, isReplay);

// New query methods:
PlayerManager.Instance.GetPlayersByPhase(PlayerPhase.Play);
PlayerManager.Instance.GetPlayersByPhases(new[] { PlayerPhase.Play, PlayerPhase.Replay });
PlayerManager.Instance.GetPlayersByTeams(new[] { PlayerTeam.Blue, PlayerTeam.Red });
```

### PlayerData Changes

```csharp
// 202
playerData.lastUsernameChange  // double

// 310
playerData.usernameChangedAt   // double? (nullable)
playerData.mmr                 // int (new)
playerData.cooldowns           // PlayerCooldown[] (new)
```

---

## 8. Game Manager Changes

### GameState Struct Changes

```csharp
// 202
gameState.Time       // int
// IsOvertime was computed: gameState.Period > 3

// 310
gameState.Tick       // int (renamed from Time)
gameState.IsOvertime // bool (now a stored field)
```

### GameManager API Changes

```csharp
// 202 ❌
GameManager.Instance.Server_UpdateGameState(phase, time, period, blueScore, redScore);
GameManager.Instance.Server_SetPhase(GamePhase.FaceOff);
GameManager.Instance.Server_StartGame(warmup: true);
GameManager.Instance.Server_GameOver();
GameManager.Instance.Server_Pause();
GameManager.Instance.Server_Resume();
GameManager.Instance.Server_ResetGameState();
GameManager.Instance.IsFirstFaceOff;

// 310 ✅
GameManager.Instance.Server_SetGameState(phase, tick, period, blueScore, redScore, isOvertime);
// Server_SetPhase, Server_StartGame, Server_GameOver, Server_Pause, Server_Resume
// are ALL REMOVED — game flow is now managed by the GameMode system
// Convenience properties added:
GameManager.Instance.Tick
GameManager.Instance.Period
GameManager.Instance.BlueScore
GameManager.Instance.RedScore
```

### Goal Scoring RPC Changed

```csharp
// 202 ❌
Server_GoalScoredRpc(team, hasLastPlayer, lastPlayerClientId,
    hasGoalPlayer, goalPlayerClientId, hasAssistPlayer, assistPlayerClientId,
    hasSecondAssistPlayer, secondAssistPlayerClientId,
    speedAcrossLine, highestSpeedSinceStick, rpcParams);

// 310 ✅ — Uses NetworkObjectReference instead of bool+clientId pairs
Server_NotifyGoalScoredRpc(byTeam,
    goalPlayerNetworkObjectReference,
    assistPlayerNetworkObjectReference,
    secondAssistPlayerNetworkObjectReference,
    puckNetworkObjectReference);
```

### GameManagerController Gutted

Almost all game logic was removed from `GameManagerController` in 310. Goal scoring, phase transitions, vote handling, and chat commands have moved to the **Game Mode system** (`BaseGameMode` / `StandardGameMode`).

---

## 9. Server & Networking Changes

### Server Struct Simplified

```csharp
// 202 — 15 fields
Server {
    IpAddress, Port, PingPort, Name, MaxPlayers, Password,
    Voip, IsPublic, IsDedicated, IsHosted, IsAuthenticated,
    OwnerSteamId, SleepTimeout, ClientTickRate, ClientRequiredModIds
}

// 310 — 6 fields
Server {
    IpAddress, Port, Name, MaxPlayers, TickRate, UseVoip
}
```

`Server` is now a `NetworkVariable<Server>` on `ServerManager` (auto-synced), replacing the old RPC-based configuration push.

### ServerManager Decomposition

Functionality extracted to dedicated managers:

| 202 (on ServerManager) | 310 (new home) |
|---|---|
| `AdminSteamIds`, `LoadAdminSteamIds()` | `AdminManager` |
| `BannedSteamIds`, `AddBannedSteamId()` | `BanManager` |
| `SteamIdTimeouts`, `Server_TimeoutSteamId()` | `TimeoutManager` |
| `ConnectionApprovalRequests`, `Server_ConnectionApproval()` | `ConnectionApprovalManager` |
| `UDPSocket` (ping) | Removed entirely |

### Server Start Methods

```csharp
// 202 ❌
serverManager.Client_StartHost(port, name, maxPlayers, password, isPublic, isDedicated, ownerSteamId, voip);
serverManager.Client_StartServer(port, isDedicated);

// 310 ✅
serverManager.StartHost(port, name, maxPlayers, password, isPublic, useVoip, useWhitelist);
serverManager.StartServer(port, isDedicated);
```

### Server Kick

```csharp
// 202 ❌
serverManager.Server_KickPlayer(player, DisconnectionCode.Kicked, sendToast);

// 310 ✅ — added message parameter
serverManager.Server_KickPlayer(player, DisconnectionCode.Kicked, "reason message", sendToast);
```

### Connection.cs Changes

```csharp
// 202 ❌
connection.IpAddress  // string
connection.Port       // ushort

// 310 ✅
connection.EndPoint.ipAddress  // string
connection.EndPoint.port       // ushort
```

### ConnectionData Expanded (4 → 27 properties)

All cosmetic data is now sent during connection:
```csharp
// 310 new fields on ConnectionData:
Key, Handedness, FlagID,
HeadgearIDBlueAttacker/Red/GoalieBlue/GoalieRed,
MustacheID, BeardID,
JerseyIDBlueAttacker/Red/GoalieBlue/GoalieRed,
StickSkinIDBlueAttacker/Red/GoalieBlue/GoalieRed,
StickShaftTapeIDBlueAttacker/Red/GoalieBlue/GoalieRed,
StickBladeTapeIDBlueAttacker/Red/GoalieBlue/GoalieRed
```

Removed: `SocketId`

### WebSocketManager: Instance → Static

```csharp
// 202 ❌
MonoBehaviourSingleton<WebSocketManager>.Instance.Emit("event", data, callback);

// 310 ✅
WebSocketManager.Emit("event", data, callback);
```

**WebSocket events renamed:**
- `"connect"` → `"connected"`
- `"disconnect"` → `"disconnected"`

**Message wrapping changed:**
- 202: `message["response"]` → `SocketIOResponse`
- 310: `message["inMessage"]` → `InMessage` (use `.GetData<T>()`)

**Server URL changed:** `wss://puck1.nasejevs.com` → `wss://puck2.nasejevs.com`

### Default Port Changed
- 202: `7777`
- 310: `30609`

### New NetworkingUtils Methods

```csharp
// 310 new utilities
NetworkingUtils.GetPlayerPositionFromNetworkObjectReference(ref)
NetworkingUtils.GetPuckFromNetworkObjectReference(ref)
NetworkingUtils.CompressFloatToShort(value, min, max)
NetworkingUtils.CompressFloatToByte(value, min, max)
NetworkingUtils.DecompressShortToFloat(compressed, min, max)
NetworkingUtils.DecompressByteToFloat(compressed, min, max)
```

---

## 10. Settings & Configuration Changes

### SettingsManager: Instance → Static

```csharp
// 202 ❌
MonoBehaviourSingleton<SettingsManager>.Instance.CameraAngle

// 310 ✅
SettingsManager.CameraAngle
```

### Storage Backend Changed

```csharp
// 202: PlayerPrefs directly
PlayerPrefs.GetInt("Debug", 0)

// 310: SaveManager wrapper
SaveManager.GetBool("Debug", false)
```

### Setting Type Changes

| Setting | 202 Type | 310 Type |
|---|---|---|
| `Debug` | `int` (0/1) | `bool` |
| `ShowPuckDisplay` | `int` (0/1) | `bool` |
| `VSync` | `int` (0/1) | `bool` |
| `MotionBlur` | `int` (0/1) | `bool` |
| `Handedness` | `string` ("RIGHT") | `PlayerHandedness` enum |
| `Units` | `string` ("METRIC") | `Units` enum |
| `Quality` | `string` ("HIGH") | `ApplicationQuality` enum |
| `WindowMode` | `string` ("BORDERLESS") | `FullScreenMode` enum |
| `NetworkSmoothingStrength` | `float` | `int` |

### Skin Settings: Strings → Int IDs

```csharp
// 202 ❌
SettingsManager.Instance.JerseyAttackerBlueSkin     // string
SettingsManager.Instance.GetJerseySkin(team, role)  // returns string
SettingsManager.Instance.UpdateJerseySkin(team, role, "skinName")

// 310 ✅
SettingsManager.JerseyIDBlueAttacker                // int
SettingsManager.GetJerseyID(team, role)             // returns int
SettingsManager.UpdateJerseyID(team, role, 42)
```

### New Settings (310)

- `MaxMatchmakingRtt` (int, default 50)
- `Team` (PlayerTeam)
- `Role` (PlayerRole)
- `ApplyForBothTeams` (bool)
- `FlagID`, `MustacheID`, `BeardID` (int)
- All headgear/jersey/stick IDs per team/role (int)

### Removed Settings

- All string-based skin settings (`VisorAttackerBlueSkin`, `Country`, etc.)
- `audioMixer` field

### Constants.cs Expanded Massively

`Constants` went from ~1 constant to 100+ constants including:
- `APP_ID` (was `AppId`)
- `DEFAULT_SERVER_PORT = 30609`
- `DEFAULT_SERVER_MAX_PLAYERS = 12`
- `DEFAULT_SERVER_TICK_RATE = 200`
- All default cosmetic IDs
- Team/badge colors
- All default settings values
- `KICK_TIMEOUT = 60f`
- `MATCH_ABANDONMENT_TIMEOUT = 120f`
- `INPUT_DEADZONE`, `SPRINT_STAMINA_THRESHOLD`
- `CHAT_BLACKLIST`, `CHAT_WHITELIST`

---

## 11. UI System Changes

### UIComponent → UIView

```csharp
// 202 ❌
public class MyUI : UIComponent { }
uiManager.components  // List<UIComponent>

// 310 ✅
public class MyUI : UIView { }
uiManager.views  // List<UIView>
```

### UI Callback Pattern Changed

```csharp
// 202 ❌
uiComponent.OnVisibilityChanged += handler;  // EventHandler pattern

// 310 ✅
uiView.OnVisibility = (Action<UIView>)Delegate.Combine(uiView.OnVisibility, handler);
```

### UI State Management

```csharp
// 202 ❌
uiManager.SetUiState(UIState.MainMenu);
uiManager.isMouseActive;
uiManager.ShowMainMenuComponents();
uiManager.HideMainMenuComponents();
uiManager.ShowGameComponents();

// 310 ✅
uiManager.ShowPhaseViews(UIPhase.MainMenu);
GlobalStateManager.UIState.IsMouseRequired;
GlobalStateManager.UIState.IsMouseOverUI;
GlobalStateManager.UIState.IsInteracting;
```

### UI Sound Methods Renamed

```csharp
// 202 ❌
uiManager.PlayerSelectSound();
uiManager.PlayerClickSound();
uiManager.PlayerNotificationSound();
uiManager.SetUiScale(1.0f);

// 310 ✅
uiManager.PlaySelectSound();
uiManager.PlayClickSound();
uiManager.PlayNotificationSound();
uiManager.SetUIScale(1.0f);
```

### UIManager Fields Changed

```csharp
// 202 ❌
uiManager.UiDocument
uiManager.Announcement        // UIAnnouncement
uiManager.ServerLauncher      // UIServerLauncher

// 310 ✅
uiManager.UIDocument
uiManager.Announcements       // UIAnnouncements
uiManager.NewServer           // UINewServer
uiManager.Footer              // UIFooter (new)
uiManager.Friends             // UIFriends (new)
uiManager.Play                // UIPlay (new)
uiManager.Matchmaking         // UIMatchmaking (new)
```

### Scoreboard

```csharp
// 202 ❌
scoreboard.SetTime(time);

// 310 ✅
scoreboard.SetTick(tick);
```

---

## 12. Enum Changes

### GamePhase

```
202: None, Warmup, FaceOff, Playing, BlueScore, RedScore, Replay, PeriodOver, GameOver
310: None, Warmup, PreGame, FaceOff, Play, BlueScore, RedScore, Replay, Intermission, GameOver, PostGame
```

| Change | Details |
|---|---|
| `Playing` → `Play` | Renamed |
| `PeriodOver` → `Intermission` | Renamed |
| `PreGame` | New (between Warmup and FaceOff) |
| `PostGame` | New (after GameOver) |

### PlayerTeam (Value Reorder)

```
202: None=0, Spectator=1, Blue=2, Red=3
310: None=0, Blue=1, Red=2, Spectator=3
```

### ConnectionRejectionCode

```
202: Unreachable, InvalidSocketId, InvalidSteamId, ServerFull, TimedOut, Banned, MissingPassword, InvalidPassword, MissingMods
310: Unreachable, ServerFull, TimedOut, Banned, NotWhitelisted, MissingPassword, InvalidPassword, MissingMods, Unknown
```

Removed: `InvalidSocketId`, `InvalidSteamId`
Added: `NotWhitelisted`, `Unknown`

### DisconnectionCode

```
202: Disconnected=0, Kicked=1, Banned=2
310: ConnectionLost=0, Disconnected=1, Kicked=2, Banned=3
```

Added: `ConnectionLost` (at position 0, shifting all others)

### New Enums in 310

- `PlayerPhase` — `None, TeamSelect, PositionSelect, Play, Replay, Spectate`
- `UIPhase` — (check file for values)
- `AuthenticationPhase`
- `TransactionPhase`
- `CameraType`
- `ApplicationQuality`
- `Units`
- `AvatarSize`
- `EdgegapDependency`
- `ConnectionState` (struct, not enum)
- `ServerSortType` / `ServerSortDirection`
- `HierarchyChangeType`
- `KeyBindInteractionType`
- `HeadgearRole`
- `JerseyTeam`
- `StickSkinTeam`
- `QuickChatCategory`
- `SpinnerDirection`
- `TCPServerMessageType`
- `PlayerPhase` (replaced `PlayerState` enum)

---

## 13. Cosmetics System Changes

The entire cosmetics system moved from **string-based skin names** to **integer IDs**, and from **individual NetworkVariables** to **compound structs**.

### Per-Player Cosmetics

```csharp
// 202 ❌ — 20+ individual NetworkVariable<FixedString32Bytes>
player.JerseyAttackerBlueSkin.Value     // FixedString32Bytes
player.VisorAttackerBlueSkin.Value      // FixedString32Bytes
player.StickAttackerBlueSkin.Value      // FixedString32Bytes
player.GetPlayerVisorSkin()             // FixedString32Bytes
player.GetPlayerJerseySkin()            // FixedString32Bytes

// 310 ✅ — Single NetworkVariable<PlayerCustomizationState>
player.CustomizationState.Value.JerseyIDBlueAttacker      // int
player.CustomizationState.Value.HeadgearIDBlueAttacker    // int (Visor → Headgear)
player.CustomizationState.Value.StickSkinIDBlueAttacker   // int
player.GetPlayerJerseyID()                                 // int
player.GetPlayerHeadgearID()                               // int
player.GetPlayerStickSkinID()                              // int
```

### "Visor" → "Headgear"

The visor system was renamed to headgear throughout. `VisorAttackerBlueSkin` → `HeadgearIDBlueAttacker`, etc.

### Stick Customization

```csharp
// 202 ❌
stick.UpdateStick();
stickMesh.SetSkin(team, "skinName");
stickMesh.SetShaftTape("tapeName");
stickMesh.SetBladeTape("tapeName");

// 310 ✅
stick.ApplyCustomizations();
stickMesh.SetSkinID(42, team);
stickMesh.SetShaftTapeID(7);
stickMesh.SetBladeTapeID(3);
```

### Items System

```csharp
// 202 ❌
MonoBehaviourSingleton<ItemManager>.Instance.OwnedItemIds  // int[]
MonoBehaviourSingleton<ItemManager>.Instance.OwnedItems    // List<string>

// 310 ✅
ItemManager.Items                          // List<Item>
ItemManager.GetItemById(42)                // Item
ItemManager.GetItemsByCategories(cats)     // List<Item>
```

---

## 14. Mod System Changes

### ModManager Renamed

```csharp
// 202 ❌
MonoBehaviourSingleton<ModManagerV2>.Instance

// 310 ✅
MonoBehaviourSingleton<ModManager>.Instance
```

### ModConfiguration → ModConfig

```csharp
// 202 ❌
ModConfiguration config;

// 310 ✅
ModConfig config;
// Properties: ulong id, bool enabled, bool clientRequired
```

### Mod Event Names Changed

```csharp
// 202 → 310
"Event_Client_OnModEnableSucceeded"  → "Event_OnModEnableSucceeded"
"Event_Client_OnModEnableFailed"     → "Event_OnModEnableFailed"
"Event_Client_OnModDisableSucceeded" → "Event_OnModDisableSucceeded"
"Event_Client_OnModDisableFailed"    → "Event_OnModDisableFailed"
"Event_Client_OnModChanged"          → "Event_OnModChanged"
```

### IPuckMod Interface — UNCHANGED

```csharp
// Both 202 and 310:
public interface IPuckMod {
    bool OnEnable();
    bool OnDisable();
}
```

### New: PatchManager

```csharp
// 310 — Harmony patches for UIElements
PatchManager.Initialize();   // Applies VisualElementHarmonyPatch
PatchManager.Dispose();
```

---

## 15. New Game Mode System

This is the biggest new feature for modders. You can now create custom game modes.

### Architecture

```
IGameMode (internal interface)
  └── BaseGameMode<TConfig> (abstract, 30+ virtual hooks)
        └── StandardGameMode<TConfig> (full game flow)
              ├── PublicGameMode<TConfig> (default lobby)
              └── MatchableGameMode<TConfig>
                    └── CompetitiveGameMode<TConfig> (ranked)
```

### Creating a Custom Game Mode

Subclass `StandardGameMode` or `BaseGameMode` and override hooks:

```csharp
public class MyGameMode : StandardGameMode<MyGameModeConfig>
{
    public MyGameMode() : base("./my_config.json") { }

    protected override void OnPlayStarted() { /* custom logic */ }
    protected override void OnGoalScored(PlayerTeam byTeam, Player goal, Player assist, Player secondAssist, Puck puck) { }
    protected override void OnPlayerJoined(Player player) { }
    protected override void OnPlayerRequestTeam(Player player, PlayerTeam team) { }
    // ... 30+ hooks available
}
```

### Available Hooks in BaseGameMode

**Game State:**
- `OnGameStateChanged`, `OnGamePhaseStarted`, `OnGamePhaseEnded`

**Per-Phase (started + ended for each):**
- `OnWarmupStarted/Ended`, `OnPreGameStarted/Ended`, `OnFaceOffStarted/Ended`
- `OnPlayStarted/Ended`, `OnBlueScoreStarted/Ended`, `OnRedScoreStarted/Ended`
- `OnReplayStarted/Ended`, `OnIntermissionStarted/Ended`
- `OnGameOverStarted/Ended`, `OnPostGameStarted/Ended`

**Player Events:**
- `OnPlayerJoined(Player)`, `OnPlayerLeft(Player)`
- `OnPlayerGameStateChanged(Player, old, new)`
- `OnPlayerPhaseChanged(Player, old, new)`
- `OnPlayerTeamChanged(Player, old, new)`
- `OnPlayerRoleChanged(Player, old, new)`
- `OnPlayerPositionChanged(Player, old, new)`

**Requests:**
- `OnPlayerRequestTeamSelect(Player)`
- `OnPlayerRequestTeam(Player, PlayerTeam)`
- `OnPlayerRequestPositionSelect(Player)`
- `OnPlayerRequestPosition(Player, PlayerPosition)`
- `OnPlayerRequestHandedness(Player, PlayerHandedness)`

**Other:**
- `OnGoalScored(PlayerTeam, Player, Player, Player, Puck)`
- `OnPuckEnterGoal(PlayerTeam, Puck)`
- `OnVoteSuccess(Vote)`
- `ScoreGoal(...)`, `SendServerState()`

### StandardGameModeConfig

```csharp
public class StandardGameModeConfig : BaseGameModeConfig {
    public Dictionary<GamePhase, int> phaseDurationMap { get; set; }
    // Defaults: Warmup=60, PreGame=10, FaceOff=5, Play=300, etc.
    public float spawnDelay { get; set; }   // default 5
    public int maxPeriods { get; set; }     // default 3
}
```

### Registering Custom Game Modes

Game modes are registered in `GameModeManager`:
```csharp
// Built-in modes:
// "public" → PublicGameMode<PublicGameModeConfig>
// "competitive" → CompetitiveGameMode<CompetitiveGameModeConfig>
GameModeManager.Instance.SelectGameMode("public");
GameModeManager.Instance.EnableSelectedGameMode();
```

---

## 16. Complete Event Name Migration Table

| 202 Event Name | 310 Event Name |
|---|---|
| `Event_OnPlayerSpawned` | `Event_Everyone_OnPlayerSpawned` |
| `Event_OnPlayerDespawned` | `Event_Everyone_OnPlayerDespawned` |
| `Event_OnPlayerStateChanged` | `Event_Everyone_OnPlayerGameStateChanged` |
| `Event_OnPlayerTeamChanged` | (covered by `Event_Everyone_OnPlayerGameStateChanged`) |
| `Event_OnPlayerRoleChanged` | (covered by `Event_Everyone_OnPlayerGameStateChanged`) |
| `Event_OnPlayerUsernameChanged` | `Event_Everyone_OnPlayerUsernameChanged` |
| `Event_OnPlayerAdded` | `Event_Everyone_OnPlayerAdded` |
| `Event_OnPlayerRemoved` | `Event_Everyone_OnPlayerRemoved` |
| `Event_OnPlayerBodySpawned` | `Event_Everyone_OnPlayerBodySpawned` |
| `Event_OnPlayerHandednessChanged` | `Event_Everyone_OnPlayerHandednessChanged` |
| `Event_OnPlayerPositionClaimedByChanged` | `Event_Everyone_OnPlayerPositionClaimedByPlayerChanged` |
| `Event_OnGameStateChanged` | `Event_Everyone_OnGameStateChanged` |
| `Event_OnGamePhaseChanged` | (removed — use `Event_Everyone_OnGameStateChanged`) |
| `Event_OnGameOver` | (removed — use game mode hooks) |
| `Event_OnGoalScored` | `Event_Everyone_OnGoalScored` |
| `Event_OnPuckSpawned` | `Event_Everyone_OnPuckSpawned` |
| `Event_OnPuckDespawned` | `Event_Everyone_OnPuckDespawned` |
| `Event_OnStickSpawned` | `Event_Everyone_OnStickSpawned` |
| `Event_OnStickDespawned` | `Event_Everyone_OnStickDespawned` |
| `Event_OnClientConnected` | `Event_Everyone_OnClientConnected` |
| `Event_OnClientDisconnected` | `Event_Everyone_OnClientDisconnected` |
| `Event_Client_OnModEnableSucceeded` | `Event_OnModEnableSucceeded` |
| `Event_Client_OnModEnableFailed` | `Event_OnModEnableFailed` |
| `Event_Client_OnModDisableSucceeded` | `Event_OnModDisableSucceeded` |
| `Event_Client_OnModDisableFailed` | `Event_OnModDisableFailed` |
| `Event_Client_OnModChanged` | `Event_OnModChanged` |
| `Event_Client_OnTransportFailure` | `Event_OnTransportFailure` |
| `Event_Client_OnMainMenuClickHostServer` | `Event_OnMainMenuClickHostServer` |
| `Event_Client_OnMainMenuClickPractice` | `Event_OnPlayClickPractice` |
| `Event_Client_OnServerLauncherClickStartSelfHostedServer` | `Event_OnNewServerClickStart` |
| `Event_Client_OnMainMenuClickJoinServer` | `Event_OnMainMenuClickJoinServer` |
| `Event_Client_OnPauseMenuClickDisconnect` | `Event_OnPauseMenuClickDisconnect` |
| `Event_Client_OnPauseMenuClickSwitchTeam` | `Event_OnPauseMenuClickSelectTeam` |
| `Event_Client_OnDebugChanged` | `Event_OnDebugChanged` |
| `Event_Client_OnGotLaunchCommandLine` | `Event_OnGotLaunchCommandLine` |
| `Event_Client_OnGameRichPresenceJoinRequested` | `Event_OnGameRichPresenceJoinRequested` |
| `Event_Client_OnServerBrowserClickServer` | `Event_OnServerBrowserClickEndPoint` |
| `Event_Client_OnPendingModsCleared` | `Event_OnPendingModsCleared` |
| `Event_Client_OnPopupClickOk` | `Event_OnPopupClickOk` |
| `Event_Client_OnPlayerSelectTeam` | `Event_OnTeamSelectClickTeam` |
| `Event_Client_OnPlayerRequestPositionSelect` | `Event_OnPauseMenuClickSelectPosition` |
| `Event_Client_OnHandednessChanged` | `Event_OnHandednessChanged` |
| `Event_Client_OnCameraAngleChanged` | `Event_OnCameraAngleChanged` |
| `Event_Client_OnServerConfiguration` | `Event_Everyone_OnServerChanged` |
| `Event_Client_OnUserInterfaceScaleChanged` | `Event_OnUserInterfaceScaleChanged` |
| `Event_Client_OnIsClientChanged` | (removed) |
| `Event_Client_OnLevelReady` | `Event_OnServerStateChanged` |
| `Event_Server_OnSynchronizeComplete` | `Event_Server_OnClientSceneSynchronizeComplete` |
| `Event_Server_OnServerReady` | `Event_Server_OnServerStarted` |
| `Event_Server_ConnectionApproval` | (moved to `ConnectionApprovalManager`) |
| `Event_Server_OnPlayerSubscription` | (removed) |
| `Event_Server_OnPlayerSleepInput` | (removed) |
| `Event_Server_OnPuckEnterTeamGoal` | `Event_Server_OnPuckEnterGoal` |

---

## Quick Migration Checklist

1. **Find/replace** `MonoBehaviourSingleton<EventManager>.Instance.` → `EventManager.`
2. **Find/replace** `MonoBehaviourSingleton<InputManager>.Instance.` → `InputManager.`
3. **Find/replace** `MonoBehaviourSingleton<SettingsManager>.Instance.` → `SettingsManager.`
4. **Find/replace** `MonoBehaviourSingleton<ItemManager>.Instance.` → `ItemManager.`
5. **Find/replace** `PlayerBodyV2` → `PlayerBody`
6. **Find/replace** `ModManagerV2` → `ModManager`
7. **Find/replace** `ModManagerControllerV2` → `ModManagerController`
8. **Find/replace** `ModConfiguration` → `ModConfig`
9. **Find/replace** `NetworkObjectCollisionBuffer` → `NetworkObjectCollisionRecorder`
10. **Replace** `player.State.Value` → `player.Phase` (type: `PlayerPhase`)
11. **Replace** `player.Team.Value` → `player.Team`
12. **Replace** `player.Role.Value` → `player.Role`
13. **Replace** `DestroyOnLoad()` → `AllowSceneDestruction()`
14. **Replace** `GamePhase.Playing` → `GamePhase.Play`
15. **Replace** `GamePhase.PeriodOver` → `GamePhase.Intermission`
16. **Replace** `gameState.Time` → `gameState.Tick`
17. **Update all event names** per the migration table above
18. **Replace** string-based skin accessors with int-based ID accessors
19. **Replace** `ChangingRoom*` references with `LockerRoom*`
20. **Update** any `PlayerTeam` integer casts (enum values reordered)
