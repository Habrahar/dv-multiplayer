using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.ThingTypes;
using DV.Utils;
using JetBrains.Annotations;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace Multiplayer.Components.SaveGame;

public class NetworkedSaveGameManager : SingletonBehaviour<NetworkedSaveGameManager>
{
    private const string ROOT_KEY = "Multiplayer";
    private const string PLAYERS_KEY = "Players";

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
            //store inventory see StorageSerializer.SaveStorage()
            players.SetJObject(player.Guid.ToString(), playerData);
        }

        root.SetJObject(PLAYERS_KEY, players);
        data.SetJObject(ROOT_KEY, root);
    }

    public JObject Server_GetPlayerData(SaveGameData data, Guid guid)
    {
        return data?.GetJObject(ROOT_KEY)?.GetJObject(PLAYERS_KEY)?.GetJObject(guid.ToString());
    }

    #endregion

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedSaveGameManager)}]";
    }
}
