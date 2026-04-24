using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Menn.Utils;

// Shared slot-indexed player cache. Each plugin wires it up once in Load and
// reads from it at tick rate without calling Utilities.GetPlayers()
// (CSSHARP-CONVENTIONS.md §11 — `GetPlayers` is O(MaxSlots) with native
// lookups, not cheap at 10+ Hz).
//
// Design:
//   - Slot-indexed array (size 65) for O(1) lookup. Slots are CSSharp's stable
//     handle for the lifetime of a connection.
//   - Primitives mirror a CSSharp `CCSPlayerController` surface area that most
//     plugins actually need: SteamId, Slot, Name, IsBot, Team, IsAlive + the
//     raw controller reference as an escape hatch for adapter code.
//   - Self-wiring lifecycle: Start(plugin) subscribes the cache to the CSSharp
//     events that mutate it (connect, disconnect, team, spawn, death). Plugins
//     don't need to know the event list, just call Start.
//   - Heal-on-miss: if a hot path asks about a slot that should be cached but
//     isn't (race: first sampler tick before OnClientPutInServer fires),
//     GetOrHeal re-fetches via Utilities.GetPlayerFromSlot and counts the
//     miss — non-zero counters are a signal something is racing or a
//     lifecycle event got lost.
//   - Event callbacks (OnAdd / OnRemove / OnTeamChange / OnSpawn / OnDeath)
//     let plugins extend behavior without duplicating the event wiring.
//
// Bot SteamIDs: every bot shares SteamID=0, so the raw SteamId cannot be used
// as a cache key. This cache is slot-indexed; plugins that need SteamID→slot
// lookups should maintain their own map and use SyntheticBotId to distinguish
// bots for event-bus purposes.
public sealed class PlayerCache
{
    public sealed class Entry
    {
        public CCSPlayerController Controller = default!;
        public int Slot;
        public ulong SteamId;
        public string Name = "";
        public bool IsBot;
        public CsTeam Team;
        public bool IsAlive;
    }

    // CS2 servers are capped at 64 slots; +1 for 1-based indexing headroom.
    private const int MaxSlots = 65;

    private readonly Entry?[] _slots = new Entry?[MaxSlots];
    private int _count;
    public int Count => _count;

    // Misses/heals exposed for /pmstats-style introspection. Non-zero steady
    // state = a CSSharp lifecycle event fired on the wrong slot or we're
    // racing with OnClientPutInServer.
    public int MissCount { get; private set; }
    public int HealCount { get; private set; }
    public int ReconcileCount { get; private set; }

    private readonly ILogger _logger;

    // Callbacks — plugins subscribe here instead of re-registering CSSharp
    // event handlers. Invoked synchronously on the game thread after the cache
    // mutation completes. A null check is cheap; missing subscribers are fine.
    public event Action<Entry>? OnAdd;
    public event Action<ulong, int>? OnRemove;        // (steamId, slot)
    public event Action<Entry>? OnTeamChange;
    public event Action<Entry>? OnSpawn;
    public event Action<Entry>? OnDeath;

    public PlayerCache(ILogger logger) { _logger = logger; }

    public void Start(BasePlugin plugin, bool hotReload)
    {
        // OnClientPutInServer fires for every entity including bots (unlike
        // EventPlayerConnectFull which can be unreliable for mid-session bot_add
        // on some CS2 builds). Both paths covered — AddOrUpdate is idempotent.
        plugin.RegisterListener<Listeners.OnClientPutInServer>(slot =>
        {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c is not null && c.IsValid) AddOrUpdate(c);
        });

        plugin.RegisterEventHandler<EventPlayerConnectFull>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } c) AddOrUpdate(c);
            return HookResult.Continue;
        });

        plugin.RegisterEventHandler<EventPlayerDisconnect>((@event, _) =>
        {
            var c = @event.Userid;
            if (c is null || !c.IsValid) return HookResult.Continue;
            RemoveSlot(c.Slot);
            return HookResult.Continue;
        });

        plugin.RegisterEventHandler<EventPlayerTeam>((@event, _) =>
        {
            var c = @event.Userid;
            if (c is null || !c.IsValid) return HookResult.Continue;
            if (_slots[c.Slot] is { } e)
            {
                e.Team = (CsTeam)@event.Team;
                OnTeamChange?.Invoke(e);
            }
            return HookResult.Continue;
        });

        plugin.RegisterEventHandler<EventPlayerSpawn>((@event, _) =>
        {
            var c = @event.Userid;
            if (c is null || !c.IsValid) return HookResult.Continue;
            if (_slots[c.Slot] is { } e)
            {
                e.IsAlive = true;
                OnSpawn?.Invoke(e);
            }
            return HookResult.Continue;
        });

        plugin.RegisterEventHandler<EventPlayerDeath>((@event, _) =>
        {
            var c = @event.Userid;
            if (c is null || !c.IsValid) return HookResult.Continue;
            if (_slots[c.Slot] is { } e)
            {
                e.IsAlive = false;
                OnDeath?.Invoke(e);
            }
            return HookResult.Continue;
        });

        plugin.RegisterListener<Listeners.OnMapStart>(_ => Reconcile());

        if (hotReload) Reconcile();
    }

    // Wipe + rehydrate from the engine's authoritative player list. Cheap to
    // call on map change or after hot reload. ReconcileCount exposed so ops
    // can spot abnormal reconciliation churn.
    public void Reconcile()
    {
        for (var i = 0; i < MaxSlots; i++) _slots[i] = null;
        _count = 0;
        foreach (var c in Utilities.GetPlayers())
        {
            if (c is null || !c.IsValid) continue;
            AddOrUpdate(c);
        }
        ReconcileCount++;
        _logger.LogInformation("PlayerCache reconciled: {Count} players", _count);
    }

    private void AddOrUpdate(CCSPlayerController c)
    {
        if (c.Slot < 0 || c.Slot >= MaxSlots) return;
        var existing = _slots[c.Slot];
        var isNew = existing is null;
        var entry = existing ?? new Entry();
        entry.Controller = c;
        entry.Slot = c.Slot;
        entry.SteamId = c.SteamID;
        entry.Name = c.PlayerName ?? "";
        entry.IsBot = c.IsBot;
        entry.Team = c.Team;
        entry.IsAlive = c.PawnIsAlive;
        if (isNew)
        {
            _slots[c.Slot] = entry;
            _count++;
        }
        if (isNew) OnAdd?.Invoke(entry);
    }

    private void RemoveSlot(int slot)
    {
        if (slot < 0 || slot >= MaxSlots) return;
        var entry = _slots[slot];
        if (entry is null) return;
        _slots[slot] = null;
        _count--;
        OnRemove?.Invoke(entry.SteamId, slot);
    }

    public Entry? Get(int slot)
    {
        if (slot < 0 || slot >= MaxSlots) return null;
        return _slots[slot];
    }

    // Heal-on-miss: if the sampler asks about a slot that hasn't been cached
    // yet (race between OnClientPutInServer and the first tick), re-fetch
    // live and cache. Non-zero MissCount/HealCount over a long session =
    // something is slipping through the lifecycle wiring.
    public Entry? GetOrHeal(int slot)
    {
        var e = Get(slot);
        if (e is not null) return e;
        MissCount++;
        var c = Utilities.GetPlayerFromSlot(slot);
        if (c is null || !c.IsValid) return null;
        AddOrUpdate(c);
        HealCount++;
        return Get(slot);
    }

    public IEnumerable<Entry> All()
    {
        for (var i = 0; i < MaxSlots; i++)
            if (_slots[i] is { } e) yield return e;
    }

    // Only T/CT players that are both valid and alive. Used by hot-path
    // consumers (samplers, balance executors) that skip spectators/None/dead.
    public IEnumerable<Entry> AliveCombatants()
    {
        for (var i = 0; i < MaxSlots; i++)
        {
            var e = _slots[i];
            if (e is null) continue;
            if (!e.IsAlive) continue;
            if (e.Team != CsTeam.Terrorist && e.Team != CsTeam.CounterTerrorist) continue;
            if (!e.Controller.IsValid) continue;
            yield return e;
        }
    }

    public IEnumerable<Entry> ByTeam(CsTeam team)
    {
        for (var i = 0; i < MaxSlots; i++)
        {
            var e = _slots[i];
            if (e is null) continue;
            if (e.Team != team) continue;
            yield return e;
        }
    }

    // Bots share SteamID=0 in the engine. Plugins that need SteamID-keyed data
    // structures (event bus, ban cache, etc.) must distinguish bots without
    // colliding with real SteamID64s. 0xF000... is outside the real SteamID
    // range (real IDs start ~0x0110_0001_0000_0000) so there's no collision.
    public static ulong SyntheticBotId(int slot) => 0xF000_0000_0000_0000UL | (uint)slot;
    public static bool IsSyntheticBotId(ulong id) => (id & 0xF000_0000_0000_0000UL) == 0xF000_0000_0000_0000UL;
}
