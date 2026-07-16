
namespace Multiplayer.Networking.Packets.Clientbound.Jobs;

public class ClientboundJobValidateResponsePacket
{
    /// <summary>
    /// Why the server would not hand the job over. Only the server knows: it judges the asking
    /// player's licences and slots, not the host's. Without a reason on the wire the client just
    /// waited out its validator and played the error sound, saying nothing (B8).
    /// </summary>
    public enum RefusalReason : byte
    {
        Accepted = 0,
        NotAvailable = 1,   //gone, or no longer offered
        AlreadyTaken = 2,   //someone else got there first
        LicencesMissing = 3,
        NoFreeSlots = 4
    }

    public ushort JobNetId { get; set; }

    /// <summary>The server has no such job: the client's copy is junk and gets destroyed.</summary>
    public bool Invalid { get; set; }

    public RefusalReason Reason { get; set; }
}
