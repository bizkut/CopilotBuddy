// Decompiled with JetBrains decompiler
// Type: Bots.Quest.QuestOrder.ForcedQuestPickUp
// Assembly: Honorbuddy, Version=2.0.0.5999, Culture=neutral, PublicKeyToken=50a565ab5c01ae50
// MVID: FB7FEB85-27C0-4D17-B8DE-615FDFDA7752
// Assembly location: C:\Users\Texy6\Desktop\Honorbuddy-cleaned.exe

using CommonBehaviors.Actions;
using CommonBehaviors.Decorators;
using Styx;
using Styx.Helpers;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Inventory.Frames;
using Styx.Logic.Inventory.Frames.Gossip;
using Styx.Logic.Inventory.Frames.Quest;
using Styx.Logic.Pathing;
using Styx.Logic.POI;
using Styx.Logic.Profiles.Quest;
using Styx.Logic.Questing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using TreeSharp;

#nullable disable
namespace Bots.Quest.QuestOrder;

public class ForcedQuestPickUp : ForcedBehavior
{
    private static readonly Frame QuestTitleButton = new Frame("QuestTitleButton1");
    private static readonly Frame QuestFrameCompleteQuestButton = new Frame("QuestFrameCompleteQuestButton");
    private int lastShownQuestId = -1;
    private WaitTimer _pickupTimer;
    private bool _giverHostile;

    public ForcedQuestPickUp(
        uint questId,
        string questName,
        uint giverId,
        string giverName,
        WoWPoint giverLocation,
        QuestObjectType? giverType)
    {
        this.QuestId = questId;
        this.QuestName = questName;
        this.GiverId = giverId;
        this.GiverName = giverName;
        this.GiverLocation = giverLocation;
        this.GiverType = giverType;
    }

    public override bool IsDone
    {
        get
        {
            // PickUp is done when:
            // 1) Quest is in completed quests cache (already turned in)
            // 2) Quest is in the quest log (just accepted, ready for objectives)
            try
            {
                // A giver that turned hostile (rival faction such as Oracles/Frenzyheart) never
                // offers the quest; the pickup is skipped instead of timing out and stopping the bot.
                if (this._giverHostile)
                    return true;

                // If quest is in completed cache, skip pickup
                if (ObjectManager.Me.QuestLog.GetCompletedQuests().Contains(this.QuestId))
                    return true;

                // If quest is in log, the PickUp behavior is done
                // This is the key: we just need the quest IN the log, not completed
                if (ObjectManager.Me.QuestLog.ContainsQuest(this.QuestId))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public uint QuestId { get; private set; }

    public string QuestName { get; private set; }

    public uint GiverId { get; private set; }

    public string GiverName { get; private set; }

    public WoWPoint GiverLocation { get; private set; }

    public QuestObjectType? GiverType { get; private set; }

    public override void OnStart()
    {
        if (ObjectManager.Me.QuestLog.GetAllQuests().Count >= 25)
        {
            Logging.Write(Color.Red, "You do not have any space in your quest log.");
            Logging.Write(Color.Red, "CopilotBuddy stopped!");
            TreeRoot.Stop();
        }
        string goalText = this.GetGoalText();
        Logging.Write("[PickUp] {0}", (object)goalText);
        TreeRoot.GoalText = goalText;
        this._pickupTimer = null;
    }

    private string GetGoalText()
    {
        Styx.Logic.Questing.Quest quest = Styx.Logic.Questing.Quest.FromId(this.QuestId);
        QuestObjectType? giverType = this.GiverType;
        if ((giverType.GetValueOrDefault() != QuestObjectType.Item ? 0 : (giverType.HasValue ? 1 : 0)) != 0)
        {
            WoWItem woWitem = ObjectManager.Me.CarriedItems.FirstOrDefault<WoWItem>((Func<WoWItem, bool>)(woWItem_0 => (int)woWItem_0.Entry == (int)this.GiverId));
            if ((WoWObject)woWitem != (WoWObject)null && !string.IsNullOrEmpty(this.QuestName))
                return string.Format("Picking up quest {0} from item {1}", (object)this.QuestName, (object)woWitem.Name);
        }
        if (quest != null)
        {
            string str = string.Format("Picking up {0}", (object)quest.Name);
            return str;
        }
        if (string.IsNullOrEmpty(this.QuestName))
            return string.Format("Picking up quest with ID {0}", (object)this.QuestId);
        string questInfo = string.Format("Picking up {0}", (object)this.QuestName);
        return questInfo;
    }

    private static LocalPlayer Me => ObjectManager.Me;

    protected override Composite CreateBehavior()
    {
        return (Composite)new DecoratorIsNotPoiType((IEnumerable<PoiType>)new PoiType[3]
        {
            PoiType.Harvest,
            PoiType.Skin,
            PoiType.Loot
        }, (Composite)new PrioritySelector(new Composite[5]
        {
            (Composite)new Decorator(new CanRunDecoratorDelegate(this.ShouldSetPoi), (Composite)new ActionSetPoi(true, (RetrieveBotPoiDelegate)(context => new BotPoi(new PickUpNode(this.GiverLocation, this.GiverId, this.GiverName, this.GiverType, this.QuestId, this.QuestName))))),
            (Composite)new Decorator((CanRunDecoratorDelegate)(context =>
            {
                QuestObjectType? giverType = this.GiverType;
                return giverType.GetValueOrDefault() == QuestObjectType.Item && giverType.HasValue;
            }), (Composite)new Sequence((ContextChangeHandler)(context => (object)ObjectManager.GetObjectsOfType<WoWItem>().FirstOrDefault<WoWItem>((Func<WoWItem, bool>)(woWItem_0 => (int)woWItem_0.Entry == (int)this.GiverId))), new Composite[1]
            {
                (Composite)new DecoratorContinue((CanRunDecoratorDelegate)(context => context != null && context is WoWItem), (Composite)new Sequence(new Composite[3]
                {
                    (Composite)new TreeSharp.Action((ActionSucceedDelegate)(context => ((WoWItem)context).UseContainerItem())),
                    (Composite)new WaitContinue(5, new CanRunDecoratorDelegate(this.IsQuestFrameVisible), (Composite)new TreeSharp.Action((ActionSucceedDelegate)(context => QuestFrame.Instance.AcceptQuest()))),
                    (Composite)new WaitContinue(2, (CanRunDecoratorDelegate)(context => false), (Composite)new ActionAlwaysSucceed())
                }))
            })),
            (Composite)new Decorator((CanRunDecoratorDelegate)(context => BotPoi.Current.Type == PoiType.QuestPickUp && BotPoi.Current.Entry == this.GiverId && BotPoi.Current.AsObject is WoWUnit giver && giver.IsHostile), (Composite)new TreeSharp.Action((ActionSucceedDelegate)(context =>
            {
                Logging.Write(Color.Orange, "[PickUp] {0} is hostile to us, skipping {1}", BotPoi.Current.AsObject.Name, this.QuestName);
                this._giverHostile = true;
                BotPoi.Clear("Quest giver hostile");
            }))),
            (Composite)new Decorator((CanRunDecoratorDelegate)(context => !(BotPoi.Current.AsObject != (WoWObject)null) ? (double)ForcedQuestPickUp.Me.Location.DistanceSqr(BotPoi.Current.Location) > 6.25 : !BotPoi.Current.AsObject.WithinInteractRange), (Composite)new ActionMoveToPoi()),
            (Composite)new Decorator((CanRunDecoratorDelegate)(context => BotPoi.Current.AsObject != (WoWObject)null && BotPoi.Current.AsObject.WithinInteractRange), (Composite)new Sequence((ContextChangeHandler)(context => (object)BotPoi.Current.AsObject), new Composite[11]
            {
                (Composite)new ActionMoveStop(),
                (Composite)new TreeSharp.Action((ActionDelegate)(context => this.CloseFrames(context))),
                (Composite)new TreeSharp.Action((ActionDelegate)(context => this.CheckPickupTimeout(context))),
                (Composite)new DecoratorContinue((CanRunDecoratorDelegate)(context => context is WoWUnit), (Composite)new TreeSharp.Action((ActionSucceedDelegate)(context => ((WoWUnit)context).Target()))),
                (Composite)new TreeSharp.Action((ActionSucceedDelegate)(context => ((WoWObject)context).Interact())),
                (Composite)new ActionSleep(1500),
                (Composite)new DecoratorContinue(new CanRunDecoratorDelegate(this.IsGossipOrQuestListVisible), (Composite)new Sequence(new Composite[2]
                {
                    (Composite)new TreeSharp.Action((ActionDelegate)(context => this.SelectAvailableQuest(context))),
                    (Composite)new ActionSleep(200)
                })),
                (Composite)new DecoratorContinue(new CanRunDecoratorDelegate(this.IsQuestFrameVisible), (Composite)new Sequence(new Composite[2]
                {
                    (Composite)new DecoratorContinue(new CanRunDecoratorDelegate(this.IsCompleteQuestButtonVisible), (Composite)new Sequence(new Composite[2]
                    {
                        (Composite)new TreeSharp.Action((ActionDelegate)(context => this.CompleteQuestBeforeAccept(context))),
                        (Composite)new TreeSharp.Action((ActionDelegate)(context => this.CloseFrames(context)))
                    })),
                    (Composite)new TreeSharp.Action((ActionSucceedDelegate)(context => QuestFrame.Instance.AcceptQuest()))
                })),
                (Composite)new TreeSharp.Action((ActionDelegate)(context => this.ClearTarget(context))),
                (Composite)new TreeSharp.Action((ActionDelegate)(context => this.CloseFrames(context))),
                (Composite)new ActionClearPoi("Quest Completed")
            }))
        }));
    }

    private bool ShouldSetPoi(object context)
    {
        BotPoi current = BotPoi.Current;
        return current.Type != PoiType.QuestPickUp || (int)current.Entry != (int)this.GiverId;
    }

    private RunStatus CloseFrames(object context)
    {
        if (!GossipFrame.Instance.IsVisible && !QuestFrame.Instance.IsVisible)
            return RunStatus.Success;
        GossipFrame.Instance.Close();
        QuestFrame.Instance.Close();
        StyxWoW.Sleep(250);
        return RunStatus.Running;
    }

    private RunStatus CheckPickupTimeout(object context)
    {
        if (this._pickupTimer == null)
        {
            this._pickupTimer = new WaitTimer(TimeSpan.FromSeconds(40.0));
            this._pickupTimer.Reset();
        }
        if (this._pickupTimer.IsFinished)
        {
            TreeRoot.Stop(string.Format("Failed to pickup quest at {0} after 20 seconds", ((WoWObject)context).Name));
            return RunStatus.Failure;
        }
        return RunStatus.Success;
    }

    private bool IsGossipOrQuestListVisible(object context)
    {
        return GossipFrame.Instance.IsVisible || ForcedQuestPickUp.QuestTitleButton.IsVisible;
    }

    private RunStatus SelectAvailableQuest(object context)
    {
        if (!GossipFrame.Instance.IsVisible && !ForcedQuestPickUp.QuestTitleButton.IsVisible)
            return RunStatus.Success;

        var gossipQuests = GossipFrame.Instance.AvailableQuests;
        var nativeQuests = QuestFrame.Instance.AvailableQuests;

        int questIndex = -1;
        if (gossipQuests.Count > 0)
        {
            for (int i = 0; i < gossipQuests.Count; i++)
            {
                if ((long)gossipQuests[i].Id == (long)this.QuestId)
                {
                    questIndex = i;
                    Logging.WriteDebug("[QuestPickUp] Matched quest {0} by memory ID at gossip index {1}.", this.QuestId, i);
                    break;
                }
            }

            // HB1 (WotLK source) used Lua-only quest matching — no memory IDs.
            // GetGossipAvailableQuests() returns 3 values per quest in 3.3.5a: title, level, isTrivial.
            if (questIndex == -1)
            {
                List<string> luaDump = Lua.GetReturnValues("return GetGossipAvailableQuests()");
                string matchName = this.QuestName;
                if (string.IsNullOrEmpty(matchName))
                {
                    var quest = Styx.Logic.Questing.Quest.FromId(this.QuestId);
                    if (quest != null)
                        matchName = quest.Name;
                }

                if (luaDump != null && luaDump.Count >= 3 && !string.IsNullOrEmpty(matchName))
                {
                    const int valuesPerQuest = 3;
                    for (int k = 0; k < luaDump.Count / valuesPerQuest; k++)
                    {
                        if (string.Equals(luaDump[k * valuesPerQuest], matchName, StringComparison.OrdinalIgnoreCase))
                        {
                            questIndex = k;
                            Logging.WriteDebug("[QuestPickUp] Matched quest \"{0}\" by Lua name at gossip index {1}.", matchName, k);
                            break;
                        }
                    }
                }

                if (questIndex == -1 && gossipQuests.Count == 1)
                {
                    questIndex = 0;
                    Logging.WriteDebug("[QuestPickUp] Single gossip quest — selecting index 0.");
                }
            }
        }
        else
        {
            if (nativeQuests.Count <= 0)
                return RunStatus.Success;

            for (int j = 0; j < nativeQuests.Count; j++)
            {
                if (nativeQuests[j] == this.QuestId)
                {
                    questIndex = j;
                    break;
                }
            }
        }

        if (questIndex == -1)
        {
            Logging.WriteDebug("[QuestPickUp] Quest \"{0}\" (id={1}) not found — gossip={2}, native={3}.",
                this.QuestName, this.QuestId, gossipQuests.Count, nativeQuests.Count);
            return RunStatus.Failure;
        }

        GossipFrame.Instance.SelectAvailableQuest(questIndex);
        StyxWoW.Sleep(500);
        return RunStatus.Running;
    }

    private bool IsQuestFrameVisible(object context)
    {
        return QuestFrame.Instance.IsVisible;
    }

    private bool IsCompleteQuestButtonVisible(object context)
    {
        return ForcedQuestPickUp.QuestFrameCompleteQuestButton.IsVisible;
    }

    private RunStatus CompleteQuestBeforeAccept(object context)
    {
        if (this.lastShownQuestId == -1)
            this.lastShownQuestId = (int)QuestFrame.Instance.CurrentShownQuestId;
        if (QuestFrame.Instance.IsVisible && (long)this.lastShownQuestId == (long)QuestFrame.Instance.CurrentShownQuestId)
        {
            QuestFrame.Instance.CompleteQuest();
            StyxWoW.Sleep(500);
            return RunStatus.Running;
        }
        this.lastShownQuestId = -1;
        return RunStatus.Success;
    }

    private RunStatus ClearTarget(object context)
    {
        if (!ObjectManager.Me.GotTarget)
            return RunStatus.Success;
        ObjectManager.Me.ClearTarget();
        StyxWoW.Sleep(300);
        return RunStatus.Running;
    }

    public override string ToString()
    {
        return string.Format("[ForcedQuestPickUp QuestId: {0}, QuestName: {1}]", (object)this.QuestId, (object)this.QuestName);
    }
}
