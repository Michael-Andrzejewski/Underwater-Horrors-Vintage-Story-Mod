using UnderwaterHorrors;

static class CombatChecks
{
    public static void Run()
    {
        int checks=0;
        void Check(bool result,string description){checks++;if(!result)throw new Exception(description);}
        var cycle=new AnglerfishAttackCycle();
        Check(cycle.TriggerRange==14,"Strike range default changed");
        Check(!cycle.TryBegin(cycle.TriggerRange+.001),"Attack outside the mouth's strike range");
        Check(!cycle.TryBegin(double.NaN),"NaN attack range accepted");
        Check(!cycle.TryBegin(double.PositiveInfinity),"Infinite attack range accepted");
        Check(!cycle.TryBegin(-1),"Negative range accepted");
        Check(cycle.TryBegin(cycle.TriggerRange),"Inclusive range boundary failed");
        float received=0;
        for(int i=0;i<20;i++)if(cycle.TryHit())received+=AnglerfishAttackCycle.Damage;
        Check(received==50,"Repeated collision ticks applied more than one 50-damage hit");
        Check(!cycle.TryBegin(0),"Active attack restarted");
        cycle.Finish();
        Check(!cycle.TryHit(),"Damage after attack completion");
        Check(!cycle.TryBegin(0),"Immediate repeat attack bypassed cooldown");
        cycle.Tick(5.9f);Check(!cycle.TryBegin(0),"Cooldown ended too early");
        cycle.Tick(.11f);Check(cycle.TryBegin(0),"Cooldown never cleared");
        Check(cycle.TryHit(),"New attack retained old damage latch");
        foreach(float dt in new[]{.01f,.05f,.25f})
        {
            var timed=new AnglerfishAttackCycle();timed.TryBegin(1);
            for(int i=0;i<(int)Math.Ceiling(2.1/dt);i++)timed.Tick(dt);
            Check(timed.Phase==AnglerfishPhase.Recovering,"Lunge failed to time out at tick size "+dt);
            Check(!timed.TryHit(),"Timed-out lunge could damage");
        }
        var blocked=new AnglerfishAttackCycle();blocked.TryBegin(8);blocked.Finish();
        Check(!blocked.TryHit(),"Blocked/invalid-target strike could damage");
        var returning=new AnglerfishAttackCycle();returning.TryBegin(8);returning.Finish();returning.Tick(6);
        Check(returning.Phase==AnglerfishPhase.Returning,"Recovery did not enter return phase");
        returning.ArriveHome();Check(returning.Phase==AnglerfishPhase.Lurking,"Return did not restore lurk");
        // A high-speed mouth step crosses the victim although neither endpoint
        // overlaps: the distance-to-segment test must register it.
        Check(AnglerfishAttackCycle.DistanceToSegment(5,0,0,0,0,0,10,0,0)==0,"Swept hit missed victim between endpoints");
        Check(AnglerfishAttackCycle.DistanceToSegment(5,4,0,0,0,0,10,0,0)==4,"Swept test hit player outside mouth radius");
        Check(AnglerfishAttackCycle.DistanceToSegment(0,3,4,0,0,0,0,0,0)==5,"Stationary mouth test failed");
        Check(AnglerfishAttackCycle.DistanceToSegment(20,8,0,0,8,0,24,8,0)==0,"Main-body proximity ignored rear trunk");
        Check(AnglerfishAttackCycle.DistanceToSegment(0,40,0,0,8,0,24,8,0)>14,"Distant lure proximity incorrectly triggered body attack");
        SteeringChecks.Run();
        Check(AnglerfishAttackCycle.LungeSpeed==28 && AnglerfishAttackCycle.Damage==50,"Lunge speed/damage changed");
        Console.WriteLine($"PASS {checks} combat checks: range, damage, cooldown, timeout, return and swept contact");
    }
}
