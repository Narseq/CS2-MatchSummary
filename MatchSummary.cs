using System.Globalization;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace MatchSummary;

public class MatchSummaryConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;

    // bots are counted in the rankings and awards
    [JsonPropertyName("IncludeBots")] public bool IncludeBots { get; set; } = false;

    // a player needs at least this many played rounds to appear in the TOP and the awards
    [JsonPropertyName("MinRounds")] public int MinRounds { get; set; } = 3;

    // how many players the end-of-map TOP lists
    [JsonPropertyName("TopCount")] public int TopCount { get; set; } = 3;

    [JsonPropertyName("ShowTop")] public bool ShowTop { get; set; } = true;
    [JsonPropertyName("ShowAwards")] public bool ShowAwards { get; set; } = true;
    [JsonPropertyName("ShowPersonal")] public bool ShowPersonal { get; set; } = true;

    // seconds after the match ends before the summary is printed
    [JsonPropertyName("SummaryDelay")] public float SummaryDelay { get; set; } = 1.5f;

    // chat commands (each one also works as !name and /name); empty list disables !stats
    [JsonPropertyName("StatsCommands")] public List<string> StatsCommands { get; set; } = ["stats", "mystats"];
}

internal sealed class PlayerStats
{
    public string Name = "";
    public bool IsBot;
    public int Kills, Deaths, Assists, FlashAssists, Headshots, Damage, UtilityDamage;
    public int Rounds, Entries, Clutches, BestClutch, Mvps, K3, K4, K5;
    public int RoundKills;

    public double Adr => Rounds > 0 ? (double)Damage / Rounds : 0;
    public double HsPercent => Kills > 0 ? 100.0 * Headshots / Kills : 0;
}

public class MatchSummaryPlugin : BasePlugin, IPluginConfig<MatchSummaryConfig>
{
    public override string ModuleName => "MatchSummary";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Ziuber (Fragujemy.com)";
    public override string ModuleDescription => "End-of-map summary: TOP by ADR, awards and a personal line for every player; !stats during the map.";

    public MatchSummaryConfig Config { get; set; } = new();
    public void OnConfigParsed(MatchSummaryConfig config)
    {
        config.MinRounds = Math.Max(0, config.MinRounds);
        config.TopCount = Math.Clamp(config.TopCount, 1, 10);
        config.SummaryDelay = Math.Clamp(config.SummaryDelay, 0f, 10f);
        Config = config;
    }

    private static readonly string[] UtilityWeapons = ["hegrenade", "inferno", "molotov", "incgrenade"];

    private readonly Dictionary<string, PlayerStats> _stats = new();
    private readonly Dictionary<int, int> _health = new();          // slot -> health before the next hit
    private readonly Dictionary<int, (string Key, int Vs)> _clutch = new(); // team -> lone survivor this round
    private bool _roundLive;
    private bool _firstKill;
    private bool _summaryShown;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ => ResetMap());

        RegisterEventHandler<EventRoundFreezeEnd>(OnFreezeEnd);
        RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        RegisterEventHandler<EventPlayerHurt>(OnHurt);
        RegisterEventHandler<EventPlayerDeath>(OnDeath);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventRoundMvp>(OnMvp);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);

        foreach (var name in Config.StatsCommands.Select(c => c.Trim().TrimStart('!', '/')).Where(c => c.Length > 0).Distinct())
        {
            AddCommand($"css_{name}", "Shows your statistics for this map", OnStatsCommand);
        }
    }

    // ---------- state ----------

    private void ResetMap()
    {
        _stats.Clear();
        _health.Clear();
        _clutch.Clear();
        _roundLive = false;
        _firstKill = false;
        _summaryShown = false;
    }

    private static bool InWarmup() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.WarmupPeriod ?? false;

    private static bool Valid(CCSPlayerController? p) => p is { IsValid: true } && !p.IsHLTV;

    private bool Counted(CCSPlayerController? p) => Valid(p) && (Config.IncludeBots || !p!.IsBot);

    private static string KeyOf(CCSPlayerController p) => p.IsBot ? $"bot:{p.PlayerName}" : p.SteamID.ToString(CultureInfo.InvariantCulture);

    private PlayerStats StatsOf(CCSPlayerController p)
    {
        string key = KeyOf(p);
        if (!_stats.TryGetValue(key, out var s))
        {
            s = new PlayerStats { IsBot = p.IsBot };
            _stats[key] = s;
        }

        s.Name = p.PlayerName;
        return s;
    }

    private static bool Playing(CCSPlayerController p) => p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    // ---------- events ----------

    private HookResult OnFreezeEnd(EventRoundFreezeEnd ev, GameEventInfo info)
    {
        if (!Config.Enabled || InWarmup())
        {
            _roundLive = false;
            return HookResult.Continue;
        }

        _roundLive = true;
        _firstKill = false;
        _clutch.Clear();

        foreach (var p in Utilities.GetPlayers().Where(p => Counted(p) && Playing(p) && p.PawnIsAlive))
        {
            var s = StatsOf(p);
            s.Rounds++;
            s.RoundKills = 0;
        }

        return HookResult.Continue;
    }

    private HookResult OnSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (Valid(ev.Userid)) _health[ev.Userid!.Slot] = 100;
        return HookResult.Continue;
    }

    private HookResult OnHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        var victim = ev.Userid;
        var attacker = ev.Attacker;
        if (!Valid(victim)) return HookResult.Continue;

        // damage actually taken: the last hit cannot take more than the health that was left
        int before = _health.TryGetValue(victim!.Slot, out var h) ? h : 100;
        int dealt = Math.Clamp(Math.Min(ev.DmgHealth, before), 0, 100);
        _health[victim.Slot] = Math.Max(0, ev.Health);

        if (!_roundLive || !Counted(attacker) || attacker == victim || attacker!.Team == victim.Team) return HookResult.Continue;

        var s = StatsOf(attacker);
        s.Damage += dealt;
        if (UtilityWeapons.Contains(ev.Weapon)) s.UtilityDamage += dealt;
        return HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!_roundLive) return HookResult.Continue;

        var victim = ev.Userid;
        var attacker = ev.Attacker;
        var assister = ev.Assister;
        if (!Valid(victim)) return HookResult.Continue;

        if (Counted(victim)) StatsOf(victim!).Deaths++;

        bool enemyKill = Valid(attacker) && attacker != victim && attacker!.Team != victim!.Team;
        if (enemyKill)
        {
            if (!_firstKill && Counted(attacker)) StatsOf(attacker!).Entries++;
            _firstKill = true;

            if (Counted(attacker))
            {
                var s = StatsOf(attacker!);
                s.Kills++;
                s.RoundKills++;
                if (ev.Headshot) s.Headshots++;
            }
        }

        if (Counted(assister) && assister != victim && assister!.Team != victim!.Team)
        {
            var a = StatsOf(assister);
            a.Assists++;
            if (ev.Assistedflash) a.FlashAssists++;
        }

        TrackClutch(victim!);
        return HookResult.Continue;
    }

    // after every death: a team with exactly one player left against at least one enemy is in a clutch
    private void TrackClutch(CCSPlayerController justDied)
    {
        var alive = Utilities.GetPlayers()
            .Where(p => Valid(p) && Playing(p) && p.PawnIsAlive && p != justDied)
            .GroupBy(p => (int)p.Team)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (int team in new[] { (int)CsTeam.Terrorist, (int)CsTeam.CounterTerrorist })
        {
            int enemy = team == (int)CsTeam.Terrorist ? (int)CsTeam.CounterTerrorist : (int)CsTeam.Terrorist;
            int mine = alive.TryGetValue(team, out var m) ? m.Count : 0;
            int theirs = alive.TryGetValue(enemy, out var t) ? t.Count : 0;

            if (mine == 1 && theirs >= 1 && !_clutch.ContainsKey(team) && Counted(m![0]))
            {
                _clutch[team] = (KeyOf(m[0]), theirs);
            }
        }
    }

    private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info)
    {
        if (!_roundLive) return HookResult.Continue;
        _roundLive = false;

        if (_clutch.TryGetValue(ev.Winner, out var c) && _stats.TryGetValue(c.Key, out var clutcher))
        {
            clutcher.Clutches++;
            clutcher.BestClutch = Math.Max(clutcher.BestClutch, c.Vs);
        }

        foreach (var s in _stats.Values)
        {
            if (s.RoundKills == 3) s.K3++;
            else if (s.RoundKills == 4) s.K4++;
            else if (s.RoundKills >= 5) s.K5++;
            s.RoundKills = 0;
        }

        return HookResult.Continue;
    }

    private HookResult OnMvp(EventRoundMvp ev, GameEventInfo info)
    {
        if (Config.Enabled && !InWarmup() && Counted(ev.Userid)) StatsOf(ev.Userid!).Mvps++;
        return HookResult.Continue;
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch ev, GameEventInfo info)
    {
        if (!Config.Enabled || _summaryShown) return HookResult.Continue;
        _summaryShown = true;
        AddTimer(Config.SummaryDelay, PrintSummary, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    // ---------- output ----------

    private List<KeyValuePair<string, PlayerStats>> Ranked() =>
        _stats.Where(kv => kv.Value.Rounds >= Math.Max(1, Config.MinRounds))
            .OrderByDescending(kv => kv.Value.Adr)
            .ThenByDescending(kv => kv.Value.Kills)
            .ToList();

    private static string Num(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

    private string Line(CCSPlayerController viewer, PlayerStats s) =>
        T(viewer, "line", Num(s.Adr), s.Kills, s.Deaths, s.Assists, Num(s.HsPercent));

    private void PrintSummary()
    {
        var ranked = Ranked();
        string map = Server.MapName;

        foreach (var viewer in Utilities.GetPlayers().Where(p => Valid(p) && !p.IsBot))
        {
            Say(viewer, T(viewer, "header", map));

            if (ranked.Count == 0)
            {
                Say(viewer, T(viewer, "no_data"));
                continue;
            }

            if (Config.ShowTop)
            {
                for (int i = 0; i < Math.Min(Config.TopCount, ranked.Count); i++)
                {
                    var s = ranked[i].Value;
                    Say(viewer, T(viewer, "top_row", i + 1, s.Name, Line(viewer, s)));
                }
            }

            if (Config.ShowAwards) PrintAwards(viewer, ranked.Select(kv => kv.Value).ToList());

            if (Config.ShowPersonal) PrintPersonal(viewer, ranked);
        }
    }

    private void PrintAwards(CCSPlayerController viewer, List<PlayerStats> pool)
    {
        void Award(string key, Func<PlayerStats, double> by, Func<PlayerStats, string> value)
        {
            var best = pool.OrderByDescending(by).FirstOrDefault();
            if (best != null && by(best) > 0) Say(viewer, T(viewer, key, best.Name, value(best)));
        }

        Award("award_kills", s => s.Kills, s => s.Kills.ToString(CultureInfo.InvariantCulture));
        Award("award_hs", s => s.Kills >= 5 ? s.HsPercent : 0, s => Num(s.HsPercent) + "%");
        Award("award_entry", s => s.Entries, s => s.Entries.ToString(CultureInfo.InvariantCulture));
        Award("award_clutch", s => s.Clutches * 10 + s.BestClutch, s => T(viewer, "clutch_value", s.Clutches, s.BestClutch));
        Award("award_utility", s => s.UtilityDamage, s => s.UtilityDamage.ToString(CultureInfo.InvariantCulture));
        Award("award_mvp", s => s.Mvps, s => s.Mvps.ToString(CultureInfo.InvariantCulture));
    }

    private void PrintPersonal(CCSPlayerController viewer, List<KeyValuePair<string, PlayerStats>> ranked)
    {
        if (!_stats.TryGetValue(KeyOf(viewer), out var mine) || mine.Rounds == 0) return;

        int place = ranked.FindIndex(kv => kv.Key == KeyOf(viewer)) + 1;
        string where = place > 0 ? T(viewer, "place", place, ranked.Count) : T(viewer, "place_none", Config.MinRounds);
        Say(viewer, T(viewer, "personal", where, Line(viewer, mine), mine.K3, mine.K4, mine.K5));
    }

    private void OnStatsCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Valid(player) || player!.IsBot) return;

        if (!Config.Enabled || !_stats.TryGetValue(KeyOf(player), out var mine) || mine.Rounds == 0)
        {
            Say(player, T(player, "stats_empty"));
            return;
        }

        var ranked = Ranked();
        int place = ranked.FindIndex(kv => kv.Key == KeyOf(player)) + 1;
        string where = place > 0 ? T(player, "place", place, ranked.Count) : T(player, "place_none", Config.MinRounds);
        Say(player, T(player, "stats_header", mine.Rounds));
        Say(player, T(player, "personal", where, Line(player, mine), mine.K3, mine.K4, mine.K5));
        Say(player, T(player, "stats_extra", mine.Entries, mine.Clutches, mine.UtilityDamage, mine.FlashAssists, mine.Mvps));
    }

    private string T(CCSPlayerController player, string key, params object[] args) => Localizer.ForPlayer(player, key, args);

    private void Say(CCSPlayerController player, string text) => player.PrintToChat(T(player, "prefix") + " " + text);
}
