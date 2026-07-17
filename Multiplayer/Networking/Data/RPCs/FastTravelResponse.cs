using LiteNetLib.Utils;

namespace Multiplayer.Networking.Data.RPCs;

public class FastTravelResponse : IRpcResponse
{
    public enum ResponseType : byte
    {
        Success = 0,
        UnknownDestination = 1,
        InsufficientFunds = 2
    }

    public ResponseType Response { get; set; }

    /// <summary>What the host charged. Only meaningful on success; shown to nobody, logged.</summary>
    public int Price { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)Response);
        writer.Put(Price);
    }

    public void Deserialize(NetDataReader reader)
    {
        Response = (ResponseType)reader.GetByte();
        Price = reader.GetInt();
    }
}
