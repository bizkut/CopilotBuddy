#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.Logic.Pathing;
using Styx.Logic.Profiles;
using Styx.Logic.POI;
using Styx.WoWInternals;
using Styx.WoWInternals.World;
using GreenMagic;
using Styx.WoWInternals.WoWObjects;

namespace Styx.Logic
{
    public class Targeting
    {
        private static readonly List<string> _blacklistedMobNames;
        private static Targeting _instance;
        // backing fields for events (originally obfuscated names in HB)
        private TargetListUpdateFinishedDelegate _targetListUpdateFinishedHandlers;
        private IncludeTargetsFilterDelegate _includeTargetsFilterHandlers;
        private RemoveTargetsFilterDelegate _removeTargetsFilterHandlers;
        private WeighTargetsDelegate _weighTargetsFilterHandlers;
        private static readonly HashSet<uint> _blacklistedMobIds;
        private static readonly HashSet<uint> hashSet_0;
        // from original HB: special quest mobs used in include filter
        private static readonly HashSet<uint> _specialQuestMobIds = new HashSet<uint>
        {
            28577U, 28557U, 28576U, 28560U, 28559U, 28941U, 28942U
        };
        private bool _includeWorldPlayers;
        private bool _includeElites;
        private bool _displayTargetingExceptions;
        private List<WoWObject> _objectList;
        private int _maxTargets;
        private static Converter<WoWObject, WoWUnit> _objectToUnitConverter;
        private static Func<WoWObject, bool> _unitOrPlayerPredicate;
        private static Func<WoWObject, bool> _unitPredicate;
        private static Func<WoWObject, bool> _unitNotPlayerPredicate;
        private static Func<WoWObject, TargetPriority> _targetPrioritySelector;
        private static Func<TargetPriority, double> _getScoreFunc;
        private static Func<TargetPriority, WoWObject> _targetToObjectSelector;

        public Targeting()
        {
            this.DisplayTargetingExceptions = true;
            this.ObjectList = new List<WoWObject>();
            this.MaxTargets = 5;
            this.InitializeFilters();
        }

        static Targeting()
        {
            // Once for every Targeting flavour (loot, heal, dungeon, BG): refused attack targets.
            BotEvents.OnBotStart += args => Lua.Events.AttachEvent("UI_ERROR_MESSAGE", OnUiErrorMessage);
            BotEvents.OnBotStop += args =>
            {
                Lua.Events.DetachEvent("UI_ERROR_MESSAGE", OnUiErrorMessage);
                _refusedTargets.Clear();
            };
            Targeting._blacklistedMobNames = new List<string>();
            HashSet<uint> hashSet = new HashSet<uint>();
            hashSet.Add(28093U);
            hashSet.Add(22979U);
            hashSet.Add(24196U);
            hashSet.Add(13358U);
            hashSet.Add(23837U);
            hashSet.Add(13358U);
            hashSet.Add(9157U);
            hashSet.Add(17407U);
            hashSet.Add(17378U);
            hashSet.Add(17408U);
            hashSet.Add(24222U);
            hashSet.Add(32544U);
            hashSet.Add(32522U);
            hashSet.Add(24879U);
            hashSet.Add(25040U);
            // missing ids from original
            hashSet.Add(23537U);
            hashSet.Add(18729U);
            Targeting._blacklistedMobIds = hashSet;
            Targeting.hashSet_0 = hashSet;
        }

        public static double PullDistance
        {
            get
            {
                // BUG-26 fix: Check CR override first, fall back to settings
                double? crOverride = RoutineManager.Current?.PullDistance;
                if (crOverride.HasValue)
                    return crOverride.Value;
                return (double)LevelbotSettings.Instance.PullDistance;
            }
        }

        public static double PullDistanceSqr
        {
            get
            {
                double pullDistance = PullDistance;
                return pullDistance * pullDistance;
            }
        }

        public static int GetAggroOnMeWithin(WoWPoint position, float range)
        {
            float rangeSqr = range * range;
            // use cached units to reduce memory queries (400ms timeout in ObjectManager)
            return ObjectManager.CachedUnits
                .Count(u => u.Location.DistanceSqr(position) < rangeSqr
                            && u.IsAlive && u.Attackable && u.IsHostile
                            && (u.IsTargetingMeOrPet || u.IsTargetingAnyMinion || u.TappedByAllThreatLists));
        }

        public static int GetAggroWithin(WoWPoint position, float range)
        {
            float rangeSqr = range * range;
            return ObjectManager.CachedUnits
                .Where(u => u.IsAlive && u.Attackable && u.IsHostile && u.Location.DistanceSqr(position) <= rangeSqr)
                .Count();
        }

        public static double CollectionRange
        {
            get
            {
                var area = StyxWoW.AreaManager.CurrentGrindArea;
                if (area != null)
                {
                    double? maxDistance = area.MaxDistance;
                    if (maxDistance != null && maxDistance.Value > 10.0)
                    {
                        return maxDistance.Value;
                    }
                }
                return 100.0;
            }
        }

        public bool IncludeWorldPlayers
        {
            get
            {
                if (Battlegrounds.IsInsideBattleground)
                {
                    return true;
                }
                return this._includeWorldPlayers;
            }
            set
            {
                this._includeWorldPlayers = value;
            }
        }

        public bool IncludeElites
        {
            get { return this._includeElites; }
            set { this._includeElites = value; }
        }

        public bool KillBetweenHotspots
        {
            get
            {
                return StyxSettings.Instance.KillBetweenHotspots;
            }
        }

        public bool DisplayTargetingExceptions
        {
            get { return this._displayTargetingExceptions; }
            set { this._displayTargetingExceptions = value; }
        }

        /// <summary>
        /// HB 4.3.4 compatibility: gates event subscription.
        /// Not enforced in CB (thread-safe Interlocked pattern used instead).
        /// </summary>
        protected bool AllowEvents { get; set; } = true;

        public static Targeting Instance
        {
            get
            {
                Targeting targeting;
                if ((targeting = Targeting._instance) == null)
                {
                    targeting = (Targeting._instance = new Targeting());
                }
                return targeting;
            }
            set
            {
                Targeting._instance = value;
            }
        }

        public WoWUnit FirstUnit
        {
            get
            {
                if (this.ObjectList.Count == 0)
                {
                    return null;
                }
                return (this.ObjectList[0] as WoWUnit);
            }
        }

        public List<WoWUnit> TargetList
        {
            get
            {
                List<WoWObject> objectList = this.ObjectList;
                if (Targeting._objectToUnitConverter == null)
                {
                    Targeting._objectToUnitConverter = new Converter<WoWObject, WoWUnit>(ToWoWUnit);
                }
                return objectList.ConvertAll<WoWUnit>(Targeting._objectToUnitConverter);
            }
        }

        protected List<WoWObject> ObjectList
        {
            get { return this._objectList; }
            private set { this._objectList = value; }
        }

        public int MaxTargets
        {
            get { return this._maxTargets; }
            set { this._maxTargets = value; }
        }

        public event TargetListUpdateFinishedDelegate OnTargetListUpdateFinished
        {
            add
            {
                TargetListUpdateFinishedDelegate targetListUpdateFinishedDelegate = this._targetListUpdateFinishedHandlers;
                TargetListUpdateFinishedDelegate targetListUpdateFinishedDelegate2;
                do
                {
                    targetListUpdateFinishedDelegate2 = targetListUpdateFinishedDelegate;
                    TargetListUpdateFinishedDelegate targetListUpdateFinishedDelegate3 = (TargetListUpdateFinishedDelegate)Delegate.Combine(targetListUpdateFinishedDelegate2, value);
                    targetListUpdateFinishedDelegate = Interlocked.CompareExchange<TargetListUpdateFinishedDelegate>(ref this._targetListUpdateFinishedHandlers, targetListUpdateFinishedDelegate3, targetListUpdateFinishedDelegate2);
                }
                while (targetListUpdateFinishedDelegate != targetListUpdateFinishedDelegate2);
            }
            remove
            {
                TargetListUpdateFinishedDelegate targetListUpdateFinishedDelegate = this._targetListUpdateFinishedHandlers;
                TargetListUpdateFinishedDelegate targetListUpdateFinishedDelegate2;
                do
                {
                    targetListUpdateFinishedDelegate2 = targetListUpdateFinishedDelegate;
                    TargetListUpdateFinishedDelegate targetListUpdateFinishedDelegate3 = (TargetListUpdateFinishedDelegate)Delegate.Remove(targetListUpdateFinishedDelegate2, value);
                    targetListUpdateFinishedDelegate = Interlocked.CompareExchange<TargetListUpdateFinishedDelegate>(ref this._targetListUpdateFinishedHandlers, targetListUpdateFinishedDelegate3, targetListUpdateFinishedDelegate2);
                }
                while (targetListUpdateFinishedDelegate != targetListUpdateFinishedDelegate2);
            }
        }

        private void InitializeFilters()
        {
            this.IncludeTargetsFilter += this.DefaultIncludeTargetsFilter;
            this.RemoveTargetsFilter += this.DefaultRemoveTargetsFilter;
            this.WeighTargetsFilter += this.DefaultTargetWeight;
        }

        // Units the server refused as attack targets, until the time shown. The client can disagree:
        // with a peace-forced faction we are Hated with (Valiance Expedition after a GM change, the
        // losing Sholazar side, Oracles or Frenzyheart) UnitCanAttack says yes and the NPC attacks us,
        // but the server never puts us at war with it and rejects every attack as an invalid target.
        private static readonly Dictionary<ulong, DateTime> _refusedTargets = new Dictionary<ulong, DateTime>();
        private static string[] _refusalMessages;

        private static void OnUiErrorMessage(object sender, LuaEventArgs e)
        {
            if (e.Args == null || e.Args.Length == 0)
                return;
            _refusalMessages ??= new[]
            {
                Lua.GetReturnVal<string>("return SPELL_FAILED_BAD_TARGETS", 0U),
                Lua.GetReturnVal<string>("return ERR_INVALID_ATTACK_TARGET", 0U),
            };
            string message = Convert.ToString(e.Args[0]);
            if (string.IsNullOrEmpty(message) || Array.IndexOf(_refusalMessages, message) < 0)
                return;
            // "Invalid target" also answers a cast on a mob that just died or a wrong spell target; only
            // a live NPC red to us by reputation alone can be the peace-forced refusal described above.
            WoWUnit target = StyxWoW.Me?.CurrentTarget;
            if (target == null || target.IsPlayer || target.Dead || !IsHostileByReputationOnly(target) || !target.IsHostile)
                return;
            _refusedTargets[target.Guid] = DateTime.UtcNow.AddMinutes(3);
            Logging.Write(System.Drawing.Color.Orange, "[Targeting] The server refuses {0} as a target ({1}), ignoring it for 3 minutes",
                target.Name, message);
            StyxWoW.Me.ClearTarget();
        }

        private static bool IsRefusedTarget(WoWUnit unit)
        {
            if (!_refusedTargets.TryGetValue(unit.Guid, out DateTime until))
                return false;
            if (until > DateTime.UtcNow)
                return true;
            _refusedTargets.Remove(unit.Guid);
            return false;
        }

        private static readonly Dictionary<(uint unit, uint me), bool> _reputationOnlyHostility = new Dictionary<(uint, uint), bool>();

        /// <summary>
        /// The unit is red to us only because of our standing: its faction tracks reputation and its
        /// faction template alone is not hostile to ours (Valiance Expedition for an Alliance player,
        /// the Sholazar side we did not pick). The server attacks us on our rank but lets us attack
        /// back only at war, which peace-forced factions never are, so such units are never pulled.
        /// </summary>
        private static bool IsHostileByReputationOnly(WoWUnit unit)
        {
            var key = (unit.FactionId, StyxWoW.Me.FactionId);
            if (!_reputationOnlyHostility.TryGetValue(key, out bool repOnly))
            {
                var theirs = new WoWFaction(key.Item1, true);
                var mine = new WoWFaction(key.Item2, true);
                repOnly = theirs.Record.RepGainId >= 0
                          && WoWFaction.CompareFactions(theirs, mine) != WoWUnitReaction.Hostile;
                _reputationOnlyHostility[key] = repOnly;
            }
            return repOnly;
        }

        public event IncludeTargetsFilterDelegate IncludeTargetsFilter
        {
            add
            {
                IncludeTargetsFilterDelegate includeTargetsFilterDelegate = this._includeTargetsFilterHandlers;
                IncludeTargetsFilterDelegate includeTargetsFilterDelegate2;
                do
                {
                    includeTargetsFilterDelegate2 = includeTargetsFilterDelegate;
                    IncludeTargetsFilterDelegate includeTargetsFilterDelegate3 = (IncludeTargetsFilterDelegate)Delegate.Combine(includeTargetsFilterDelegate2, value);
                    includeTargetsFilterDelegate = Interlocked.CompareExchange<IncludeTargetsFilterDelegate>(ref this._includeTargetsFilterHandlers, includeTargetsFilterDelegate3, includeTargetsFilterDelegate2);
                }
                while (includeTargetsFilterDelegate != includeTargetsFilterDelegate2);
            }
            remove
            {
                IncludeTargetsFilterDelegate includeTargetsFilterDelegate = this._includeTargetsFilterHandlers;
                IncludeTargetsFilterDelegate includeTargetsFilterDelegate2;
                do
                {
                    includeTargetsFilterDelegate2 = includeTargetsFilterDelegate;
                    IncludeTargetsFilterDelegate includeTargetsFilterDelegate3 = (IncludeTargetsFilterDelegate)Delegate.Remove(includeTargetsFilterDelegate2, value);
                    includeTargetsFilterDelegate = Interlocked.CompareExchange<IncludeTargetsFilterDelegate>(ref this._includeTargetsFilterHandlers, includeTargetsFilterDelegate3, includeTargetsFilterDelegate2);
                }
                while (includeTargetsFilterDelegate != includeTargetsFilterDelegate2);
            }
        }

        public event RemoveTargetsFilterDelegate RemoveTargetsFilter
        {
            add
            {
                RemoveTargetsFilterDelegate removeTargetsFilterDelegate = this._removeTargetsFilterHandlers;
                RemoveTargetsFilterDelegate removeTargetsFilterDelegate2;
                do
                {
                    removeTargetsFilterDelegate2 = removeTargetsFilterDelegate;
                    RemoveTargetsFilterDelegate removeTargetsFilterDelegate3 = (RemoveTargetsFilterDelegate)Delegate.Combine(removeTargetsFilterDelegate2, value);
                    removeTargetsFilterDelegate = Interlocked.CompareExchange<RemoveTargetsFilterDelegate>(ref this._removeTargetsFilterHandlers, removeTargetsFilterDelegate3, removeTargetsFilterDelegate2);
                }
                while (removeTargetsFilterDelegate != removeTargetsFilterDelegate2);
            }
            remove
            {
                RemoveTargetsFilterDelegate removeTargetsFilterDelegate = this._removeTargetsFilterHandlers;
                RemoveTargetsFilterDelegate removeTargetsFilterDelegate2;
                do
                {
                    removeTargetsFilterDelegate2 = removeTargetsFilterDelegate;
                    RemoveTargetsFilterDelegate removeTargetsFilterDelegate3 = (RemoveTargetsFilterDelegate)Delegate.Remove(removeTargetsFilterDelegate2, value);
                    removeTargetsFilterDelegate = Interlocked.CompareExchange<RemoveTargetsFilterDelegate>(ref this._removeTargetsFilterHandlers, removeTargetsFilterDelegate3, removeTargetsFilterDelegate2);
                }
                while (removeTargetsFilterDelegate != removeTargetsFilterDelegate2);
            }
        }

        public event WeighTargetsDelegate WeighTargetsFilter
        {
            add
            {
                WeighTargetsDelegate weighTargetsDelegate = this._weighTargetsFilterHandlers;
                WeighTargetsDelegate weighTargetsDelegate2;
                do
                {
                    weighTargetsDelegate2 = weighTargetsDelegate;
                    WeighTargetsDelegate weighTargetsDelegate3 = (WeighTargetsDelegate)Delegate.Combine(weighTargetsDelegate2, value);
                    weighTargetsDelegate = Interlocked.CompareExchange<WeighTargetsDelegate>(ref this._weighTargetsFilterHandlers, weighTargetsDelegate3, weighTargetsDelegate2);
                }
                while (weighTargetsDelegate != weighTargetsDelegate2);
            }
            remove
            {
                WeighTargetsDelegate weighTargetsDelegate = this._weighTargetsFilterHandlers;
                WeighTargetsDelegate weighTargetsDelegate2;
                do
                {
                    weighTargetsDelegate2 = weighTargetsDelegate;
                    WeighTargetsDelegate weighTargetsDelegate3 = (WeighTargetsDelegate)Delegate.Remove(weighTargetsDelegate2, value);
                    weighTargetsDelegate = Interlocked.CompareExchange<WeighTargetsDelegate>(ref this._weighTargetsFilterHandlers, weighTargetsDelegate3, weighTargetsDelegate2);
                }
                while (weighTargetsDelegate != weighTargetsDelegate2);
            }
        }

        public void Clear()
        {
            if (this.ObjectList != null)
            {
                this.ObjectList.Clear();
            }
        }

        protected virtual List<WoWObject> GetInitialObjectList()
        {
            if (Battlegrounds.IsInsideBattleground)
            {
                IEnumerable<WoWObject> objectList = ObjectManager.ObjectList;
                if (Targeting._unitOrPlayerPredicate == null)
                {
                    Targeting._unitOrPlayerPredicate = new Func<WoWObject, bool>(IsPlayer);
                }
                return objectList.Where(Targeting._unitOrPlayerPredicate).ToList<WoWObject>();
            }
            else if (!this.IncludeWorldPlayers)
            {
                IEnumerable<WoWObject> objectList2 = ObjectManager.ObjectList;
                if (Targeting._unitNotPlayerPredicate == null)
                {
                    Targeting._unitNotPlayerPredicate = new Func<WoWObject, bool>(IsUnitNotPlayer);
                }
                return objectList2.Where(Targeting._unitNotPlayerPredicate).ToList<WoWObject>();
            }
            else
            {
                IEnumerable<WoWObject> objectList3 = ObjectManager.ObjectList;
                if (Targeting._unitPredicate == null)
                {
                    Targeting._unitPredicate = new Func<WoWObject, bool>(IsUnit);
                }
                return objectList3.Where(Targeting._unitPredicate).ToList<WoWObject>();
            }
        }

        public virtual void Pulse()
        {
            try
            {
                using (StyxWoW.Memory.AcquireFrame())
                {
                    if (!(StyxWoW.Me == null) && StyxWoW.IsInGame)
                    {
                        List<WoWObject> initialObjectList = this.GetInitialObjectList();
                        Delegate e = this._removeTargetsFilterHandlers;
                        object[] array = new object[] { initialObjectList };
                        this.InvokeFilterDelegate(e, array);
                        HashSet<WoWObject> hashSet = new HashSet<WoWObject>();
                        Delegate e2 = this._includeTargetsFilterHandlers;
                        object[] array2 = new object[] { initialObjectList, hashSet };
                        this.InvokeFilterDelegate(e2, array2);
                        IEnumerable<WoWObject> source = hashSet;
                        if (Targeting._targetPrioritySelector == null)
                        {
                            Targeting._targetPrioritySelector = new Func<WoWObject, TargetPriority>(CreateTargetPriority);
                        }
                        List<TargetPriority> list = source.Select(Targeting._targetPrioritySelector).ToList<TargetPriority>();
                        Delegate e3 = this._weighTargetsFilterHandlers;
                        object[] array3 = new object[] { list };
                        this.InvokeFilterDelegate(e3, array3);
                        IEnumerable<TargetPriority> source2 = list;
                        if (Targeting._getScoreFunc == null)
                        {
                            Targeting._getScoreFunc = new Func<TargetPriority, double>(GetScore);
                        }
                        list = source2.OrderByDescending(Targeting._getScoreFunc).Take(this.MaxTargets).ToList<TargetPriority>();
                        IEnumerable<TargetPriority> source3 = list;
                        if (Targeting._targetToObjectSelector == null)
                        {
                            Targeting._targetToObjectSelector = new Func<TargetPriority, WoWObject>(GetObject);
                        }
                        this.ObjectList = source3.Select(Targeting._targetToObjectSelector).ToList<WoWObject>();

                        Targeting._blacklistedMobNames.Clear();
                        foreach (TargetPriority targetPriority in list)
                        {
                            Targeting._blacklistedMobNames.Add(string.Format("{0} Dist: {1}", targetPriority.Object.Name, Math.Ceiling(targetPriority.Object.Distance)));
                        }
                        if (this._targetListUpdateFinishedHandlers != null)
                        {
                            this._targetListUpdateFinishedHandlers(Targeting._blacklistedMobNames);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (this.DisplayTargetingExceptions)
                {
                    Logging.WriteException(ex);
                }
            }
        }

        private void InvokeFilterDelegate(Delegate e, params object[] args)
        {
            if (e != null)
            {
                foreach (Delegate @delegate in e.GetInvocationList())
                {
                    try
                    {
                        @delegate.DynamicInvoke(args);
                    }
                    catch (Exception ex)
                    {
                        if (this.DisplayTargetingExceptions)
                        {
                            Logging.WriteException(ex);
                        }
                    }
                }
            }
        }

        protected virtual void DefaultRemoveTargetsFilter(List<WoWObject> units)
        {
            using (StyxWoW.Memory.AcquireFrame())
            {
                Blacklist.Flush();

                bool flag = StyxWoW.Me.IsInParty || StyxWoW.Me.IsInRaid;
                double num = Targeting.CollectionRange * Targeting.CollectionRange;
                Profile currentProfile = ProfileManager.CurrentProfile;

                HashSet<ulong> hashSet = new HashSet<ulong>();
                HashSet<ulong> hashSet2 = new HashSet<ulong>();
                if (StyxWoW.Me.IsInRaid)
                {
                    using (List<WoWPlayer>.Enumerator enumerator = StyxWoW.Me.RaidMembers.GetEnumerator())
                    {
                        while (enumerator.MoveNext())
                        {
                            WoWPlayer woWPlayer = enumerator.Current;
                            hashSet2.Add(woWPlayer.Guid);
                            foreach (WoWUnit woWUnit in woWPlayer.Minions)
                            {
                                hashSet.Add(woWUnit.Guid);
                            }
                        }
                    }
                }

                if (StyxWoW.Me.IsInParty)
                {
                    foreach (WoWPlayer woWPlayer2 in StyxWoW.Me.PartyMembers)
                    {
                        hashSet2.Add(woWPlayer2.Guid);
                        foreach (WoWUnit woWUnit2 in woWPlayer2.Minions)
                        {
                            hashSet.Add(woWUnit2.Guid);
                        }
                    }
                }

                bool isInsideBattleground = Battlegrounds.IsInsideBattleground;

                for (int i = units.Count - 1; i >= 0; i--)
                {
                    WoWObject woWObject = units[i];
                    if (!(woWObject == null) && woWObject is WoWUnit)
                    {
                        bool isPlayer = woWObject is WoWPlayer;
                        WoWUnit woWUnit3 = (WoWUnit)woWObject;
                        // Checked before the "it is attacking us, keep it" rules below: a unit the
                        // server will not let us attack stays refused even while it hits us, and a unit
                        // red to us only by reputation is answered when it attacks, never pulled.
                        // Cheap descriptor reads and the cached faction-table lookup come first; the
                        // native reaction call behind IsHostile only runs for reputation-tracked units.
                        if (IsRefusedTarget(woWUnit3)
                            || (!isPlayer && !woWUnit3.Aggro && !woWUnit3.PetAggro
                                && !woWUnit3.IsTargetingMeOrPet && !woWUnit3.IsTargetingAnyMinion
                                && IsHostileByReputationOnly(woWUnit3) && woWUnit3.IsHostile))
                        {
                            units.RemoveAt(i);
                            continue;
                        }
                        bool combat = woWUnit3.Combat;

                        if (isPlayer || !combat || !flag || (!hashSet2.Contains(woWUnit3.CurrentTargetGuid) && !hashSet.Contains(woWUnit3.Guid)))
                        {
                            bool isAlive = woWUnit3.IsAlive;
                            bool petAggro = woWUnit3.PetAggro;

                            if (isPlayer || !combat || !isAlive || (!petAggro && !woWUnit3.Aggro && !woWUnit3.IsTargetingMeOrPet && !woWUnit3.IsTargetingAnyMinion))
                            {
                                double distanceSqr = woWUnit3.DistanceSqr;
                                bool isFriendly = woWUnit3.IsFriendly;

                                if (!isPlayer || !combat || !isAlive || distanceSqr >= num || isFriendly || (!woWUnit3.IsTargetingMeOrPet && !woWUnit3.IsTargetingAnyMinion))
                                {
                                    uint entry = woWUnit3.Entry;
                                    WoWPoint location = woWUnit3.Location;
                                    bool removed = Targeting.hashSet_0.Contains(entry) || Blacklist.Contains(woWUnit3.Guid, false) || woWUnit3.Dead || distanceSqr > num || woWUnit3.OnTaxi || woWUnit3.IsFlightMaster || (woWUnit3.IsFlying && !isInsideBattleground) || (woWUnit3.TaggedByOther && !flag);

                                    if (!removed && currentProfile != null)
                                    {
                                        removed = currentProfile.AvoidMobs.Contains(entry) || currentProfile.AvoidMobs.Contains(woWUnit3.Name);
                                    }

                                    if (!removed)
                                    {
                                        bool tooNearBlackspot = Targeting.IsTooNearBlackspot(currentProfile != null ? currentProfile.Blackspots : null, location);
                                        if (tooNearBlackspot || (currentProfile != null && IsNotWithinHotspotRange(location, false)))
                                        {
                                            removed = true;
                                        }

                                        if (!removed && (woWUnit3.IsCritter || woWUnit3.IsNonCombatPet || !woWUnit3.Attackable || isFriendly))
                                        {
                                            removed = true;
                                        }
                                    }

                                    if (removed)
                                    {
                                        units.RemoveAt(i);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        units.RemoveAt(i);
                    }
                }
            }
        }

        protected virtual void DefaultIncludeTargetsFilter(List<WoWObject> incomingUnits, HashSet<WoWObject> outgoingUnits)
        {
            using (StyxWoW.Memory.AcquireFrame())
            {
                bool isInCombat = StyxWoW.Me.Combat
                    || StyxWoW.Me.IsBeingAttacked
                    || StyxWoW.Me.Minions.Any(m => m.Combat);

                foreach (WoWObject woWObject in incomingUnits)
                {
                    bool isInsideBattleground = Battlegrounds.IsInsideBattleground;
                    WoWUnit unit = woWObject.ToUnit();

                    if (isInCombat)
                    {
                        if (!isInsideBattleground && woWObject is WoWPlayer)
                        {
                            WoWPlayer woWPlayer = woWObject.ToPlayer();
                            if (woWPlayer != null
                                && (woWPlayer.Aggro || woWPlayer.PetAggro || woWPlayer.IsTargetingAnyMinion || woWPlayer.IsTargetingMeOrPet)
                                && woWPlayer.Combat
                                && !woWPlayer.IsFriendly)
                            {
                                outgoingUnits.Add(woWPlayer);
                            }
                        }
                        else
                        {
                            if (_specialQuestMobIds.Contains(unit.Entry))
                            {
                                if (unit.TaggedByMe && unit.HealthPercent < 99.0)
                                {
                                    outgoingUnits.Add(unit);
                                    continue;
                                }
                                if ((StyxWoW.Me.QuestLog.ContainsQuest(12678U) || StyxWoW.Me.QuestLog.ContainsQuest(12722U))
                                    && unit.DistanceSqr < 400.0
                                    && unit.NpcEmoteState == 431U)
                                {
                                    outgoingUnits.Add(unit);
                                    continue;
                                }
                            }

                            if (unit.Aggro || (unit.Fleeing && unit.TaggedByMe) || unit.IsTargetingAnyMinion || unit.IsTargetingMeOrPet)
                            {
                                outgoingUnits.Add(unit);
                            }
                        }
                    }
                }

                if (!StyxWoW.Me.CurrentMap.IsDungeon && !StyxWoW.Me.CurrentMap.IsRaid)
                {
                    WoWObject asObject;
                    if (BotPoi.Current.Type == PoiType.Kill
                        && (asObject = BotPoi.Current.AsObject) != null
                        && asObject.IsValid
                        && !outgoingUnits.Contains(asObject))
                    {
                        outgoingUnits.Add(asObject);
                    }
                }
            }
        }

        /// <summary>
        /// Weights each unit in the target list. Matches HB 4.3.4 structure.
        /// </summary>
        protected virtual void DefaultTargetWeight(List<TargetPriority> units)
        {
            using (StyxWoW.Memory.AcquireFrame())
            {
                bool inCombat = StyxWoW.Me.PetInCombat || StyxWoW.Me.Combat;
                LocalPlayer me = StyxWoW.Me;
                Profile currentProfile = ProfileManager.CurrentProfile;

                bool[] losResults = null;
                if (!Battlegrounds.IsInsideBattleground)
                {
                    WorldLine[] traceLines = new WorldLine[units.Count];
                    WoWPoint traceLinePos = StyxWoW.Me.GetTraceLinePos();
                    for (int i = units.Count - 1; i >= 0; i--)
                    {
                        traceLines[i] = new WorldLine(traceLinePos, units[i].Object.ToUnit().GetTraceLinePos());
                    }
                    GameWorld.MassTraceLine(traceLines, GameWorld.CGWorldFrameHitFlags.HitTestLOS, out losResults);
                }

                for (int j = units.Count - 1; j >= 0; j--)
                {
                    WoWUnit unit = units[j].Object.ToUnit();
                    double num = 200.0 - 2.0 * unit.Distance;
                    double distance = unit.Distance;
                    ulong guid = unit.Guid;
                    ulong currentTargetGuid = unit.CurrentTargetGuid;

                    if (!inCombat)
                    {
                        if (currentProfile != null && !currentProfile.TargetElites && unit.Elite)
                        {
                            if (distance > 20.0)
                            {
                                units.RemoveAt(j);
                                continue;
                            }
                            num -= 1000.0;
                        }

                        if (Blacklist.Contains(guid))
                        {
                            if (distance > 20.0)
                            {
                                units.RemoveAt(j);
                                continue;
                            }
                            num -= 1000.0;
                        }

                        if (unit.MyReaction <= WoWUnitReaction.Neutral && distance < 30.0)
                        {
                            num += (30.0 - distance) * (double)(WoWUnitReaction.Friendly - unit.MyReaction);
                        }

                        if (currentTargetGuid == guid || (BotPoi.Current.Type == PoiType.Kill && BotPoi.Current.Guid == unit.Guid))
                        {
                            num += 150.0;
                        }

                        if (unit.IsPet)
                        {
                            num -= 50.0;
                        }

                        float myAggroRange = unit.MyAggroRange;
                        if (myAggroRange != 0f && distance < (double)(myAggroRange + 5f))
                        {
                            num += 100.0;
                        }

                        if (losResults != null && !losResults[j])
                        {
                            num += 25.0;
                        }
                        else
                        {
                            num -= 25.0;
                        }
                    }
                    else
                    {
                        if (me.GotTarget && me.CurrentTarget.MaxMana > 1U && me.CurrentTarget.ManaPercent > 0.0)
                        {
                            num += unit.ManaPercent;
                        }
                        num -= unit.HealthPercent;
                        if (!unit.CanSelect)
                        {
                            num -= 1000.0;
                        }
                    }

                    units[j].Score += num;
                }
            }
        }

        /// <summary>
        /// Indicates that a point lies outside the current hotspot collection range.
        /// </summary>
        public bool IsNotWithinHotspotRange(WoWPoint point, bool force = false)
        {
            if ((KillBetweenHotspots || Battlegrounds.IsInsideBattleground || StyxWoW.Me.IsInInstance) && !force)
                return false;

            try
            {
                var ga = ProfileManager.CurrentProfile?.GrindArea;
                if (ga?.CurrentHotSpot != null)
                {
                    float distance2D = ga.CurrentHotSpot.Position.Distance2D(point);
                    return distance2D >= CollectionRange;
                }
            }
            catch { }
            return false;
        }

        private static WoWUnit ToWoWUnit(WoWObject o)
        {
            return o.ToUnit();
        }

        private static bool IsPlayer(WoWObject o)
        {
            return (o is WoWPlayer);
        }

        private static bool IsUnit(WoWObject o)
        {
            return (o is WoWUnit);
        }

        private static bool IsUnitOrPlayer(WoWObject o)
        {
            return (o is WoWUnit) || (o is WoWPlayer);
        }

        private static bool IsUnitNotPlayer(WoWObject o)
        {
            if (o is WoWUnit)
            {
                return !(o is WoWPlayer);
            }
            return false;
        }

        private static TargetPriority CreateTargetPriority(WoWObject unit)
        {
            TargetPriority targetPriority = new TargetPriority();
            targetPriority.Object = unit;
            targetPriority.Score = 0.0;
            return targetPriority;
        }

        private static double GetScore(TargetPriority p)
        {
            return p.Score;
        }

        private static WoWObject GetObject(TargetPriority p)
        {
            return p.Object;
        }

        #region Blackspot Support

        /// <summary>
        /// Checks if a point is within any blackspot.
        /// HB 4.3.4 compatible - used by Quest Behaviors.
        /// </summary>
        /// <param name="blackspots">Collection of blackspots to check.</param>
        /// <param name="point">The point to check.</param>
        /// <returns>True if the point is within any blackspot.</returns>
        public static bool IsTooNearBlackspot(IEnumerable<Blackspot> blackspots, WoWPoint point)
        {
            if (blackspots == null)
                return false;

            foreach (var spot in blackspots)
            {
                float dx = point.X - spot.Location.X;
                float dy = point.Y - spot.Location.Y;
                float dz = point.Z - spot.Location.Z;

                // Check horizontal distance (within radius)
                if (dx * dx + dy * dy <= spot.Radius * spot.Radius)
                {
                    // Check vertical distance (within height)
                    if (Math.Abs(dz) <= spot.Height)
                        return true;
                }
            }
            return false;
        }

        #endregion

        public sealed class TargetPriority
        {
            public WoWObject Object;
            public double Score;
        }
    }
}
