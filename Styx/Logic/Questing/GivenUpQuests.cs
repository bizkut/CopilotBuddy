using System;
using System.Linq;
using Styx.Helpers;

namespace Styx.Logic.Questing
{
    /// <summary>
    /// Quests this character gave up on: a profile abandoned them with AbandonQuest Remember="True"
    /// because it cannot complete them (guarded turn-ins of converted profiles). Kept in the character
    /// settings so later profile loads skip the pickup instead of taking and abandoning the quest again.
    /// </summary>
    public static class GivenUpQuests
    {
        public static bool Contains(uint questId)
        {
            uint[] quests = CharacterSettings.Instance?.GivenUpQuests;
            return quests != null && Array.IndexOf(quests, questId) >= 0;
        }

        public static void Add(uint questId)
        {
            CharacterSettings settings = CharacterSettings.Instance;
            if (settings == null || Contains(questId))
                return;
            settings.GivenUpQuests = (settings.GivenUpQuests ?? Array.Empty<uint>()).Append(questId).ToArray();
            settings.Save();
        }
    }
}
