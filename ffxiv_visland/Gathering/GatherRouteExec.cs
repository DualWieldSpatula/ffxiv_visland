using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons;
using ECommons.CircularBuffers;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using visland.Helpers;
using visland.IPC;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace visland.Gathering;

public class GatherRouteExec : IDisposable
{
    public GatherRouteDB RouteDB;
    public GatherRouteDB.Route? CurrentRoute;
    public int CurrentWaypoint;
    public bool ContinueToNext;
    public bool Paused;
    public bool Loop;
    public bool Waiting;
    public long WaitUntil;
    public bool Pathfind;
    public AddonMaster.Gathering? GatheringAM;
    public AddonMaster.Gathering.GatheredItem? GatheredItem;
    public AddonMaster.GatheringMasterpiece? GatheringCollectableAM;

    public State CurrentState;
    public enum State
    {
        None,
        WaitingForNavmesh,
        Gathering,
        Paused,
        Pathfinding,
        Moving,
        WaitingForDestination,
        Eating,
        Mounting,
        Dismounting,
        Jumping,
        JobSwapping,
        Teleporting,
        Interacting,
        ExtractingMateria,
        PurifyingCollectables,
        RepairingGear,
        Waiting,
        AdjustingPosition,
    }

    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();

    private Throttle _interact = new();
    private CircularBuffer<long> Errors = new(5);

    public GatherRouteExec()
    {
        RouteDB = Service.Config.Get<GatherRouteDB>();
    }

    public void Dispose()
    {
    }

    public void Start(GatherRouteDB.Route route, int waypoint, bool continueToNext, bool loopAtEnd, bool pathfind = false)
    {
        CurrentState = State.None;
        CurrentRoute = route;
        route.Waypoints.RemoveAll(x => x.IsPhantom);
        CurrentWaypoint = waypoint;
        ContinueToNext = continueToNext;
        Loop = loopAtEnd;
        route.Waypoints[waypoint].Pathfind = pathfind;
        Pathfind = pathfind;
        _camera.Enabled = true;
        _movement.Enabled = true;
    }

    public void Finish()
    {
        SetState(State.None);
        if (CurrentRoute == null) return;

        CurrentRoute.Waypoints.RemoveAll(x => x.IsPhantom);
        CurrentRoute = null;
        CurrentWaypoint = 0;
        ContinueToNext = false;
        Waiting = false;
        Paused = false;
        _camera.Enabled = false;
        _movement.Enabled = false;
        CompatModule.RestoreChanges();
        if (Pathfind && NavmeshIPC.IsRunning())
            NavmeshIPC.Stop();
    }

    public unsafe void Update()
    {
        _camera.SpeedH = _camera.SpeedV = default;
        _movement.DesiredPosition = Player.Object?.Position ?? new();

        if (Paused && NavmeshIPC.IsRunning())
            NavmeshIPC.Stop();

        if (!Player.Available || Player.Object!.IsCasting || Player.Mounting || Player.IsJumping || Paused || CurrentRoute == null || P.TaskManager.IsBusy || CurrentWaypoint >= CurrentRoute.Waypoints.Count)
            return;

        CompatModule.EnsureCompatibility(RouteDB);

        if (RouteDB.AutoGather && GatheringAM != null && GatheredItem != null && !Player.InGatheringAnimation)
        {
            SetState(State.Gathering);
            GatheringActions.UseNextBestAction(GatheringAM, GatheredItem);
            return;
        }

        if (RouteDB.AutoGather && GatheringCollectableAM != null && !Player.InGatheringAnimation)
        {
            SetState(State.Gathering);
            GatheringActions.UseNextBestAction(GatheringCollectableAM);
            return;
        }

        if (GenericHelpers.IsOccupied()) return; // must check after auto gathering

        var wp = CurrentRoute.Waypoints[CurrentWaypoint];
        var toWaypoint = wp.Position - Player.Object.Position;
        var needToGetCloser = toWaypoint.LengthSquared() > wp.Radius * wp.Radius;
        Pathfind = wp.Pathfind;

        if (wp.IsPhantom && wp.InteractWithOID == 0)
        {
            var obj = Svc.Objects.FirstOrDefault(o => o?.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable && o?.Position.X - CurrentRoute.Waypoints[CurrentWaypoint].InteractWithPosition.X < 5 && o?.Position.Z - CurrentRoute.Waypoints[CurrentWaypoint].InteractWithPosition.Z < 5, null);
            if (obj != null)
            {
                wp.InteractWithOID = obj.BaseId;
                wp.InteractWithName = obj.Name.TextValue;
                wp.InteractWithPosition = obj.Position;
            }
        }

        var food = CurrentRoute.Food != 0 ? CurrentRoute.Food : RouteDB.GlobalFood != 0 ? RouteDB.GlobalFood : 0;
        if (food != 0 && Player.HasFood((uint)food) && !Player.HasFoodBuff && Player.AnimationLock == 0)
        {
            SetState(State.Eating);
            PluginLog.Debug($"Eating {GenericHelpers.GetRow<Item>((uint)food)?.Name}");
            Player.EatFood(food);
            return;
        }

        if (RouteDB.TeleportBetweenZones && wp.ZoneID != default && Coordinates.HasAetheryteInZone((uint)wp.ZoneID) && Player.Territory.RowId != wp.ZoneID)
        {
            SetState(State.Teleporting);
            PluginLog.Information($"Teleporting from [{Player.Territory}] to [{wp.ZoneID}] {Coordinates.GetNearestAetheryte(wp.ZoneID, wp.Position)}");
            P.TaskManager.Enqueue(() => Telepo.Instance()->Teleport(Coordinates.GetNearestAetheryte(wp.ZoneID, wp.Position), 0));
            P.TaskManager.Enqueue(() => Player.Object?.IsCasting);
            P.TaskManager.Enqueue(() => Player.Territory.RowId == wp.ZoneID);
            return;
        }

        if (wp.InteractWithOID != default && !Player.IsOnIsland && wp.IsNode && Player.Job != wp.NodeJob)
        {
            SetState(State.JobSwapping);
            PluginLog.Debug($"Changing job to {wp.NodeJob}");
            P.TaskManager.Enqueue(() => Player.SwitchJob(wp.NodeJob));
            return;
        }

        if (needToGetCloser)
        {
            if (wp.IsNode && Player.DistanceTo(wp.Position) < 50 && !Svc.Objects.Any(x => x.BaseId == wp.InteractWithOID && x.IsTargetable))
            {
                PluginLog.Debug("Current waypoint target is not targetable, moving to next waypoint");
                if (NavmeshIPC.IsRunning())
                    NavmeshIPC.Stop();
                goto next;
            }

            if (NavmeshIPC.IsRunning()) { SetState(State.WaitingForDestination); return; }
            if (wp.Movement != GatherRouteDB.Movement.Normal && !Player.Mounted)
            {
                SetState(State.Mounting);
                Player.Mount();
                return;
            }

            Player.Sprint();

            if (wp.Movement == GatherRouteDB.Movement.MountFly && Player.Mounted && !
