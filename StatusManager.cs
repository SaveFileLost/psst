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

	// Modifying statuses outside of prediction sets it to dirty
	// It will not be read at the beginning of simulate so the change doesn't get overriden!
	bool dataDirty = false;

	Type TypeFromId(byte id)
	{
		Log.Info($"id given: {id}");

		if (typeById.TryGetValue(id, out var type))
		{
			Log.Info($"type given: {type.Name}");
			return type;
		}

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
		var statusTypes = TypeLibrary.GetTypes()
			.Where(t => t.Interfaces.Contains(statusInterface))
			.OrderBy(t => t.Name);

		// Default ordering isn't deterministic, so we order them by name

		foreach (var (statusType, typeId) in statusTypes.Select((v, k) => (v, k)))
		{
			Log.Info(statusType.Name);

			typeById[(byte)typeId] = statusType.TargetType;
			idByType[statusType.TargetType] = (byte)typeId;
		}
	}

	protected override void OnActivate()
	{
		if (!Game.IsServer) return;
		WriteData();
	}

	// Always run this after modifying the state!
	void EvaluateDirty()
	{
		// For some reason Prediction.Enabled is true outside of simulate, so we have to check for the client too
		if (Prediction.CurrentHost is null || !Prediction.Enabled)
			dataDirty = true;
	}

	void WriteData()
	{
		var stream = new System.IO.MemoryStream();
		var writer = new System.IO.BinaryWriter(stream);

		writer.Write((ushort)statuses.Count);
		foreach (var (id, status) in statuses)
		{
			writer.Write(IdFromType(status.GetType()));
			writer.Write(id);

			if (status is ITimedStatus timed)
				writer.Write(timed.UntilRemoval.Absolute);

			if (status is ISerializableStatus ser)
				ser.Write(writer);
		}

		stream.Position = 0;
		Data = new System.IO.StreamReader(stream).ReadToEnd();
	}

	void ReadData()
	{
		if (Data == default) return;

		statuses = new();

		var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(Data));
		var reader = new System.IO.BinaryReader(stream);

		var statusCount = reader.ReadUInt16();
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
		if (!dataDirty)
			ReadData();

		dataDirty = false;

		// When TimeUntil is true, that means it passed
		var expiredStatuses = All<ITimedStatus>().Where(s => s.UntilRemoval < 0f);
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
		return Add(status);
	}

	public T Add<T>(T status) where T : struct, IStatus
	{
		statuses[status.Id] = status;
		NextFreeId += 1;

		EvaluateDirty();

		return status;
	}

	public T? Get<T>() where T : struct, IStatus => statuses.Values.OfType<T>().Cast<T?>().FirstOrDefault();
	public T? Get<T>(uint id) where T : struct, IStatus
		=> statuses.Values.OfType<T>().Where(s => s.Id == id).Cast<T?>().FirstOrDefault();

	public List<IStatus> All() => statuses.Values.ToList();
	public List<T> All<T>() where T : IStatus => statuses.Values.OfType<T>().ToList();

	public void Remove(IStatus status) => Remove(status.Id);
	public void Remove(uint id)
	{
		statuses.Remove(id);
		EvaluateDirty();
	}
}

internal class SimulationDisposer : IDisposable
{
	readonly StatusManager statusManager;

	public SimulationDisposer(StatusManager sm) => statusManager = sm;
	void IDisposable.Dispose() => statusManager.EndSimulate();
}