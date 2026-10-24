using UnderwaterHorrors;
using Vintagestory.API.MathTools;

static class SteeringChecks
{
    public static void Run()
    {
        int checks=0;
        void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
        var anchor=new Vec3d(0,-25,0);
        foreach(float dt in new[]{.016666667f,.05f,.25f})
        {
            // Cast across 300 blocks, settle, then follow a swimmer toward the body.
            var steering=new AnglerfishLureCycle();
            var player=new Vec3d(300,0,0);
            var tip=anchor.Clone();
            double worstStep=0,worstAcceleration=0;
            for(double t=0;t<65;t+=dt)
            {
                var previousVelocity=steering.Velocity.Clone();
                var next=steering.Step(tip,AnglerfishLureCycle.BaitGoal(player,anchor,t),dt);
                worstStep=Math.Max(worstStep,tip.DistanceTo(next)/dt);
                worstAcceleration=Math.Max(worstAcceleration,steering.Velocity.DistanceTo(previousVelocity)/dt);
                tip=next;
            }
            Check(worstStep<=8.00001,"Long cast snapped beyond speed limit");
            Check(worstAcceleration<=8.00001,"Long cast exceeded acceleration limit");
            Check(tip.DistanceTo(player)>=8 && tip.DistanceTo(player)<=15,"Cast failed to settle in bait range");
            double minimum=double.MaxValue,maximum=0;
            for(double t=65;t<190;t+=dt)
            {
                player.X-=2*dt;
                tip=steering.Step(tip,AnglerfishLureCycle.BaitGoal(player,anchor,t),dt);
                double range=tip.DistanceTo(player);
                minimum=Math.Min(minimum,range);maximum=Math.Max(maximum,range);
            }
            Check(minimum>=8 && maximum<=15,$"Approaching swimmer left bait band at dt={dt}: {minimum:F2}..{maximum:F2}");
            Check(tip.X<player.X,"Bait failed to lead toward body");
            Check(tip.Y<-1,"Bait never led the player deeper");
            // Faster swimming in either direction still stays in the band;
            // the bait must glide back toward someone who stops following.
            foreach(double swimmerSpeed in new[]{-4d,4d})
            {
                var swimmer=new Vec3d(220,0,0);
                var follower=new AnglerfishLureCycle();
                var bait=AnglerfishLureCycle.BaitGoal(swimmer,anchor,0);
                bool inside=true;
                for(double t=0;t<15;t+=dt)
                {
                    swimmer.X+=swimmerSpeed*dt;
                    bait=follower.Step(bait,AnglerfishLureCycle.BaitGoal(swimmer,anchor,t),dt);
                    double range=bait.DistanceTo(swimmer);
                    inside &= range>=8 && range<=15;
                }
                Check(inside,"Four-block/s swimmer escaped the target spacing band");
            }
            // Abrupt target change must brake rather than instantly reverse.
            var velocity=steering.Velocity.Clone();
            var reversed=steering.Step(tip,new Vec3d(1000,0,0),dt);
            Check(reversed.DistanceTo(tip)<=8*dt+.00001,"Target switch teleported bait");
            Check(steering.Velocity.DistanceTo(velocity)<=8*dt+.00001,"Target switch snapped velocity");
            var recalled=steering.Step(reversed,anchor,dt,AnglerfishLureCycle.CruiseSpeed);
            Check(recalled.DistanceTo(reversed)<=8*dt+.00001,"Recall snapped bait to body");
        }
        var far=AnglerfishLureCycle.BaitGoal(new Vec3d(300,0,0),anchor,0);
        var near=AnglerfishLureCycle.BaitGoal(new Vec3d(40,0,0),anchor,0);
        Check(near.Y<far.Y-3,"Near-body bait does not descend progressively");
        Check(double.IsFinite(AnglerfishLureCycle.BaitGoal(anchor,anchor,0).Y),"Coincident bait target produced NaN");
        float yaw=0,largestTurn=0;
        for(int i=0;i<120;i++)
        {
            float next=AnglerfishAttackCycle.SmoothYaw(yaw,(float)Math.PI,.05f,45);
            largestTurn=Math.Max(largestTurn,Math.Abs(GameMath.AngleRadDistance(yaw,next)));
            yaw=next;
        }
        Check(largestTurn<=45*GameMath.DEG2RAD*.05f+.00001,"Patrol exceeded serpent turn-speed cap");
        Check(Math.Abs(GameMath.AngleRadDistance(yaw,(float)Math.PI))<.01,"Patrol never completed its smooth turn");
        float wrapped=AnglerfishAttackCycle.SmoothYaw(179*GameMath.DEG2RAD,-179*GameMath.DEG2RAD,.05f,45);
        Check(Math.Abs(GameMath.AngleRadDistance(wrapped,-179*GameMath.DEG2RAD))<2*GameMath.DEG2RAD,"Patrol turned the long way across yaw wrap");
        Check(AnglerfishLureCycle.SegmentCount*AnglerfishLureCycle.SegmentLength>AnglerfishLureCycle.MaxReach+80,"Rope lacks slack for moving body");
        Console.WriteLine($"PASS {checks} steering checks: long casts, spacing, descent, acceleration, recall and serpent turning");
    }
}
