using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Serverbound;

/// <summary>
/// A client's belt, as it stands. The inventory lives on the client - it is their game that owns
/// the slots - so the host cannot read it and can only be told. It keeps the latest telling and
/// writes it to the save, which is what lets a player buy something in a shop, log out, and find
/// it still there (B13). Sent when the belt changes, not on a clock.
/// </summary>
public class ServerboundPlayerInventoryPacket
{
    public PlayerItemSaveData[] Items { get; set; }
}
