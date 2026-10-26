// Decompiled with JetBrains decompiler
// Type: Styx.Logic.MountHelper
// Based on HB 4.3.4, adapted for WoW 3.3.5a

using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.WoWInternals;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable disable
namespace Styx.Logic
{
    /// <summary>
    /// Helper class for managing player mounts.
    /// </summary>
    public static class MountHelper
    {
        // Engineering and profession-gated mounts (spell IDs)
        internal static readonly HashSet<int> _restrictedMountSpellIds = new HashSet<int>()
        {
            44151,  // Turbo-Charged Flying Machine
            44153,  // Flying Machine
            75973,  // X-53 Touring Rocket
            48025,  // Headless Horseman's Mount
            75596,  // Frosty Flying Carpet
            61309,  // Magnificent Flying Carpet
            61451,  // Flying Carpet
            63796,  // Mimiron's Head
            71342,  // Big Love Rocket
            59996,  // X-45 Heartbreaker
            93326   // Sandstone Drake (Cata, but kept for compatibility)
        };
        
        private static WaitTimer _refreshTimer = new WaitTimer(TimeSpan.FromMinutes(5.0));
        private static List<MountWrapper> _mountCache;
        private static ulong _cachedPlayerGuid;

        static MountHelper()
        {
            BotEvents.Player.OnMapChanged += new BotEvents.Player.MapChangedDelegate(OnMapChanged);
        }

        private static void OnMapChanged(BotEvents.Player.MapChangedEventArgs args)
        {
            _mountCache = null;
        }

        /// <summary>
        /// Gets the number of mounts the player has.
        /// </summary>
        public static int NumMounts
        {
            get
            {
                // Asked on every MoveTo through Mount.CanMount; the count only changes when a mount is learned.
                DateTime now = DateTime.UtcNow;
                if (now - _numMounts.checkedAt < TimeSpan.FromSeconds(5))
                    return _numMounts.count;
                int count = Lua.GetReturnVal<int>("return GetNumCompanions('MOUNT')", 0U);
                _numMounts = (count, now);
                return count;
            }
        }

        private static (int count, DateTime checkedAt) _numMounts;

        /// <summary>
        /// Gets all mounts available to the player.
        /// </summary>
        public static List<MountWrapper> Mounts
        {
            get
            {
                if (_refreshTimer.IsFinished || (long)_cachedPlayerGuid != (long)StyxWoW.Me.Guid)
                {
                    _mountCache = null;
                    _refreshTimer.Reset();
                }
                
                if (_mountCache == null)
                {
                    _cachedPlayerGuid = StyxWoW.Me.Guid;
                    using (new FrameLock())
                    {
                        List<MountWrapper> mountWrapperList = new List<MountWrapper>();
                        int numMounts = NumMounts;
                        // Check if we're in a battleground using MapId
                        // WotLK battleground map IDs: 30 (AV), 489 (WSG), 529 (AB), 566 (EotS), 607 (SotA), 628 (IoC)
                        uint mapId = StyxWoW.Me.MapId;
                        bool isBattleground = mapId == 30 || mapId == 489 || mapId == 529 || 
                                              mapId == 566 || mapId == 607 || mapId == 628;
                        
                        for (int slot = 1; slot <= numMounts; ++slot)
                        {
                            try
                            {
                                MountWrapper mountWrapper = new MountWrapper(slot);
                                switch (mountWrapper.CreatureSpellId)
                                {
                                    case -1:
                                        Logging.WriteDebug($"[Mount] {mountWrapper.Name} is known, but we don't have the skill to use it!");
                                        continue;
                                    case 44151: // Turbo-Charged Flying Machine
                                        if (StyxWoW.Me.GetSkill(SkillLine.Engineering)?.CurrentValue >= 375)
                                            break;
                                        goto case -1;
                                    case 44153: // Flying Machine
                                        if (StyxWoW.Me.GetSkill(SkillLine.Engineering)?.CurrentValue >= 300)
                                            break;
                                        goto case -1;
                                    case 61309: // Magnificent Flying Carpet
                                        if (StyxWoW.Me.GetSkill(SkillLine.Tailoring)?.CurrentValue >= 425)
                                            break;
                                        goto case -1;
                                    case 61451: // Flying Carpet
                                        if (StyxWoW.Me.GetSkill(SkillLine.Tailoring)?.CurrentValue >= 300)
                                            break;
                                        goto case -1;
                                    case 75596: // Frosty Flying Carpet
                                        if (StyxWoW.Me.GetSkill(SkillLine.Tailoring)?.CurrentValue >= 425)
                                            break;
                                        goto case -1;
                                }
                                
                                if (_restrictedMountSpellIds.Contains(mountWrapper.CreatureSpellId))
                                {
                                    if (isBattleground)
                                        continue;
                                }
                                
                                mountWrapperList.Add(mountWrapper);
                            }
                            catch (Exception ex)
                            {
                                Logging.Write($"Error getting mount info for mount slot {slot}. Exception: {ex}");
                            }
                        }
                        _mountCache = mountWrapperList;
                    }
                }
                return _mountCache;
            }
        }

        /// <summary>
        /// Gets all ground mounts available to the player.
        /// </summary>
        public static List<MountWrapper> GroundMounts
        {
            get
            {
                return Mounts.Where(m => 
                    m.Type == MountType.Ground || 
                    m.Type == MountType.EpicGroundOnly || 
                    m.Type == MountType.Scaling || 
                    _restrictedMountSpellIds.Contains(m.CreatureSpellId))
                    .ToList();
            }
        }

        /// <summary>
        /// Gets all flying mounts available to the player.
        /// </summary>
        public static List<MountWrapper> FlyingMounts
        {
            get
            {
                return Mounts.Where(m => 
                    m.Type == MountType.Flying || 
                    m.Type == MountType.TransformFlight || 
                    m.Type == MountType.Scaling || 
                    _restrictedMountSpellIds.Contains(m.CreatureSpellId))
                    .ToList();
            }
        }

        /// <summary>
        /// Gets all underwater mounts available to the player.
        /// </summary>
        public static List<MountWrapper> UnderwaterMounts
        {
            get
            {
                return Mounts.Where(m => 
                    m.Type == MountType.Underwater || 
                    m.Type == MountType.UnderwaterVashjir)
                    .ToList();
            }
        }

        /// <summary>
        /// Wrapper class for mount information.
        /// </summary>
        public sealed class MountWrapper
        {
            internal MountWrapper(int slot)
            {
                Slot = slot;
                List<string> returnValues = Lua.GetReturnValues($"return GetCompanionInfo('MOUNT', {slot})");
                
                if (returnValues == null || returnValues.Count < 5)
                {
                    CreatureId = 0;
                    Name = "Unknown";
                    CreatureSpellId = -1;
                    Icon = "";
                    IsSummoned = false;
                    Type = MountType.Unknown;
                    return;
                }
                
                int result1;
                if (!int.TryParse(returnValues[0], out result1))
                    result1 = 0;
                CreatureId = result1;
                
                Name = returnValues[1];
                
                int result2;
                if (!int.TryParse(returnValues[2], out result2))
                    result2 = 0;
                CreatureSpellId = result2;
                
                Icon = returnValues[3];
                IsSummoned = returnValues[4] == "1";
                
                if (CreatureSpellId != 0)
                {
                    CreatureSpell = WoWSpell.FromId(CreatureSpellId);
                    if (CreatureSpell != null)
                    {
                        // Try Cata+ MiscValueB classification first (Mount.dbc, values 225-248).
                        int miscValueB = CreatureSpell.SpellEffect1?.MiscValueB ?? 0;
                        if (Enum.IsDefined(typeof(MountType), miscValueB))
                        {
                            Type = (MountType)miscValueB;
                        }
                        else
                        {
                            // WotLK 3.3.5a: no Mount.dbc. Classify by inspecting all spell effects.
                            // WotLK flying mount spells include one of these aura types:
                            //   207 = SPELL_AURA_MOD_INCREASE_MOUNTED_FLIGHT_SPEED
                            //   208 = SPELL_AURA_MOD_INCREASE_FLIGHT_SPEED
                            //   209 = SPELL_AURA_MOUNTED_FLIGHT_SPEED_ALWAYS
                            //   211 = SPELL_AURA_MOD_MOUNTED_FLIGHT_SPEED_NOT_STACK
                            // WotLK ground mounts only have aura 32 (MOD_INCREASE_MOUNTED_SPEED)
                            // with BasePoints ≤ 100 (60% or 100% speed).
                            // Flying mounts add a *second* speed effect with BasePoints > 100
                            // (150% for regular flying, 280%/310% for epic/master flying).
                            Type = MountType.Ground; // safe default
                            foreach (var effect in CreatureSpell.SpellEffects)
                            {
                                if (effect == null) continue;
                                int auraId = (int)effect.AuraType;
                                // Primary: known flight-speed aura IDs from WotLK DBC.
                                if (auraId == 207 || auraId == 208 || auraId == 209 || auraId == 211)
                                {
                                    Type = MountType.Flying;
                                    break;
                                }
                                // Secondary: speed-modifier aura with BasePoints > 100 → flying speed.
                                // Ground mounts: 60% (BasePoints=59) or 100% (BasePoints=99).
                                // Flying mounts have at least 150% extra (BasePoints >= 149).
                                if ((auraId == 32 || auraId == 129 || auraId == 130) && effect.BasePoints >= 149)
                                {
                                    Type = MountType.Flying;
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        Type = MountType.Unknown;
                    }
                }
                else
                {
                    CreatureSpell = null;
                    Type = MountType.Unknown;
                }
            }

            /// <summary>
            /// The mount type (flying, ground, etc.)
            /// </summary>
            public MountType Type { get; private set; }

            /// <summary>
            /// The slot index in the mount collection.
            /// </summary>
            public int Slot { get; private set; }

            /// <summary>
            /// The creature ID of the mount.
            /// </summary>
            public int CreatureId { get; private set; }

            /// <summary>
            /// The spell ID used to summon this mount.
            /// </summary>
            public int CreatureSpellId { get; private set; }

            /// <summary>
            /// The WoWSpell used to summon this mount.
            /// </summary>
            public WoWSpell CreatureSpell { get; private set; }

            /// <summary>
            /// The icon path for the mount.
            /// </summary>
            public string Icon { get; private set; }

            /// <summary>
            /// Whether this mount is currently summoned.
            /// </summary>
            public bool IsSummoned { get; private set; }

            /// <summary>
            /// The display name of the mount.
            /// </summary>
            public string Name { get; private set; }

            /// <summary>
            /// Whether the player can currently mount this mount.
            /// </summary>
            public bool CanMount => CreatureSpell != null && CreatureSpell.CanCast;
        }
    }

    public enum MountType
    {
        Unknown = -1,
        EpicGroundOnly = 225,
        Flying = 229,
        Ground = 230,
        Underwater = 231,
        UnderwaterVashjir = 232,
        TransformFlight = 238,
        AhnQiraj = 241,
        ProfessionMount = 247,
        Scaling = 248
    }
}
