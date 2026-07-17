namespace Multiplayer.Networking.Packets.Serverbound;

/// <summary>
/// Asks the host to charge for a fast travel. The price is deliberately absent: vanilla takes it
/// from the local Inventory, which on a client is only a mirror of the wallet the server holds,
/// so the trip cost nothing and the next balance packet handed the money back (B11). The host
/// works the fare out itself from this marker and where the player actually is - a price off the
/// wire would just be a different way of travelling free.
/// </summary>
public class ServerboundFastTravelRequestPacket
{
    public uint TicketId { get; set; }
    public string MarkerName { get; set; }
}
