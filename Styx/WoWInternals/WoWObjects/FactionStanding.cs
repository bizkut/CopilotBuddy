using System.Runtime.InteropServices;

namespace Styx.WoWInternals.WoWObjects
{
    /// <summary>
    /// A player's standing with a faction: one 16-byte entry of the client's reputation array
    /// (0xC22B78, indexed by Faction.dbc ReputationIndex). GetRepListRepValue (0x5D05B0) returns
    /// the sum of the first two ints; the last two are not read.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FactionStanding
    {
        /// <summary>Starting reputation for the player's race and class (Faction.dbc base).</summary>
        public int BaseReputation;

        /// <summary>Reputation earned on top of the base (character_reputation.standing).</summary>
        public int Reputation;

        private readonly int _unknown8;
        private readonly int _unknownC;

        /// <summary>Total reputation, the value the client ranks Hated..Exalted on.</summary>
        public int TotalReputation => BaseReputation + Reputation;
    }
}
