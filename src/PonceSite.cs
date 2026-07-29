// PonceSite.cs - read-only client for poncepuck.net.
//
// Everything here is fetched from world-readable static JSON that the site already publishes for
// its own pages, so there is no token, no login, and nothing user-specific in flight. Requests are
// pinned to the poncepuck.net host, fetched at most once per TTL, and mirrored to disk so a session
// costs one request per feed at worst. The site has NO rate limiting of its own, so the politeness
// budget is enforced entirely on this side - do not add polling without a visible-only gate.
//
// Deliberately NOT consumed: /data/banned_steam_ids.json and /data/ban_meta.json. They are
// world-readable but they are moderation records with free-text reasons; republishing them into a
// client mod would broadcast individual disciplinary history to anyone who installs it.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace PoncePuck.LocalMute
{
    internal static class PonceSite
    {
        private const string Host = "https://poncepuck.net";

        // Pre-baked leaderboards - byte-identical to /stats/api/leaderboard.php but static, so they
        // cost the site a file read instead of a full aggregate query.
        private const string PathSkaters = "/stats/cache/pts.json";
        private const string PathGoalies = "/stats/cache/sv.json";
        private const string PathServers = "/data/player_counts_cache.json";

        private static readonly string[] BadgeFeeds =
        {
            "/data/website_admins.json",
            "/data/mod_steam_ids.json",
            "/data/league_staff_steam_ids.json",
            "/data/donor_list_cache.json",
        };

        private const int LeaderboardTtl = 6 * 60 * 60;   // stats move slowly; 6h is plenty
        private const int BadgeTtl       = 6 * 60 * 60;
        private const int ServerTtl      = 25;            // live data, but never faster than this

        public sealed class PlayerStats
        {
            public bool HasSkater, HasGoalie;
            public int Gp, Goals, Assists, Points, Rank;
            public int GoalieGp, Saves, GoalsAgainst, Shutouts;
            public double SavePct;
        }

        public sealed class ServerRow
        {
            public string Name = "", DisplayName = "", Phase = "";
            public int Players, MaxPlayers, Period, ScoreBlue, ScoreRed, TimeRemaining;
            public bool ClockRunning;
            public long LastSeenEpoch;
            public bool RawIsLive;
            public string Ip = "";
            public int Port;

            /// <summary>Steam's launch-args deep link. Puck parses "+ipAddress"/"+port" out of
            /// Event_OnGotLaunchCommandLine and auto-connects, which is exactly what the website's
            /// own join buttons do (2994020 is Puck's app id).
            /// Empty when the feed carries no IP - see CanJoin.</summary>
            public string JoinUrl
            {
                get
                {
                    if (!CanJoin) return "";
                    return "steam://run/2994020//" + UnityWebRequest.EscapeURL("+ipAddress " + Ip + " +port " + Port);
                }
            }

            public bool CanJoin => IsLive && Port > 0 && !string.IsNullOrEmpty(Ip);

            /// <summary>Friendly name where the heartbeat supplied one, else the raw server slug.</summary>
            public string Label => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : Name;

            /// <summary>The site recomputes this from last_seen_epoch rather than trusting the cached
            /// boolean, which goes stale whenever the file outlives the heartbeat. 360s matches the
            /// server-side threshold in heartbeat.php.</summary>
            public bool IsLive
            {
                get
                {
                    if (LastSeenEpoch > 0)
                        return (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - LastSeenEpoch) <= 360;
                    return RawIsLive;
                }
            }

            public string StatusLabel
            {
                get
                {
                    if (!IsLive) return "";
                    if (Period > 0)
                        return Period <= 3 ? "P" + Period : (Period == 4 ? "OT" : "SO");
                    switch (Phase)
                    {
                        case "Warmup":   return "Warmup";
                        case "FaceOff":  return "Face-off";
                        case "Playing":  return "Playing";
                        case "GameOver": return "Final";
                        default:         return "";
                    }
                }
            }

            public string ScoreLabel
            {
                get
                {
                    if (!IsLive) return "";
                    if (ScoreBlue == 0 && ScoreRed == 0 && Period == 0 && Phase != "GameOver") return "";
                    return ScoreBlue + " – " + ScoreRed;
                }
            }
        }

        private static readonly Dictionary<ulong, PlayerStats> _stats = new Dictionary<ulong, PlayerStats>();
        private static readonly HashSet<ulong> _admins = new HashSet<ulong>();
        private static readonly HashSet<ulong> _mods = new HashSet<ulong>();
        private static readonly HashSet<ulong> _leagueStaff = new HashSet<ulong>();
        private static readonly HashSet<ulong> _donors = new HashSet<ulong>();
        private static readonly List<ServerRow> _servers = new List<ServerRow>();

        private static bool _statsRequested, _badgesRequested;
        private static float _serversNextAllowed;

        public static bool StatsReady { get; private set; }
        public static bool BadgesReady { get; private set; }
        public static bool ServersReady { get; private set; }

        /// <summary>Raised on the main thread once a feed lands, so open UI can refill in place.</summary>
        public static event Action StatsChanged;
        public static event Action BadgesChanged;
        public static event Action ServersChanged;

        // ---------------------------------------------------------------- public reads

        public static PlayerStats GetStats(string steamId)
        {
            if (!ulong.TryParse((steamId ?? "").Trim(), out ulong sid) || sid == 0UL) return null;
            return _stats.TryGetValue(sid, out var row) ? row : null;
        }

        /// <summary>Highest-precedence badge for a player, or null. Ordered most specific first so a
        /// staff member who also donates reads as staff.</summary>
        public static string GetBadge(string steamId)
        {
            if (!ulong.TryParse((steamId ?? "").Trim(), out ulong sid) || sid == 0UL) return null;
            if (_admins.Contains(sid))      return "ADMIN";
            if (_mods.Contains(sid))        return "MOD";
            if (_leagueStaff.Contains(sid)) return "PPHL";
            if (_donors.Contains(sid))      return "DONOR";
            return null;
        }

        public static Color BadgeColor(string badge)
        {
            switch (badge)
            {
                case "ADMIN": return new Color(1.00f, 0.45f, 0.45f);
                case "MOD":   return new Color(1.00f, 0.78f, 0.25f);
                case "PPHL":  return new Color(0.55f, 0.80f, 1.00f);
                case "DONOR": return new Color(0.65f, 0.90f, 0.60f);
                default:      return Color.white;
            }
        }

        public static List<ServerRow> Servers => _servers;

        /// <summary>Open a poncepuck.net page. Prefers the in-game Steam overlay browser so the
        /// player isn't alt-tabbed out mid-game, falling back to the system browser. The
        /// IsOverlayEnabled() probe is what makes that fallback reachable: with the overlay off,
        /// ActivateGameOverlayToWebPage returns normally and simply does nothing.</summary>
        public static void OpenExternal(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                if (Steamworks.SteamUtils.IsOverlayEnabled())
                {
                    Steamworks.SteamFriends.ActivateGameOverlayToWebPage(url);
                    return;
                }
            }
            catch { /* Steam API not initialised for this process - fall through to the browser */ }

            try { Application.OpenURL(url); }
            catch (Exception e) { Debug.LogError($"[Ponce] Failed to open '{url}': {e.Message}"); }
        }

        /// <summary>Hand a server's join deep link to Steam. Returns false when the feed didn't give
        /// us an address, so callers can fall back rather than silently doing nothing.</summary>
        public static bool TryJoin(ServerRow sv)
        {
            if (sv == null || !sv.CanJoin) return false;
            try
            {
                Application.OpenURL(sv.JoinUrl);
                Debug.Log($"[Ponce] Join requested: {sv.Label} ({sv.Ip}:{sv.Port})");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Ponce] Join failed for '{sv.Label}': {e.Message}");
                return false;
            }
        }

        // ---------------------------------------------------------------- fetch entry points

        /// <summary>Load the leaderboards. Lazy on purpose - these are the biggest payload the mod
        /// touches, so players who never open an info dialog never pay for them.</summary>
        public static void EnsureStats()
        {
            if (_statsRequested || LocalMuteRunner.Instance == null) return;
            _statsRequested = true;
            LocalMuteRunner.Run(LoadStats());
        }

        public static void EnsureBadges()
        {
            if (_badgesRequested || LocalMuteRunner.Instance == null) return;
            _badgesRequested = true;
            LocalMuteRunner.Run(LoadBadges());
        }

        /// <summary>Refresh the live server list. Throttled hard: only call this while a view that
        /// actually shows it is on screen.</summary>
        public static void RefreshServers(bool force = false)
        {
            if (LocalMuteRunner.Instance == null) return;
            if (!force && Time.unscaledTime < _serversNextAllowed) return;
            _serversNextAllowed = Time.unscaledTime + ServerTtl;
            LocalMuteRunner.Run(LoadServers());
        }

        // ---------------------------------------------------------------- loaders

        private static IEnumerator LoadStats()
        {
            string skaters = null, goalies = null;
            yield return Fetch(PathSkaters, "pts.json", LeaderboardTtl, t => skaters = t);
            yield return Fetch(PathGoalies, "sv.json", LeaderboardTtl, t => goalies = t);

            int parsed = 0;
            try
            {
                parsed += ParseSkaters(skaters);
                parsed += ParseGoalies(goalies);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ponce] Leaderboard parse failed: {e.Message}");
            }

            if (parsed > 0)
            {
                StatsReady = true;
                Debug.Log($"[Ponce] Loaded stats for {_stats.Count} players");
                Raise(StatsChanged);
            }
        }

        private static int ParseSkaters(string json)
        {
            var rows = DataArray(json);
            if (rows == null) return 0;
            int n = 0;
            foreach (var r in rows)
            {
                if (!ulong.TryParse((string)r["steam_id"], out ulong sid) || sid == 0UL) continue;
                var s = Row(sid);
                s.HasSkater = true;
                s.Gp      = (int?)r["gp"] ?? 0;
                s.Goals   = (int?)r["goals"] ?? 0;
                s.Assists = (int?)r["assists"] ?? 0;
                s.Points  = (int?)r["points"] ?? 0;
                s.Rank    = (int?)r["rank"] ?? 0;
                n++;
            }
            return n;
        }

        private static int ParseGoalies(string json)
        {
            var rows = DataArray(json);
            if (rows == null) return 0;
            int n = 0;
            foreach (var r in rows)
            {
                if (!ulong.TryParse((string)r["steam_id"], out ulong sid) || sid == 0UL) continue;
                var s = Row(sid);
                s.HasGoalie    = true;
                s.GoalieGp     = (int?)r["gp"] ?? 0;
                s.Saves        = (int?)r["saves"] ?? 0;
                s.GoalsAgainst = (int?)r["ga"] ?? 0;
                s.Shutouts     = (int?)r["shutouts"] ?? 0;
                s.SavePct      = (double?)r["save_pct"] ?? 0d;
                n++;
            }
            return n;
        }

        private static PlayerStats Row(ulong sid)
        {
            if (!_stats.TryGetValue(sid, out var s)) { s = new PlayerStats(); _stats[sid] = s; }
            return s;
        }

        private static JArray DataArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var root = JToken.Parse(json);
            if (root is JArray arr) return arr;
            return root["data"] as JArray;
        }

        private static IEnumerator LoadBadges()
        {
            var targets = new[] { _admins, _mods, _leagueStaff, _donors };
            int total = 0;

            for (int i = 0; i < BadgeFeeds.Length; i++)
            {
                string body = null;
                string file = Path.GetFileName(BadgeFeeds[i]);
                yield return Fetch(BadgeFeeds[i], file, BadgeTtl, t => body = t);

                try { total += ParseIdList(body, targets[i]); }
                catch (Exception e) { Debug.LogWarning($"[Ponce] Badge feed '{file}' parse failed: {e.Message}"); }
            }

            if (total > 0)
            {
                BadgesReady = true;
                Debug.Log($"[Ponce] Loaded {total} badge entries " +
                          $"(admin {_admins.Count} / mod {_mods.Count} / pphl {_leagueStaff.Count} / donor {_donors.Count})");
                Raise(BadgesChanged);
            }
        }

        // The four badge feeds each use a different envelope: a bare array, {adminSteamIds:[...]},
        // and {ok,data:[...]} - and the admin one stores ids as JSON numbers rather than strings.
        private static int ParseIdList(string json, HashSet<ulong> into)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            var root = JToken.Parse(json);
            JArray arr = root as JArray
                      ?? root["adminSteamIds"] as JArray
                      ?? root["data"] as JArray
                      ?? root["ids"] as JArray;
            if (arr == null) return 0;

            int n = 0;
            foreach (var t in arr)
            {
                string raw = t.Type == JTokenType.Integer ? ((ulong)t).ToString() : (string)t;
                if (ulong.TryParse((raw ?? "").Trim(), out ulong sid) && sid != 0UL && into.Add(sid)) n++;
            }
            return n;
        }

        private static IEnumerator LoadServers()
        {
            string body = null;
            yield return Fetch(PathServers, "servers.json", ServerTtl, t => body = t);
            if (string.IsNullOrEmpty(body)) yield break;

            try
            {
                var arr = JToken.Parse(body)["servers"] as JArray;
                if (arr == null) yield break;

                _servers.Clear();
                foreach (var r in arr)
                {
                    _servers.Add(new ServerRow
                    {
                        Name          = (string)r["server_name"] ?? "",
                        DisplayName   = (string)r["display_name"] ?? "",
                        Phase         = (string)r["current_phase"] ?? "",
                        Players       = (int?)r["players_online"] ?? 0,
                        MaxPlayers    = (int?)r["players_max"] ?? 0,
                        Period        = (int?)r["current_period"] ?? 0,
                        ScoreBlue     = (int?)r["score_blue"] ?? 0,
                        ScoreRed      = (int?)r["score_red"] ?? 0,
                        TimeRemaining = (int?)r["time_remaining_seconds"] ?? 0,
                        ClockRunning  = (bool?)r["clock_running"] ?? false,
                        LastSeenEpoch = (long?)r["last_seen_epoch"] ?? 0L,
                        RawIsLive     = (bool?)r["is_live"] ?? false,
                        // server_port is published by the heartbeat; ip is NOT in this feed today
                        // (the website renders it from a server-side config), so JoinUrl stays empty
                        // until the feed carries one. Read both defensively either way.
                        Port          = (int?)r["server_port"] ?? 0,
                        Ip            = (string)r["ip"] ?? (string)r["server_ip"] ?? "",
                    });
                }

                // Busiest live servers first - that is what someone picking a server is looking for.
                _servers.Sort((a, b) =>
                {
                    if (a.IsLive != b.IsLive) return a.IsLive ? -1 : 1;
                    if (a.Players != b.Players) return b.Players.CompareTo(a.Players);
                    return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
                });

                ServersReady = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ponce] Server list parse failed: {e.Message}");
                yield break;
            }

            Raise(ServersChanged);
        }

        // ---------------------------------------------------------------- transport

        private static string CacheDir()
        {
            string gameDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(gameDir, "config", "ModHub", "PlayerQoL", "PonceCache");
        }

        /// <summary>Disk copy if it is younger than the TTL, else null. Kept out of the coroutine so
        /// the file IO can sit in a try/catch (C# forbids yielding inside one).</summary>
        private static string ReadFresh(string path, int ttlSeconds)
        {
            try
            {
                if (!File.Exists(path)) return null;
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > ttlSeconds) return null;
                return File.ReadAllText(path);
            }
            catch { return null; }
        }

        private static void WriteCache(string path, string body)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, body);
            }
            catch (Exception e) { Debug.LogWarning($"[Ponce] Cache write failed: {e.Message}"); }
        }

        private static IEnumerator Fetch(string path, string cacheFile, int ttlSeconds, Action<string> onBody)
        {
            string cachePath = Path.Combine(CacheDir(), cacheFile);

            string cached = ReadFresh(cachePath, ttlSeconds);
            if (cached != null) { onBody(cached); yield break; }

            string url = Host + path;   // host is a constant: nothing user-supplied builds this URL
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 20;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[Ponce] {path} failed: {req.error}");
                    // Fall back to a stale copy rather than showing nothing.
                    onBody(ReadStale(cachePath));
                    yield break;
                }

                string body = req.downloadHandler.text;
                if (string.IsNullOrEmpty(body) || (body[0] != '{' && body[0] != '['))
                {
                    // Hosts and captive portals answer with HTML at HTTP 200; caching that would
                    // poison the feed until the TTL expired.
                    Debug.LogWarning($"[Ponce] {path} did not return JSON, ignoring");
                    onBody(ReadStale(cachePath));
                    yield break;
                }

                WriteCache(cachePath, body);
                onBody(body);
            }
        }

        private static string ReadStale(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static void Raise(Action evt)
        {
            try { evt?.Invoke(); }
            catch (Exception e) { Debug.LogWarning($"[Ponce] listener threw: {e.Message}"); }
        }
    }
}
