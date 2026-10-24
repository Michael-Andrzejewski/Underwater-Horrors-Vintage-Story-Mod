using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace UnderwaterHorrors;

/// <summary>
/// An animation manager whose list of running clips is safe to send while
/// the entity ticks.
///
/// The server writes a new entity's spawn packet on a helper thread
/// (PhysicsManager.SendEntitySpawns), and that packet lists the running
/// clips. The entity's first ticks run on the main thread at the same time
/// and start its clips. Starting or stopping one while the list was being
/// written threw "Collection was modified", the spawn packet was lost, and
/// the client never saw the entity: a giant anglerfish that bit but stayed
/// invisible. Saving the world also writes the list off the main thread.
///
/// Every change and every walk of the list here takes the same lock.
/// </summary>
public class ThreadSafeAnimationManager : AnimationManager
{
    private readonly object sync = new();

    public override bool StartAnimation(AnimationMetaData animdata)
    {
        lock (sync) return base.StartAnimation(animdata);
    }

    public override bool StartAnimation(string configCode)
    {
        lock (sync) return base.StartAnimation(configCode);
    }

    public override void StopAnimation(string code)
    {
        lock (sync) base.StopAnimation(code);
    }

    public override void StopAllAnimations()
    {
        lock (sync) base.StopAllAnimations();
    }

    public override void OnServerTick(float dt)
    {
        lock (sync) base.OnServerTick(dt);
    }

    public override void FromAttributes(ITreeAttribute tree, string version)
    {
        lock (sync) base.FromAttributes(tree, version);
    }

    protected override void SerializeActiveAnimations(bool forClient, Action<string, FastMemoryStream> output)
    {
        lock (sync) base.SerializeActiveAnimations(forClient, output);
    }
}
