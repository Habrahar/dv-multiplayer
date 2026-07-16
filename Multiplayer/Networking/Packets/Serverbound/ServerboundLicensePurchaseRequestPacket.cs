namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundLicensePurchaseRequestPacket
{
    public uint TicketId { get; set; }
    public string Id { get; set; }
    public bool IsJobLicense { get; set; }
}
