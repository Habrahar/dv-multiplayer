using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.ThingTypes;
using DV.Utils;
using JetBrains.Annotations;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using DV.Logic.Job;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace Multiplayer.Components.SaveGame;

public class NetworkedSaveGameManager : SingletonBehaviour<NetworkedSaveGameManager>
{
    private const string ROOT_KEY = "Multiplayer";
    private const string PLAYERS_KEY = "Players";

    // Job ownership is kept as its own map rather than in each player's data, and deliberately.
    // The players map excludes the host - their progress belongs to vanilla's save - but a job
    // the host took is still theirs, and vanilla's save has no idea a job has an owner at all.
    // Guessing "unclaimed means the host's" would also quietly hand them the job of any client
    // who never comes back. A job id names the owner outright, host included.
    private const string JOB_OWNERS_KEY = "JobOwners";

    // The client's belt, per player. Vanilla's own Storage_Inventory key holds the *host's*, so
    // this needs its own; a client's inventory is not part of the host's save game.
    private const string INVENTORY_KEY = "Inventory";

    protected override void Awake()
    {
        base.Awake();
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        LicenseManager.Instance.GarageUnlocked += Server_OnGarageUnlocked;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (UnloadWatcher.isUnloading)
            return;
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        LicenseManager.Instance.GarageUnlocked -= Server_OnGarageUnlocked;
    }

    // LicenseAcquired and JobLicenseAcquired are gone from here. This LicenseManager is the
    // host's own, so those events only ever said "the host got a licence", and broadcasting
    // that is what made licences shared. A client's purchase is now answered straight to the
    // buyer from NetworkServer. GarageUnlocked stays: garages are opened by a padlock in the
    // world, not bought, so there is no buyer to answer and they remain shared.

    #region Server

    private static void Server_OnGarageUnlocked(GarageType_v2 garage)
    {
        NetworkLifecycle.Instance.Server.SendGarage(garage.id);
    }

    public void Server_UpdateInternalData(SaveGameData data)
    {
        JObject root = data.GetJObject(ROOT_KEY) ?? [];
        JObject players = root.GetJObject(PLAYERS_KEY) ?? [];

        foreach (ServerPlayer player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.Peer == NetworkLifecycle.Instance.Server.SelfPeer || player.LoadingState != PlayerLoadingState.Complete)
                continue;

            JObject playerData = [];
            playerData.SetVector3(SaveGameKeys.Player_position, player.AbsoluteWorldPosition);
            playerData.SetFloat(SaveGameKeys.Player_rotation, player.WorldRotationY);
            playerData.SetFloat(SaveGameKeys.Player_money, (float)player.Money);
            playerData.SetStringArray(SaveGameKeys.Licenses_General, player.AcquiredGeneralLicenses.ToArray());
            playerData.SetStringArray(SaveGameKeys.Licenses_Jobs, player.AcquiredJobLicenses.ToArray());

            // The belt as the client last reported it. Null means they have not said yet - a
            // player still loading, most likely - and overwriting what is saved with nothing
            // would empty their pockets for them (B13).
            if (player.InventoryItems != null)
                playerData[INVENTORY_KEY] = JToken.FromObject(player.InventoryItems);

            players.SetJObject(player.Guid.ToString(), playerData);
        }

        root.SetJObject(PLAYERS_KEY, players);
        root.SetJObject(JOB_OWNERS_KEY, Server_BuildJobOwners());
        data.SetJObject(ROOT_KEY, root);
    }

    // Who owns what, by job id: those are stable across restarts, where a NetId is only a
    // session's handle and a PlayerId only a seat at the table.
    private static JObject Server_BuildJobOwners()
    {
        JObject owners = [];

        foreach (NetworkedJob job in NetworkedJob.GetAll())
        {
            if (job == null || job.OwnedBy == Guid.Empty || job.Job == null)
                continue;

            //only work in progress is worth an owner; finished and abandoned jobs are done with
            if (job.Job.State != JobState.InProgress)
                continue;

            owners.SetString(job.Job.ID, job.OwnedBy.ToString());
        }

        Multiplayer.LogDebug(() => $"[Diag] Jobs: saving {owners.Count} owner(s): {string.Join(", ", owners.Properties().Select(p => p.Name))}");
        return owners;
    }

    public JObject Server_GetPlayerData(SaveGameData data, Guid guid)
    {
        return data?.GetJObject(ROOT_KEY)?.GetJObject(PLAYERS_KEY)?.GetJObject(guid.ToString());
    }

    /// <summary>
    /// The belt this player logged out with, or null if the save has never heard of them - which
    /// is what tells the caller to hand out a starting kit rather than an empty belt.
    /// </summary>
    public static PlayerItemSaveData[] Server_GetPlayerInventory(JObject playerData)
    {
        JToken items = playerData?[INVENTORY_KEY];

        if (items == null)
            return null;

        try
        {
            return items.ToObject<PlayerItemSaveData[]>();
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"Server_GetPlayerInventory() could not read a saved inventory: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The owner a job was saved with, or Guid.Empty if it never had one. Called as each job is
    /// restored, before anyone has connected, so it answers with a Guid rather than a player.
    /// </summary>
    public Guid Server_GetJobOwner(string jobId)
    {
        string guid = SaveGameManager.Instance?.data?
            .GetJObject(ROOT_KEY)?
            .GetJObject(JOB_OWNERS_KEY)?
            .GetString(jobId);

        return Guid.TryParse(guid, out Guid owner) ? owner : Guid.Empty;
    }

    #endregion

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedSaveGameManager)}]";
    }
}
