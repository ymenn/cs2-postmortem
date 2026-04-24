using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Postmortem.Replay;

// Hooks that populate MovementFrame.ShotDirection + per-slot ReplayEvent logs.
//
// Skips knife/bayonet for shot beams — melee doesn't have a useful direction.
// All three events are cheap (handler-level) and offload to MovementSampler's
// per-slot state so the sampler owns the lifecycle (disconnect/round clears
// the event log as part of buffer cleanup).
public sealed class EventRecorder
{
    private readonly BasePlugin _plugin;
    private readonly MovementSampler _sampler;
    private readonly StringIntern _intern;
    private readonly Func<bool> _enabled;
    private float _roundStartRealtime;

    public EventRecorder(BasePlugin plugin, MovementSampler sampler, StringIntern intern, Func<bool> enabled)
    {
        _plugin = plugin;
        _sampler = sampler;
        _intern = intern;
        _enabled = enabled;
    }

    public void Start()
    {
        _plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _roundStartRealtime = Server.CurrentTime;
            return HookResult.Continue;
        });

        _plugin.RegisterEventHandler<EventWeaponFire>((@event, _) =>
        {
            if (!_enabled()) return HookResult.Continue;
            var c = @event.Userid;
            if (c is null || !c.IsValid) return HookResult.Continue;

            var activeName = c.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
            if (IsMeleeLike(activeName)) return HookResult.Continue;

            var look = c.PlayerPawn?.Value?.EyeAngles;
            if (look is not null) _sampler.TryStampShot(c.Slot, look);
            var weapon = _intern.Intern(activeName) ?? "unknown";
            _sampler.AppendEvent(c.Slot, new ShotFired(At(), weapon));
            return HookResult.Continue;
        });

        _plugin.RegisterEventHandler<EventPlayerHurt>((@event, _) =>
        {
            if (!_enabled()) return HookResult.Continue;
            var victim = @event.Userid;
            var attacker = @event.Attacker;
            var weapon = _intern.Intern(@event.Weapon) ?? "unknown";
            var at = At();
            if (victim is not null && victim.IsValid)
            {
                var srcSlot = attacker is not null && attacker.IsValid ? attacker.Slot : -1;
                _sampler.AppendEvent(victim.Slot,
                    new DamageTaken(at, srcSlot, @event.DmgHealth, weapon, (byte)@event.Hitgroup));
            }
            if (attacker is not null && attacker.IsValid && victim is not null && victim.IsValid
                && attacker.Slot != victim.Slot)
            {
                _sampler.AppendEvent(attacker.Slot,
                    new DamageDealt(at, victim.Slot, @event.DmgHealth, weapon, (byte)@event.Hitgroup));
            }
            return HookResult.Continue;
        });

        _plugin.RegisterEventHandler<EventItemPickup>((@event, _) =>
        {
            if (!_enabled()) return HookResult.Continue;
            var c = @event.Userid;
            if (c is null || !c.IsValid) return HookResult.Continue;
            var item = _intern.Intern(@event.Item) ?? "unknown";
            _sampler.AppendEvent(c.Slot, new WeaponPickup(At(), item));
            return HookResult.Continue;
        });
    }

    private float At() => Server.CurrentTime - _roundStartRealtime;

    private static bool IsMeleeLike(string? designerName)
    {
        if (string.IsNullOrEmpty(designerName)) return true;
        return designerName.Contains("knife", StringComparison.Ordinal)
            || designerName.Contains("bayonet", StringComparison.Ordinal);
    }
}
