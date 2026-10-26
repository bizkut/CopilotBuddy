// Decompiled with JetBrains decompiler
// Type: Bots.Quest.Objectives.UseGameObjectObjective
// Assembly: Honorbuddy, Version=2.0.0.5999, Culture=neutral, PublicKeyToken=50a565ab5c01ae50
// MVID: FB7FEB85-27C0-4D17-B8DE-615FDFDA7752
// Assembly location: C:\Users\Texy6\Desktop\Honorbuddy-cleaned.exe

using CommonBehaviors.Actions;
using CommonBehaviors.Decorators;
using Styx;
using Styx.Helpers;
using Styx.Logic.Pathing;
using Styx.Logic.POI;
using Styx.Logic.Profiles.Quest;
using Styx.Logic.Questing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using TreeSharp;

#nullable disable
namespace Bots.Quest.Objectives;

public class UseGameObjectObjective : QuestObjective
{
    private readonly UseObjectObjectiveInfo _useObjectInfo;
    private WoWPoint? _cachedObjectiveLocation;
    private WoWPoint? _cachedClosestLocation;
    private int _locationUpdateCounter;
    private Composite _behaviorTree;
    private WoWGameObject _currentTarget;
    private ulong _standPointFor;
    private WoWPoint _standPoint;

    public UseGameObjectObjective(
        PlayerQuest quest,
        List<WoWQuestStep> questSteps,
        Styx.Logic.Questing.Quest.QuestObjective questObjective,
        List<QuestObjective> prerequisites)
        : base(quest, questSteps, prerequisites)
    {
        this.Objective = questObjective;
        if (this.OverridedQuestInfo != null)
            this._useObjectInfo = this.OverridedQuestInfo.FindUseGameObject((uint)questObjective.ID);
    }

    public Styx.Logic.Questing.Quest.QuestObjective Objective { get; private set; }

    public override bool IsCompleted
    {
        get
        {
            QuestDescriptorData data;
            return this.Quest.GetData(out data) && (int)data.ObjectivesDone[this.Objective.Index] >= this.Objective.Count;
        }
    }

    public override bool CanComplete => this.DonePrerequisites && this.CanCompleteInternal();

    public override WoWPoint GetObjectiveLocation()
    {
        List<WoWGameObject> gameObjects = ObjectManager.GetObjectsOfType<WoWGameObject>()
            .Where(go => (long)go.Entry == (long)this.Objective.ID)
            .ToList();
            
        if (gameObjects.Count <= 0)
        {
            if (!this._cachedObjectiveLocation.HasValue)
            {
                if (this._useObjectInfo != null && this._useObjectInfo.OverridedHotspots != null && this._useObjectInfo.OverridedHotspots.Count > 0)
                {
                    this._cachedObjectiveLocation = new WoWPoint?(this._useObjectInfo.OverridedHotspots.FindClosestTo(ObjectManager.Me.Location));
                }
                else
                {
                    WoWQuestStep closestQuestStep = this.GetClosestQuestStep();
                    Vector3 position = new Vector3((float)closestQuestStep.StepPosition.X, (float)closestQuestStep.StepPosition.Y, 0.0f);
                    if (!MeshHeightHelper.FindMeshHeight(ref position))
                    {
                        Logging.Write("Could not find mesh height for quest {0} on step {1}", this.Quest.Name, closestQuestStep.PoiID);
                        this._cachedObjectiveLocation = new WoWPoint?(WoWPoint.Zero);
                    }
                    else
                    {
                        this._cachedObjectiveLocation = new WoWPoint?(MeshHeightHelper.ToWoWPoint(position));
                    }
                }
            }
            return this._cachedObjectiveLocation.Value;
        }
        
        // Update cached location periodically
        if (!this._cachedClosestLocation.HasValue || ++this._locationUpdateCounter % 25 == 0)
        {
            WoWPoint myLocation = ObjectManager.Me.Location;
            WoWPoint closest = gameObjects[0].Location;
            float closestDistSqr = myLocation.DistanceSqr(closest);
            
            for (int i = 1; i < gameObjects.Count; i++)
            {
                WoWPoint goLocation = gameObjects[i].Location;
                float distSqr = myLocation.DistanceSqr(goLocation);
                if (distSqr < closestDistSqr)
                {
                    closestDistSqr = distSqr;
                    closest = goLocation;
                }
            }
            this._cachedClosestLocation = new WoWPoint?(closest);
            this._locationUpdateCounter = 0;
        }
        return this._cachedClosestLocation.Value;
    }

    public override Composite CreateBranch()
    {
        if (this._behaviorTree == (Composite)null)
        {
            this._behaviorTree = (Composite)new PrioritySelector(new Composite[3]
            {
                (Composite)new Decorator(new CanRunDecoratorDelegate(this.HasTargetGameObject), (Composite)new PrioritySelector(new Composite[2]
                {
                    (Composite)new Decorator(new CanRunDecoratorDelegate(this.IsWithinInteractRange), (Composite)new Sequence(new Composite[4]
                    {
                        (Composite)new ActionSetActivity(ctx => string.Format("Using {0} for quest {1}", this._currentTarget.Name, this.Quest.Name)),
                        (Composite)new TreeSharp.Action(ctx => this.InteractWithGameObject(null)),
                        (Composite)new Wait(10, ctx => !StyxWoW.Me.IsCasting, (Composite)new ActionIdle()),
                        (Composite)new TreeSharp.Action(ctx => this.ClearTarget(null))
                    })),
                    (Composite)new Decorator(new CanRunDecoratorDelegate(this.NeedsToMove), (Composite)new Sequence(new Composite[2]
                    {
                        (Composite)new ActionSetActivity(ctx => string.Format("Moving to game object {0} for quest {1}", this._currentTarget.Name, this.Quest.Name)),
                        (Composite)new TreeSharp.Action(ctx => this.MoveToTarget(null))
                    }))
                })),
                (Composite)new DecoratorIsNotPoiType((IEnumerable<PoiType>)new PoiType[2]
                {
                    PoiType.Loot,
                    PoiType.Skin
                }, (Composite)new TreeSharp.Action(ctx => this.MoveTowardsObjective(null))),
                (Composite)new ActionAlwaysSucceed()
            });
        }
        return this._behaviorTree;
    }

    public override string ToString()
    {
        return string.Format("[UseGameObjectObjective GameObjectId: {0}, Count: {1}]", this.Objective.ID, this.Objective.Count);
    }

    private bool CanCompleteInternal()
    {
        if (this._useObjectInfo != null && this._useObjectInfo.OverridedHotspots != null && this._useObjectInfo.OverridedHotspots.Count > 0)
            return true;
        if (!this.QuestArea.HotspotsCreated)
            this.QuestArea.CreateHotspots();
        return this.QuestArea.Hotspots.Count > 0;
    }

    private bool HasTargetGameObject(object context)
    {
        this._currentTarget = ObjectManager.GetObjectsOfType<WoWGameObject>()
            .FirstOrDefault(go => (long)go.Entry == (long)this.Objective.ID);
        return (WoWObject)this._currentTarget != (WoWObject)null;
    }

    // The client's own interact distance for the object, or standing on the walkable spot closest
    // to it: ropes, levers and banners hang off walls above the navmesh, so nothing gets closer.
    private bool IsWithinInteractRange(object context) =>
        this._currentTarget.DistanceSqr < Math.Max(25.0, this._currentTarget.InteractRangeSqr)
        || StyxWoW.Me.Location.DistanceSqr(this.StandPoint()) < 4f;

    // Walkable mesh point nearest the object in 3D that a mesh path from here actually reaches,
    // sampled on every mesh layer around it: a rope hanging over a staircase resolves to the
    // stairs, not the ground under it or a disconnected ledge.
    private WoWPoint StandPoint()
    {
        if (this._standPointFor == this._currentTarget.Guid)
            return this._standPoint;
        WoWPoint target = this._currentTarget.Location;
        var spots = new List<WoWPoint>();
        for (float radius = 0f; radius <= 6f; radius += 0.75f)
        {
            for (int step = 0; step < (radius == 0f ? 1 : 16); step++)
            {
                float x = target.X + radius * MathF.Cos(step * MathF.PI / 8f);
                float y = target.Y + radius * MathF.Sin(step * MathF.PI / 8f);
                foreach (float z in Navigator.FindHeights(x, y))
                    spots.Add(new WoWPoint(x, y, z));
            }
        }
        WoWPoint me = StyxWoW.Me.Location;
        WoWPoint best = target;
        int tried = 0;
        foreach (WoWPoint spot in spots.OrderBy(s => s.DistanceSqr(target)))
        {
            if (++tried > 40)
                break;
            WoWPoint[] path = Navigator.GeneratePath(me, spot);
            if (path.Length > 0 && path[path.Length - 1].DistanceSqr(spot) < 2.25f)
            {
                best = spot;
                break;
            }
        }
        this._standPointFor = this._currentTarget.Guid;
        this._standPoint = best;
        Logging.WriteDebug("[UseGameObject] {0} at {1}, standing at {2} ({3:F1} yd from it, {4} mesh spots, {5} paths tried)",
            this._currentTarget.Name, target, best, best.Distance(target), spots.Count, tried);
        return best;
    }

    private RunStatus InteractWithGameObject(object context)
    {
        if (StyxWoW.Me.IsMoving)
        {
            WoWMovement.MoveStop();
            StyxWoW.Sleep(250);
            return RunStatus.Running;
        }
        this._currentTarget.Interact();
        StyxWoW.Sleep(1500);
        return RunStatus.Success;
    }

    private RunStatus ClearTarget(object context)
    {
        this._currentTarget = (WoWGameObject)null;
        return RunStatus.Success;
    }

    private bool NeedsToMove(object context) => !this.IsWithinInteractRange(context);

    private RunStatus MoveToTarget(object context)
    {
        Navigator.MoveTo(this.StandPoint());
        return RunStatus.Success;
    }

    private RunStatus MoveTowardsObjective(object context)
    {
        if (this.GetObjectiveLocation() == WoWPoint.Zero)
        {
            Logging.Write("Can not reach step {0} of {1} - blacklisting.", this.QuestSteps.First().PoiID, this.Quest.Name);
            return RunStatus.Success;
        }
        Navigator.MoveTo(this.GetObjectiveLocation());
        return RunStatus.Success;
    }
}
