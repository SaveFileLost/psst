using Sandbox;

namespace Psst;

public interface IStatus
{
	public uint Id { get; set; }
	public float RemoveAfter { get; set; }
}

public interface ISerializableStatus : IStatus
{
	public void Write(System.IO.BinaryWriter writer);
	public void Read(System.IO.BinaryReader read);
}