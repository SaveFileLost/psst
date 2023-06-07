using System;
using System.Linq;
using System.Collections.Generic;
using Sandbox;

namespace Psst;

[Prefab]
public partial class StatusManager : EntityComponent
{
	[Net, Predicted] string Data { get; set; }
	[Net, Predicted] uint NextFreeId { get; set; }


	public Dictionary<uint, IStatus> statuses = new();

	readonly Dictionary<byte, Type> typeById = new();
	readonly Dictionary<Type, byte> idByType = new();

	Type TypeFromId(byte id)
	{
		if (typeById.TryGetValue(id, out var type))
			return type;

		Log.Error($"{type.Name} is unidentified");
		return null;
	}

	byte IdFromType(Type type)
	{
		if (idByType.TryGetValue(type, out var id))
			return id;

		Log.Error($"{type.Name} is unidentified");
		return 0;
	}

	public StatusManager()
	{
		var statusInterface = typeof(IStatus);
		var statusTypes = TypeLibrary.GetTypes().Where(t => t.Interfaces.Contains(statusInterface));

		foreach (var (statusType, typeId) in statusTypes.Select((v, k) => (v, k)))
		{
			typeById[(byte)typeId] = statusType.TargetType;
			idByType[statusType.TargetType] = (byte)typeId;
		}
	}

	protected override void OnActivate()
	{
		if (!Game.IsServer) return;
		WriteData();
	}

	void WriteData()
	{
		var stream = new System.IO.MemoryStream();
		var writer = new System.IO.BinaryWriter(stream);

		writer.Write((short)statuses.Count);
		foreach (var (id, status) in statuses)
		{
			writer.Write(IdFromType(status.GetType()));
			writer.Write(id);

			if (status is ITimedStatus timed)
				writer.Write(timed.UntilRemoval.Absolute);

			if (status is ISerializableStatus ser)
				ser.Write(writer);
		}
	}

	void ReadData()
	{
		if (Data == default) return;

		statuses = new();

		var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(Data));
		var reader = new System.IO.BinaryReader(stream);

		var statusCount = reader.ReadInt16();
		for (var i = 0; i < statusCount; i++)
		{
			var statusType = TypeFromId(reader.ReadByte());

			var status = TypeLibrary.Create<IStatus>(statusType);
			status.Id = reader.ReadUInt32();

			// TimeUntil probably drifts here. There is no way to create it from absolute time.
			if (status is ITimedStatus timed)
				timed.UntilRemoval = Time.Now - reader.ReadSingle();

			if (status is ISerializableStatus ser)
				ser.Read(reader);

			statuses[status.Id] = status;
		}
	}

	public IDisposable Simulate()
	{
		ReadData();

		// When TimeUntil is true, that means it passed
		var expiredStatuses = All<ITimedStatus>().Where(s => s.UntilRemoval);
		foreach (var status in expiredStatuses)
			Remove(status);

		return new SimulationDisposer(this);
	}

	internal void EndSimulate()
	{
		WriteData();
	}

	public T Create<T>() where T : struct, IStatus
	{
		T status = new() { Id = NextFreeId };
		statuses[status.Id] = status;

		NextFreeId += 1;

		return status;
	}

	public T? Get<T>() where T : struct, IStatus => statuses.Values.OfType<T>().Cast<T?>().FirstOrDefault();
	public T? Get<T>(uint id) where T : struct, IStatus
		=> statuses.Values.OfType<T>().Where(s => s.Id == id).Cast<T?>().FirstOrDefault();

	public List<IStatus> All() => statuses.Values.ToList();
	public List<T> All<T>() where T : IStatus => statuses.Values.OfType<T>().ToList();

	public void Remove(IStatus status) => Remove(status.Id);
	public void Remove(uint id) => statuses.Remove(id);
}

internal class SimulationDisposer : IDisposable
{
	readonly StatusManager statusManager;

	public SimulationDisposer(StatusManager sm) => statusManager = sm;
	void IDisposable.Dispose() => statusManager.EndSimulate();
}