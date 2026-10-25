using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace UnderwaterHorrors;

/// <summary>Artist-facing model inspection, independent of the ocean encounter director.</summary>
public class AnglerfishModelSystem : ModSystem
{
    private ICoreServerAPI sapi;

    public override void Start(ICoreAPI api)
    {
        api.RegisterEntity("GiantAnglerfishEntity", typeof(GiantAnglerfishEntity));
        api.RegisterEntity("AnglerfishLureEntity", typeof(AnglerfishLureEntity));
        api.RegisterEntityBehaviorClass("underwaterhorrors:anglerfishanimation", typeof(EntityBehaviorAnglerfishAnimation));
        api.RegisterEntityBehaviorClass("underwaterhorrors:anglerfishai", typeof(EntityBehaviorAnglerfishAI));
        api.RegisterEntityBehaviorClass("underwaterhorrors:anglerfishlure", typeof(EntityBehaviorAnglerfishLure));
        api.RegisterEntityBehaviorClass("underwaterhorrors:anglerfishluresegment", typeof(EntityBehaviorAnglerfishLureSegment));
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.ChatCommands.Create("uhangler")
            .WithDescription("Giant anglerfish model and animation inspection")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("show")
                .WithDescription("Place an animated anglerfish 60 blocks ahead for inspection")
                .HandleWith(Show)
            .EndSubCommand()
            .BeginSubCommand("anim")
                .WithDescription("Play a clip on the nearest living anglerfish within 128 blocks; off restores automatic animation")
                .WithArgs(api.ChatCommands.Parsers.WordRange("clip", "idle", "swim", "swimfast", "lure", "bite", "swallow", "hurt", "die", "off"))
                .HandleWith(Animate)
            .EndSubCommand();
    }

    private TextCommandResult Show(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player?.Entity;
        if (player == null) return TextCommandResult.Error("Run this command as a player.");
        var type = sapi.World.GetEntityType(new AssetLocation("underwaterhorrors", "giantanglerfish-display"));
        if (type == null) return TextCommandResult.Error("The giantanglerfish entity asset is missing.");
        var fish = sapi.World.ClassRegistry.CreateEntity(type);
        fish.Pos.SetPos(player.Pos.X + Math.Sin(player.Pos.Yaw) * 60, player.Pos.Y, player.Pos.Z + Math.Cos(player.Pos.Yaw) * 60);
        fish.Pos.Dimension = player.Pos.Dimension;
        fish.Pos.Yaw = player.Pos.Yaw + (float)Math.PI;
        fish.WatchedAttributes.SetBool("underwaterhorrors:static",true);
        sapi.World.SpawnEntity(fish);
        fish.GetBehavior<EntityBehaviorAnglerfishAnimation>()?.Preview("swim");
        return TextCommandResult.Success("Anglerfish display placed for inspection. /uhangler anim swallow previews clips; /uh spawn anglerfish creates the hostile creature.");
    }

    private TextCommandResult Animate(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player?.Entity;
        if (player == null) return TextCommandResult.Error("Run this command as a player.");
        EntityBehaviorAnglerfishAnimation nearest = null;
        double distance = 128;
        foreach (var fish in sapi.World.LoadedEntities.Values)
        {
            if (fish is not GiantAnglerfishEntity || !fish.Alive || fish.Pos.Dimension != player.Pos.Dimension) continue;
            var d = fish.Pos.DistanceTo(player.Pos.XYZ);
            if (d >= distance) continue;
            var behavior = fish.GetBehavior<EntityBehaviorAnglerfishAnimation>();
            if (behavior == null) continue;
            nearest = behavior;
            distance = d;
        }
        if (nearest == null) return TextCommandResult.Error("No living anglerfish within 128 blocks. Use /uhangler show.");
        string code = args.Parsers[0].GetValue() as string;
        nearest.Preview(code == "off" ? null : code);
        return TextCommandResult.Success(code == "off" ? "Automatic anglerfish animation restored." : $"Anglerfish playing {code}; one-shot clips hold their final pose. Repeat the command to replay.");
    }
}

public class GiantAnglerfishEntity : EntityAgent
{
    // Its first ticks start a clip while the server writes its spawn packet
    // on another thread; see ThreadSafeAnimationManager.
    public GiantAnglerfishEntity() { AnimManager = new ThreadSafeAnimationManager(); }

    public override bool AlwaysActive => true;
    // Its head is some 30 blocks across and its tail trails 130 behind.
    public override double FrustumSphereRadius => 140;
    public override bool CanStepPitch => false;
    public override bool CanSwivel => false;
    public override bool CanSwivelNow => false;
}

public class AnglerfishLureEntity : EntityKrakenPart
{
    public override bool AlwaysActive => true;
    public override double FrustumSphereRadius => 6;
    // The gear can be hit (10 health, two temporal gears); the rope pieces,
    // which share this class, cannot.
    public override bool IsInteractable => Code?.Path == "anglerfishlure";
}

/// <summary>
/// Uses the same native shape-keyframe / server AnimationMetaData path as the serpent.
/// The model is spawnable for inspection; future movement/combat AI can call PlayAction.
/// </summary>
public class EntityBehaviorAnglerfishAnimation : EntityBehavior
{
    public static readonly HashSet<string> Clips = new(StringComparer.Ordinal)
        { "idle", "swim", "swimfast", "lure", "bite", "swallow", "hurt", "die" };
    private string current;
    private string preview;
    private string action;
    private float actionRemaining;
    private bool dead;
    private string locomotion;
    private float locomotionSpeed = 1, currentSpeed = 1;
    public bool IsPreviewing => preview != null;

    public EntityBehaviorAnglerfishAnimation(Entity entity) : base(entity) { }
    public override string PropertyName() => "anglerfishanimation";
    public void SetLocomotion(string code, float speed = 1)
    { if (Clips.Contains(code)) { locomotion=code; locomotionSpeed=speed; } }

    /// <summary>The bite, sped up so its jaws snap shut (frame 49) as the swoop arrives.</summary>
    public void PlayLunge(float secondsToContact)
    {
        if(entity.Api.Side!=EnumAppSide.Server || !entity.Alive || preview!=null)return;
        float speed=Math.Clamp(49f/30/Math.Max(.2f,secondsToContact),1f,4f);
        action="bite";actionRemaining=76f/30/speed;
        Play("bite",true,speed);
    }

    public void Preview(string code)
    {
        if (entity.Api.Side != EnumAppSide.Server || (code != null && !Clips.Contains(code))) return;
        preview = code;
        action = null;
        actionRemaining = 0;
        Play(code ?? "idle", true);
    }

    public void PlayAction(string code)
    {
        if (entity.Api.Side != EnumAppSide.Server || !entity.Alive) return;
        float duration = code switch { "bite" => 76f / 30, "swallow" => 106f / 30, "hurt" => 28f / 30, _ => 0 };
        if (duration == 0) return;
        action = code;
        actionRemaining = duration;
        Play(code, true);
    }

    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        base.OnEntityReceiveDamage(damageSource, ref damage);
        if (damage > 0 && damageSource?.Type != EnumDamageType.Heal && preview == null
            && entity.GetBehavior<EntityBehaviorAnglerfishAI>()?.IsLunging!=true) PlayAction("hurt");
    }

    public override void OnGameTick(float deltaTime)
    {
        if (entity.Api.Side != EnumAppSide.Server || entity.AnimManager == null) return;
        if (!entity.Alive)
        {
            if (!dead) { dead = true; preview = null; action = null; Play("die", true); }
            return;
        }
        if (dead) { dead = false; current = null; }
        if (preview != null) { Play(preview); return; }
        if (actionRemaining > 0)
        {
            actionRemaining -= deltaTime;
            if (actionRemaining > 0) { return; }
            action = null;
        }
        double speedSquared = entity.Pos.Motion.X * entity.Pos.Motion.X + entity.Pos.Motion.Z * entity.Pos.Motion.Z;
        Play(locomotion ?? (speedSquared > .01 ? "swimfast" : speedSquared > .00001 ? "swim" : "idle"), speed: locomotionSpeed);
    }

    private void Play(string code, bool restart = false, float speed = 1)
    {
        if (entity.AnimManager == null) return;
        if (current == code && currentSpeed == speed)
        {
            if (restart) entity.AnimManager.ResetAnimation(code);
            return;
        }
        // Same clip at a new speed (lying in wait vs. patrolling): change it in
        // place. Stopping and restarting would jump the clip back to its start.
        if (current == code && !restart
            && entity.AnimManager.ActiveAnimationsByAnimCode.TryGetValue(code, out var running))
        {
            running.AnimationSpeed = speed;
            currentSpeed = speed;
            entity.AnimManager.AnimationsDirty = true;
            return;
        }
        if (current != null) entity.AnimManager.StopAnimation(current);
        current = code;
        currentSpeed = speed;
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Code = code, Animation = code, AnimationSpeed = speed,
            EaseInSpeed = code == "bite" || code == "hurt" ? 20 : 6,
            EaseOutSpeed = 6, BlendMode = EnumAnimationBlendMode.Add
        }.Init());
    }
}
