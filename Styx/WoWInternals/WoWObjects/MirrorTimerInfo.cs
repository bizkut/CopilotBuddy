using System;
using System.Runtime.InteropServices;

namespace Styx.WoWInternals.WoWObjects
{
	[StructLayout(LayoutKind.Sequential, Size = 28)]
	public struct MirrorTimerInfo
	{
		[MarshalAs(UnmanagedType.U4)]
		public MirrorTimerType Type;

		public int InitialValue;

		public int MaxValue;

		public int ChangePerMillisecond;

		private uint _paused;

		public uint SpellID;

		public uint StartTime;

		/// <summary>Breath/fatigue left in milliseconds, on the client clock that stamped StartTime.</summary>
		public uint CurrentTime
		{
			get
			{
				long elapsed = (long)StyxWoW.WoWClient.PerformanceCounter() - this.StartTime;
				long value = this.InitialValue + (long)this.ChangePerMillisecond * elapsed;
				return (uint)Math.Clamp(value, 0L, this.MaxValue);
			}
		}

		public bool IsVisible
		{
			get
			{
				return this.StartTime != 0U;
			}
		}

		public override string ToString()
		{
			return string.Format("Type: {0}, InitialValue: {1}, MaxValue: {2}, ChangePerMillisecond: {3}, Paused: {4}, StartTime: {5}, SpellID: {6}",
				this.Type,
				this.InitialValue,
				this.MaxValue,
				this.ChangePerMillisecond,
				this._paused,
				this.StartTime,
				this.SpellID);
		}
	}
}
