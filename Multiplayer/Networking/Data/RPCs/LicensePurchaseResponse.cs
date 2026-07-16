using LiteNetLib.Utils;

namespace Multiplayer.Networking.Data.RPCs;

public class LicensePurchaseResponse : IRpcResponse
{
    public enum ResponseType : byte
    {
        Success = 0,
        UnknownLicense = 1,
        OutstandingDebts = 2,
        InsufficientFunds = 3
    }

    public ResponseType Response { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)Response);
    }

    public void Deserialize(NetDataReader reader)
    {
        Response = (ResponseType)reader.GetByte();
    }
}
