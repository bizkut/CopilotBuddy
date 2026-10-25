using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Styx.Helpers;

namespace Styx.Logic.Profiles.Quest
{
	/// <summary>
	/// Information about a quest.
	/// </summary>
	public class QuestInfo
	{
		public uint ID { get; private set; }
		public string Name { get; private set; }
		public List<ObjectiveInfo> Objectives { get; private set; }
		/// <summary>
		/// Daily quest (profile attribute Daily="True"). The server keeps a turned-in daily in the
		/// completed-quest list for good (AzerothCore) or never (TrinityCore); the quest log treats it as
		/// completed only while it is in today's daily slots, so a daily profile runs again after the reset.
		/// </summary>
		public bool Daily { get; private set; }

		public QuestInfo(uint id, string name)
		{
			Objectives = new List<ObjectiveInfo>();
			ID = id;
			Name = name;
		}

		/// <summary>
		/// Finds the turn-in objective for this quest.
		/// </summary>
		public TurnInObjectiveInfo? FindTurnIn()
		{
			return (TurnInObjectiveInfo?)Objectives.FirstOrDefault(
				o => o is TurnInObjectiveInfo && o.Type == ObjectiveType.TurnIn);
		}

		/// <summary>
		/// Finds the kill mob objective info for the specified mob ID.
		/// </summary>
		public KillMobObjectiveInfo? FindKillMob(uint mobId)
		{
			return (KillMobObjectiveInfo?)Objectives.FirstOrDefault(
				o => o is KillMobObjectiveInfo kmo && kmo.MobID == mobId);
		}

		/// <summary>
		/// Finds the collect item objective info for the specified item ID.
		/// </summary>
		public CollectItemObjectiveInfo? FindCollectItem(uint itemID)
		{
			return (CollectItemObjectiveInfo?)Objectives.FirstOrDefault(
				o => o is CollectItemObjectiveInfo cio && cio.ItemID == itemID);
		}

		/// <summary>
		/// Finds the use game object objective info for the specified game object ID.
		/// </summary>
		public UseObjectObjectiveInfo? FindUseGameObject(uint gameObjectID)
		{
			return (UseObjectObjectiveInfo?)Objectives.FirstOrDefault(
				o => o is UseObjectObjectiveInfo uoo && uoo.ObjectID == gameObjectID);
		}

		public static QuestInfo FromXML(XElement element)
		{
			uint id = 0U;
			string name = string.Empty;
			bool daily = false;
			foreach (XAttribute xattribute in element.Attributes())
			{
				try
				{
					string? text;
					if ((text = xattribute.Name.ToString().ToLower()) != null)
					{
						if (text == "id" || text == "questid")
						{
							if (!uint.TryParse(xattribute.Value, out id))
							{
								throw new ProfileAttributeExpectedException<uint>(xattribute);
							}
						}
						else if (text == "name")
						{
							name = xattribute.Value;
						}
						else if (text == "daily")
						{
							if (!bool.TryParse(xattribute.Value, out daily))
							{
								throw new ProfileAttributeExpectedException<bool>(xattribute);
							}
						}
					}
				}
				catch (ProfileException ex)
				{
					Logging.WriteDebug(ex.Message);
				}
			}
			if (id == 0U)
			{
				throw new ProfileMissingAttributeException<uint>("Id", element);
			}
			QuestInfo questInfo = new QuestInfo(id, name) { Daily = daily };
			foreach (XElement xelement in element.Elements())
			{
				try
				{
					ObjectiveInfo objective = ObjectiveInfo.FromXML(xelement);
					questInfo.Objectives.Add(objective);
				}
				catch (ProfileException ex)
				{
					Logging.WriteDebug(ex.Message);
				}
			}
			return questInfo;
		}
	}
}
