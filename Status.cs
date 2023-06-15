using Sandbox;

namespace Psst;

public interface IStatus
{
	public uint Id { get; set; }
}

public class Status : IStatus
{
	public uint Id { get; set; }
}

public interface ISerializableStatus : IStatus
{
	public void Write(System.IO.BinaryWriter writer);
	public void Read(System.IO.BinaryReader read);
}

public interface ITimedStatus : IStatus
{
	public TimeUntil UntilRemoval { get; set; }
}