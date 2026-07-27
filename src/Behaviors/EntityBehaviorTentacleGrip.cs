using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Turns the invisible tentacle head into a one-seat mount so a grabbed
/// player RIDES the tentacle instead of being teleported by the server
/// once per tick.
///
/// Why this exists: the player entity is client-authoritative. The client
/// simulates its own movement and reports its position upstream, so a
/// server-side TeleportToDouble every tick is a tug of war between the
/// two, which is exactly what the jittery drag looked like. Mounting has
/// no such fight:
///
///   * EntityBehaviorPlayerPhysics, when MountedOn != null, skips player
///     movement entirely and does pos.SetPos(MountedOn.SeatPosition) each
///     physics tick, locally, with no round trip.
///   * EntityBehaviorInterpolatePosition (added to the tentacle head in
///     krakententacle.json) lerps the head's position between server
///     snapshots on every render frame, so SeatPosition is smooth even
///     though the server only sends updates ~15 times a second.
///
/// That is the same path a ridden horse or elk travels, which is why
/// those are perfectly smooth.
///
/// The seat is deliberately NOT controllable and leaves the rider's
/// angles Unaffected: a gripped player cannot swim away, but can still
/// look around freely and swing at the claws, which is the escape
/// mechanic. There is no dismount action wired to the seat controls, so
/// the player cannot simply sneak out of the kraken's grip.
/// </summary>
public class EntityBehaviorTentacleGrip : EntityBehavior, IMountable
{
    /// <summary>
    /// Key written into the rider's "mountedOn" tree attribute. Must
    /// match the string passed to api.RegisterMountable so a reloading
    /// client can rebuild the seat.
    /// </summary>
    public const string MountableClassName = "underwaterhorrors:tentaclegrip";

    /// <summary>
    /// Watched attribute carrying how far above the head the rider sits.
    /// It travels as an attribute rather than being read from the config
    /// because UnderwaterHorrorsModSystem.Config is server-only: a client
    /// reading the config directly would fall back to the default and place
    /// the rider somewhere the server did not, on any server that tuned it.
    /// </summary>
    public const string RiderYOffsetKey = "underwaterhorrors:gripYOffset";

    public const double DefaultRiderYOffset = 0.5;

    private readonly TentacleGripSeat seat;
    private readonly IMountableSeat[] seats;

    public EntityBehaviorTentacleGrip(Entity entity) : base(entity)
    {
        seat = new TentacleGripSeat(this);
        seats = new IMountableSeat[] { seat };
    }

    public IMountableSeat[] Seats => seats;

    public EntityPos Position => entity.Pos;

    // Only meaningful for shape renderers that pitch when stepping up a
    // block. The head is invisible and swims, so there is nothing to tilt.
    public double StepPitch => 0;

    // Null on purpose. A gripped player is cargo, not a driver; handing
    // back a controller would make their client try to simulate the
    // tentacle locally and desync it from the server's AI.
    public Entity Controller => null;

    public Entity OnEntity => entity;

    // No seat can control, so there are no controlling controls. Same
    // answer vanilla's EntityBehaviorSeatable gives in that case.
    public EntityControls ControllingControls => null;

    public bool AnyMounted() => seat.Passenger != null;

    /// <summary>The entity currently held, or null.</summary>
    public Entity GrippedEntity => seat.Passenger;

    /// <summary>
    /// Vertical gap between the head entity and the rider's feet. Read this
    /// on both sides so the claws (placed by the server) and the rider
    /// (placed by the client) always land at the same height.
    /// </summary>
    public double RiderYOffset =>
        entity.WatchedAttributes.GetDouble(RiderYOffsetKey, DefaultRiderYOffset);

    public void SetRiderYOffset(double offset)
    {
        entity.WatchedAttributes.SetDouble(RiderYOffsetKey, offset);
    }

    /// <summary>
    /// Seats the given entity on the tentacle. Safe to call every tick:
    /// returns true straight away if it is already the passenger.
    /// </summary>
    public bool Grip(EntityAgent target)
    {
        if (target == null || !target.Alive) return false;
        if (seat.Passenger == target) return true;
        if (seat.Passenger != null) return false;

        return target.TryMount(seat);
    }

    /// <summary>
    /// Lets go of whoever is held. Idempotent.
    /// </summary>
    public void Release()
    {
        if (seat.Passenger is EntityAgent agent)
        {
            agent.TryUnmount();
        }
        seat.Passenger = null;
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        Release();
        base.OnEntityDespawn(despawn);
    }

    public override void OnEntityDeath(DamageSource damageSourceForDeath)
    {
        Release();
        base.OnEntityDeath(damageSourceForDeath);
    }

    public override string PropertyName() => "underwaterhorrors:tentaclegrip";
}

/// <summary>
/// The single seat on a tentacle head. Position is the head plus a
/// vertical offset, so the head hangs just under the rider's feet the
/// same way it did when the old code pinned it there by hand.
/// </summary>
public class TentacleGripSeat : IMountableSeat
{
    private readonly EntityBehaviorTentacleGrip grip;
    private readonly EntityControls controls = new EntityControls();
    private readonly EntityPos seatPos = new EntityPos();

    public TentacleGripSeat(EntityBehaviorTentacleGrip grip)
    {
        this.grip = grip;
    }

    // No JSON config for this seat: it is created in code, not from an
    // entity's "seats" attribute list.
    public SeatConfig Config { get; set; }
    public string SeatId { get; set; } = "tentaclegrip";
    public long PassengerEntityIdForInit { get; set; }
    public bool DoTeleportOnUnmount { get; set; } = true;

    public Entity Entity => grip.OnEntity;
    public Entity Passenger { get; set; }
    public IMountable MountSupplier => grip;

    public bool CanControl => false;

    // Unaffected: the kraken holds the player's body, not their head. They
    // keep full camera freedom so they can find and hit the claws.
    public EnumMountAngleMode AngleMode => EnumMountAngleMode.Unaffected;

    public AnimationMetaData SuggestedAnimation => null;
    public bool SkipIdleAnimation => true;

    // 1 is the no-mount default; first person hands keep behaving normally.
    public float FpHandPitchFollow => 1f;

    // Null means "leave the rider's own eye height alone" (EntityPlayer
    // only overrides LocalEyePos when the seat supplies one).
    public Vec3f LocalEyePos => null;

    // No render offset needed. The player renders where they are held.
    public Matrixf RenderTransform => null;

    public EntityControls Controls => controls;

    /// <summary>
    /// The angle a seat reports is NOT free. In first person the client runs
    /// this every frame, for any mount, whatever the AngleMode:
    ///
    ///     kick = num3 * (SeatPosition.Yaw - prevMountAngles.Y)
    ///     prevMountAngles.Y += kick;  mouseYaw += kick;  Pos.Yaw += kick
    ///
    /// It exists so a turning horse turns its rider. It is a feedback loop
    /// the moment SeatPosition.Yaw is derived from the rider's own yaw:
    /// the kick lands on Pos.Yaw, Pos.Yaw feeds the next SeatPosition.Yaw,
    /// the difference never decays, and the view spins at a constant rate
    /// that any mouse movement makes worse. (Third person hides it, since
    /// the branch is first person only.)
    ///
    /// So the rider's own client gets a CONSTANT facing here, which makes
    /// the difference decay to zero within a few frames and leaves the
    /// camera alone from then on. Zero specifically, because that is what
    /// prevMountAngles starts at, so a player who has not ridden anything
    /// else this session gets no kick at all.
    ///
    /// Everyone ELSE still needs the rider's real angles: their client draws
    /// the passenger with Pos.SetFrom(SeatPosition), which copies angles, so
    /// a constant there would freeze every onlooker's view of the gripped
    /// player to one heading. The two consumers never overlap on the same
    /// client, which is what makes the split safe.
    /// </summary>
    public EntityPos SeatPosition
    {
        get
        {
            EntityPos head = grip.OnEntity.Pos;

            if (Passenger != null && !PassengerIsLocalPlayer)
            {
                seatPos.SetFrom(Passenger.Pos);
            }
            else
            {
                seatPos.Yaw = 0;
                seatPos.Pitch = 0;
                seatPos.Roll = 0;
                seatPos.HeadYaw = 0;
                seatPos.HeadPitch = 0;
            }

            seatPos.SetPos(head.X, head.Y + grip.RiderYOffset, head.Z);
            seatPos.Dimension = head.Dimension;
            seatPos.Motion.Set(0, 0, 0);
            return seatPos;
        }
    }

    /// <summary>
    /// True only on the gripped player's own client. False on the server and
    /// on every other client, where this rider is just another entity.
    /// </summary>
    private bool PassengerIsLocalPlayer
    {
        get
        {
            if (Passenger == null) return false;
            return grip.OnEntity.Api is ICoreClientAPI capi
                && capi.World?.Player?.Entity == Passenger;
        }
    }

    public bool CanMount(EntityAgent entityAgent) => Passenger == null || Passenger == entityAgent;

    // Always true. The player has no dismount action wired to this seat,
    // so nothing they press can free them; the AI is what lets go. Refusing
    // here would risk a rider stuck mounted to a dead tentacle if any code
    // path (client attribute sync, despawn, respawn) tried to unmount and
    // could not.
    public bool CanUnmount(EntityAgent entityAgent) => true;

    public void DidMount(EntityAgent entityAgent)
    {
        Passenger = entityAgent;
    }

    public void DidUnmount(EntityAgent entityAgent)
    {
        Passenger = null;
    }

    public void MountableToTreeAttributes(TreeAttribute tree)
    {
        tree.SetString("className", EntityBehaviorTentacleGrip.MountableClassName);
        tree.SetLong("entityId", grip.OnEntity.EntityId);
        tree.SetString("seatId", SeatId);
    }
}
