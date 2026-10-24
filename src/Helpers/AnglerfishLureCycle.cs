using System;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>Continuous steering; all movement, including recall, is acceleration limited.</summary>
public sealed class AnglerfishLureCycle
{
    public const double MaxReach = 384, MinDistance = 8, MaxDistance = 15;
    public const double CruiseSpeed = 6, MaxSpeed = 8, Acceleration = 8;
    public const int SegmentCount = 128;
    public const double SegmentLength = 4;
    public readonly Vec3d Velocity = new();

    public static Vec3d BaitGoal(Vec3d player, Vec3d anchor, double time)
    {
        double dx=anchor.X-player.X,dz=anchor.Z-player.Z;
        double horizontal=Math.Sqrt(dx*dx+dz*dz);
        // Skim the surface first; gradually lead deeper as the player follows.
        double depthBlend=Math.Clamp(1-horizontal/160,0,1);
        double dy=Math.Min(-.8,(anchor.Y-player.Y)*depthBlend);
        double length=Math.Sqrt(dx*dx+dy*dy+dz*dz);
        double sway=Math.Sin(time*.6)*.65;
        return new Vec3d(player.X+dx/length*11.5+(horizontal>.001?-dz/horizontal*sway:0),
            player.Y+dy/length*11.5+Math.Sin(time*.9)*.18,
            player.Z+dz/length*11.5+(horizontal>.001?dx/horizontal*sway:0));
    }

    // How high the gear hangs over the waves for prey at the surface.
    public const double HoverHeight = 2.5;

    /// <summary>
    /// For prey at the surface: the gear hangs above the water, 11.5 blocks
    /// from them toward the fish and never past it, so following it leads
    /// them over the fish's mouth.
    /// </summary>
    public static Vec3d HoverGoal(Vec3d player, Vec3d anchor, double surfaceY, double time)
    {
        double dx=anchor.X-player.X,dz=anchor.Z-player.Z;
        double horizontal=Math.Sqrt(dx*dx+dz*dz);
        double reach=Math.Min(11.5,horizontal);
        double ux=horizontal>.001?dx/horizontal:0,uz=horizontal>.001?dz/horizontal:0;
        double sway=Math.Sin(time*.6)*.65;
        return new Vec3d(player.X+ux*reach-uz*sway,
            surfaceY+HoverHeight+Math.Sin(time*.9)*.35,
            player.Z+uz*reach+ux*sway);
    }

    public Vec3d Step(Vec3d position,Vec3d goal,float dt,double speed=MaxSpeed)
    {
        var next=position.Clone();
        if(!float.IsFinite(dt) || dt<=0)return next;
        int steps=Math.Max(1,(int)Math.Ceiling(dt*60));
        double tick=dt/steps;
        for(int i=0;i<steps;i++)
        {
            double dx=(goal.X-next.X)*1.8,dy=(goal.Y-next.Y)*1.8,dz=(goal.Z-next.Z)*1.8;
            double length=Math.Sqrt(dx*dx+dy*dy+dz*dz);
            if(length>speed){dx*=speed/length;dy*=speed/length;dz*=speed/length;}
            double ax=dx-Velocity.X,ay=dy-Velocity.Y,az=dz-Velocity.Z;
            double change=Math.Sqrt(ax*ax+ay*ay+az*az),limit=Acceleration*tick;
            if(change>limit){ax*=limit/change;ay*=limit/change;az*=limit/change;}
            Velocity.Add(ax,ay,az);
            next.Add(Velocity.X*tick,Velocity.Y*tick,Velocity.Z*tick);
        }
        return next;
    }
}
