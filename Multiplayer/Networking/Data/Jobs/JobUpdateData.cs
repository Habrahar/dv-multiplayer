using DV.ThingTypes;
using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Data.Jobs;

public struct JobUpdateStruct : INetSerializable
{
    public ushort JobNetID;
    public bool Invalid;
    public JobState JobState;
    public float StartTime;
    public float FinishTime;
    public ushort ItemNetID;
    public uint ValidationStationId;
    public ItemPositionData ItemPositionData;

    /// <summary>
    /// Who took this job, or 0 for nobody and for an owner who is not currently connected.
    /// A receiving client compares it with its own PlayerId to decide whether the job is
    /// theirs to work, or just one to stop offering.
    /// </summary>
    public byte OwnerId;

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(JobNetID);
        writer.Put(Invalid);

        //Invalid jobs will be deleted / deregistered
        if (Invalid)
            return;

        writer.Put((byte)JobState);
        writer.Put(StartTime);
        writer.Put(FinishTime);
        writer.Put(ItemNetID);
        writer.Put(ValidationStationId);
        writer.Put(OwnerId);
        ItemPositionData.Serialize(writer,ItemPositionData);
    }

    public void Deserialize(NetDataReader reader)
    {
        JobNetID = reader.GetUShort();
        Invalid = reader.GetBool();

        if (Invalid)
            return;

        JobState = (JobState)reader.GetByte();
        StartTime = reader.GetFloat();
        FinishTime = reader.GetFloat();
        ItemNetID = reader.GetUShort();
        ValidationStationId = reader.GetUInt();
        OwnerId = reader.GetByte();
        ItemPositionData = ItemPositionData.Deserialize(reader);
    }
}
