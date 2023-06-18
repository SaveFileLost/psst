using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Sandbox;

namespace Psst;

[Prefab]
public partial class StatusManager : EntityComponent
{
	[Net, Predicted] string Data { get; set; }
	[Net, Predicted] uint NextFreeId { get; set; } = 1;

	public Dictionary<uint, IStatus> statuses = new();

	readonly Dictionary<byte, Type> typeById = new();
	readonly Dictionary<Type, byte> idByType = new();

	// Modifying statuses outside of prediction sets it to dirty
	// It will not be read at the beginning of simulate so the change doesn't get overriden!
	bool dataDirty = false;

	bool isSimulating = false;

	Type TypeFromId(byte id)
	{
		if (typeById.TryGetValue(id, out var type))
		{
			return type;
		}

		Log.Error($"{type.Name} is unidentified");
		return null;
	}

	byte IdFromType(Type type)
	{
		if (idByType.TryGetValue(type, out var id))
			return id;

		Log.Error($"{type?.Name} is unidentified");
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
		if (!isSimulating && Game.IsClient)
		{
			Log.Info("StatusManager modified clientside outside of simulate, changes WILL be lost!");
			return;
		}

		// For some reason Prediction.Enabled is true outside of simulate, so we have to check for the client too
		if (Prediction.CurrentHost is null || !Prediction.Enabled)
			dataDirty = true;
	}

	void WriteData()
	{
		var stream = new MemoryStream();
		var writer = new BinaryWriter(stream);

		// If this is bigger than 255 we are FUCKED
		writer.Write((byte)statuses.Count);
		foreach (var (id, status) in statuses)
		{
			writer.Write(IdFromType(status.GetType()));
			writer.Write(id);

			if (status is ITimedStatus timed)
				writer.Write(timed.UntilRemoval.Absolute);

			if (status is ISerializableStatus ser)
				ser.Write(writer);
		}

		Data = Z85.ToZ85String(stream.ToArray(), autoPad: true);
	}

	void ReadData()
	{
		if (Data == default) return;

		statuses.Clear();

		var stream = new MemoryStream(Z85.FromZ85String(Data));
		var reader = new BinaryReader(stream);

		var statusCount = reader.ReadByte();
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
		isSimulating = true;

		// When TimeUntil is true, that means it passed
		var expiredStatuses = All<ITimedStatus>().Where(s => s.UntilRemoval < 0f);
		foreach (var status in expiredStatuses)
			Remove(status);

		return new SimulationDisposer(this);
	}

	internal void EndSimulate()
	{
		isSimulating = false;
		WriteData();
	}

	public T Create<T>() where T : struct, IStatus => Add(new T());

	public T Add<T>(T status) where T : struct, IStatus
	{
		// Keys won't collide, ids start at 1
		if (statuses.ContainsKey(status.Id))
		{
			Log.Error($"Manager already contains status {status.Id}. Are you looking for Replace()?");
			return status;
		}

		statuses[NextFreeId] = status;
		status.Id = NextFreeId;

		NextFreeId += 1;

		EvaluateDirty();

		return status;
	}

	public bool Has<T>() where T : struct, IStatus => statuses.Values.OfType<T>().Any();
	public bool Has<T0, T1>()
		where T0 : struct, IStatus
		where T1 : struct, IStatus
	=> statuses.Values.Where(s => s is T0 or T1).Any();
	public bool Has<T0, T1, T2>()
		where T0 : struct, IStatus
		where T1 : struct, IStatus
		where T2 : struct, IStatus
	=> statuses.Values.Where(s => s is T0 or T1 or T2).Any();
	public bool Has<T0, T1, T2, T3>()
		where T0 : struct, IStatus
		where T1 : struct, IStatus
		where T2 : struct, IStatus
		where T3 : struct, IStatus
	=> statuses.Values.Where(s => s is T0 or T1 or T2 or T3).Any();

	public T? Get<T>() where T : struct, IStatus => statuses.Values.OfType<T>().Cast<T?>().FirstOrDefault();
	public T? Get<T>(uint id) where T : struct, IStatus
		=> statuses.Values.OfType<T>().Where(s => s.Id == id).Cast<T?>().FirstOrDefault();

	public List<IStatus> All() => statuses.Values.ToList();
	public List<T> All<T>() where T : notnull, IStatus => statuses.Values.OfType<T>().ToList();

	public void Remove(IStatus status) => Remove(status.Id);
	public void Remove(uint id)
	{
		statuses.Remove(id);
		EvaluateDirty();
	}

	public void Replace<T>(T status) where T : struct, IStatus
	{
		// Keys won't collide, ids start at 1
		if (!statuses.ContainsKey(status.Id))
		{
			Log.Error($"Manager doesn't contain status {status.Id}. Are you looking for Add()?");
			return;
		}

		statuses[status.Id] = status;
		EvaluateDirty();
	}
}

internal class SimulationDisposer : IDisposable
{
	readonly StatusManager statusManager;

	public SimulationDisposer(StatusManager sm) => statusManager = sm;
	void IDisposable.Dispose() => statusManager.EndSimulate();
}