using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.SaveGame;
using Multiplayer.Networking.Data.Player;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Networking.Data;

public class ServerPlayer : IDisposable
{
    public const byte MAX_CREW_NAME_LENGTH = 6;
    public const float STARTING_MONEY = 1000f;
    #region ID Management
    private static readonly IdPool<byte> idPool = new();

    public void Dispose()
    {
        Multiplayer.LogDebug(() => $"Disposing ServerPlayer {Username} ({PlayerId})");
        if (PlayerId != 0)
        {
            idPool.ReleaseId(PlayerId);
            PlayerId = 0;
        }
    }
    #endregion

    public ITransportPeer Peer { get; private set; }
    public byte PlayerId { get; private set; }
    internal PlayerLoadingState LoadingState { get; set; } = PlayerLoadingState.None;
    public DateTime LastLogin { get; set; }
    private float PreviousPlayTime { get; set; }
    public float TotalPlaytime => PreviousPlayTime + (DateTime.UtcNow - LastLogin).Minutes;
    public string Username { get; set; }
    public string OriginalUsername { get; set; }
    public Guid Guid { get; set; }
    public string CharacterId { get; set; }
    public bool IsVR { get; }

    public PlayerTrackingData TrackingData { get; set; }
    public PlayerPostureFlags Posture { get; set; }        // already exists — keep
    public ushort CarId { get; set; }
    private string _crewName;
    public string CrewName
    {
        get
        {
            if (string.IsNullOrEmpty(_crewName))
                return string.Empty;
            return _crewName;
        }
        set
        {
            if (value != null)
            {
                if (value.Length > MAX_CREW_NAME_LENGTH)
                {
                    Multiplayer.LogWarning($"CrewName for player {Username} exceeds max length of {MAX_CREW_NAME_LENGTH}. Truncating.");
                    _crewName = value.Substring(0, MAX_CREW_NAME_LENGTH);
                }
                else
                {
                    _crewName = value;
                }
            }
            else
            {
                _crewName = string.Empty;
            }

            Dictionary<PlayerPreference, string> preferences = new()
            {
                { PlayerPreference.CrewName, _crewName }
            };

            NetworkLifecycle.Instance.Server.SendPlayerPreferencesUpdate(this, preferences);
        }
    }

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(CrewName))
                return Username;
            return $"[{CrewName}] {Username}";
        }
    }

    public Dictionary<NetworkedItem, uint> KnownItems { get; private set; } = new Dictionary<NetworkedItem, uint>(); //NetworkedItem, last updated tick
    public Dictionary<NetworkedItem, float> NearbyItems { get; private set; } = new Dictionary<NetworkedItem, float>(); //NetworkedItem, time since near the item
    public HashSet<ushort> OwnedItems { get; private set; } = new HashSet<ushort>();
    public StorageBase Storage { get; set; } = new StorageBase();

    private Vector3 _lastWorldPos = Vector3.zero;
    private Vector3 _lastAbsoluteWorldPosition = Vector3.zero;

    public ServerPlayer(ITransportPeer peer, string username, string originalUsername, Guid guid, string characterId, bool isVr)
    {
        PlayerId = idPool.NextId;

        Peer = peer;
        LastLogin = DateTime.UtcNow;

        Username = username;
        OriginalUsername = originalUsername;
        Guid = guid;
        CharacterId = characterId;

        IsVR = isVr;
    }

    #region Positioning
    public Vector3 RawPosition => TrackingData.Position ?? Vector3.zero;
    public float RawRotationY => TrackingData.RotationY ?? 0f;

    public Vector3 AbsoluteWorldPosition
    {
        get
        {

            Vector3 pos;
            try
            {
                if (CarId == 0 || !NetworkedTrainCar.TryGet(CarId, out NetworkedTrainCar car))
                {
                    if (CarId != 0)
                        Multiplayer.LogDebug(() => $"AbsoluteWorldPosition() noID {Username}: CarId: {CarId}");

                    pos = RawPosition;
                }
                else
                {
                    //Multiplayer.LogDebug(() => $"AbsoluteWorldPosition() hasID {Username}: CarId: {CarId}");
                    pos = car.transform.TransformPoint(RawPosition) - WorldMover.currentMove; ;
                }

                _lastAbsoluteWorldPosition = pos;
            }
            catch (Exception e)
            {
                Multiplayer.LogWarning($"AbsoluteWorldPosition() Exception {Username}");
                Multiplayer.LogWarning(e.Message);
                Multiplayer.LogWarning(e.StackTrace);
                pos = _lastAbsoluteWorldPosition;
            }

            return pos;

        }
    }

    public Vector3 WorldPosition
    {
        get
        {
            Vector3 pos;
            try
            {
                if (CarId == 0 || !NetworkedTrainCar.TryGet(CarId, out NetworkedTrainCar car))
                {
                    if (CarId != 0)
                        Multiplayer.LogDebug(() => $"WorldPosition() noID {Username}: CarId: {CarId}");

                    pos = RawPosition + WorldMover.currentMove;
                }
                else
                {
                    //Multiplayer.LogDebug(() => $"WorldPosition() hasID {Username}: CarId: {CarId}");
                    pos = car.transform.TransformPoint(RawPosition);
                }

                _lastWorldPos = pos;
            }
            catch (Exception e)
            {
                Multiplayer.LogWarning($"WorldPosition() Exception {Username}");
                Multiplayer.LogWarning(e.Message);
                Multiplayer.LogWarning(e.StackTrace);

                pos = _lastWorldPos;
            }

            return pos;
        }
    }

    public float WorldRotationY => CarId == 0 || !NetworkedTrainCar.TryGet(CarId, out NetworkedTrainCar car)
        ? RawRotationY
        : (Quaternion.Euler(0, RawRotationY, 0) * car.transform.rotation).eulerAngles.y;
    #endregion

    #region Wallet
    // The host's wallet is its own Inventory, which the vanilla save owns. Remote
    // players are backed by _money and persisted per Guid by NetworkedSaveGameManager,
    // which skips the host for exactly that reason.
    public bool IsHost => Peer == NetworkLifecycle.Instance.Server.SelfPeer;

    private double _money;

    public double Money => IsHost ? Inventory.Instance.PlayerMoney : _money;

    public void AddMoney(double amount)
    {
        if (amount <= 0d)
            return;

        if (IsHost)
        {
            Inventory.Instance.AddMoney(amount);
            return;
        }

        SetMoneyInternal(_money + amount);
    }

    public bool RemoveMoney(double amount)
    {
        if (amount <= 0d)
            return true;

        if (IsHost)
            return Inventory.Instance.RemoveMoney(amount);

        if (_money < amount)
            return false;

        SetMoneyInternal(_money - amount);
        return true;
    }

    // Used on join and by the save loader, where the balance is restored rather than earned.
    public void SetMoney(double amount)
    {
        if (IsHost)
        {
            Inventory.Instance.SetMoney(Math.Max(0d, amount));
            return;
        }

        SetMoneyInternal(amount);
    }

    private void SetMoneyInternal(double value)
    {
        _money = Math.Max(0d, value);
        NetworkLifecycle.Instance.Server.SendMoney(this);
    }
    #endregion

    #region Licenses
    // Same split as the wallet: the host's licences are its own LicenseManager, which the
    // vanilla save owns, and remote players carry their own sets persisted per Guid. Nothing
    // here is meaningful for the host - read its licences from the save instead.
    // Garages are deliberately absent: they are unlocked by a padlock out in the world, not
    // bought, so there is no server-side moment to attribute one to a player. They stay shared
    // until work trains get sorted out.
    private readonly HashSet<string> generalLicenses = [];
    private readonly HashSet<string> jobLicenses = [];

    public IReadOnlyCollection<string> AcquiredGeneralLicenses => generalLicenses;
    public IReadOnlyCollection<string> AcquiredJobLicenses => jobLicenses;

    /// <summary>
    /// What a player who has never joined before starts with. Vanilla's LoadData forces these
    /// on anyone not in restricted mode anyway, complaining to the log as it goes, so handing
    /// them over up front is the only story the game will accept quietly.
    /// </summary>
    public static IEnumerable<string> StartingGeneralLicenses =>
        LicenseManager.TutorialGeneralLicenses.Select(license => license.id);

    public static IEnumerable<string> StartingJobLicenses =>
        [JobLicenses.FreightHaul.ToV2().id];

    public bool AddGeneralLicense(string id)
    {
        if (IsHost || !generalLicenses.Add(id))
            return false;

        NetworkLifecycle.Instance.Server.SendLicense(this, id, false);
        return true;
    }

    public bool AddJobLicense(string id)
    {
        if (IsHost || !jobLicenses.Add(id))
            return false;

        NetworkLifecycle.Instance.Server.SendLicense(this, id, true);
        return true;
    }

    // Restored from the save on join, so no packets: the balance and the licences both travel
    // in the ClientboundSaveGameDataPacket the caller is building.
    public void LoadLicenses(string[] general, string[] job)
    {
        generalLicenses.Clear();
        jobLicenses.Clear();

        generalLicenses.UnionWith(general ?? StartingGeneralLicenses.ToArray());
        jobLicenses.UnionWith(job ?? StartingJobLicenses.ToArray());
    }

    private bool HasGeneralLicense(GeneralLicenseType license)
    {
        GeneralLicenseType_v2 v2 = license.ToV2();
        return IsHost ? LicenseManager.Instance.IsGeneralLicenseAcquired(v2) : generalLicenses.Contains(v2.id);
    }

    public bool IsLicensedForJob(JobLicenses required)
    {
        if (IsHost)
            return LicenseManager.Instance.IsLicensedForJob(JobLicenseType_v2.ToV2List(required));

        foreach (JobLicenseType_v2 license in JobLicenseType_v2.ToV2List(required))
            if (!jobLicenses.Contains(license.id))
                return false;

        return true;
    }

    /// <summary>
    /// Mirrors LicenseManager.GetNumberOfAllowedConcurrentJobs, which reads the host's licences
    /// and so cannot answer for anyone else.
    /// </summary>
    public int AllowedConcurrentJobs =>
        HasGeneralLicense(GeneralLicenseType.ConcurrentJobs2) ? int.MaxValue :
        HasGeneralLicense(GeneralLicenseType.ConcurrentJobs1) ? 2 : 1;
    #endregion

    #region Jobs
    // NetIds of the jobs this player has taken and not yet finished. The host's JobsManager
    // holds every player's jobs because that is what ticks their tasks, so its count cannot
    // answer "how many does this player have?".
    private readonly HashSet<ushort> takenJobs = [];

    public int TakenJobCount => takenJobs.Count;

    public void AddTakenJob(ushort jobNetId) => takenJobs.Add(jobNetId);
    public void RemoveTakenJob(ushort jobNetId) => takenJobs.Remove(jobNetId);
    #endregion

    #region Item Ownership
    public bool OwnsItem(ushort itemNetId) => OwnedItems.Contains(itemNetId);

    public void AddOwnedItem(ushort itemNetId)
    {
        OwnedItems.Add(itemNetId);
        NetworkLifecycle.Instance.Server.LogDebug(() => $"Player {Username} now owns item {itemNetId}");
    }

    public void AddOwnedItems(IEnumerable<ushort> itemNetIds)
    {
        OwnedItems.UnionWith(itemNetIds);
        NetworkLifecycle.Instance.Server.LogDebug(() => $"Player {Username} batch added items: {string.Join(", ", itemNetIds)}");
    }

    public void RemoveOwnedItem(ushort itemNetId)
    {
        if (OwnedItems.Remove(itemNetId))
        {
            NetworkLifecycle.Instance.Server.LogDebug(() => $"Player {Username} no longer owns item {itemNetId}");
        }
    }

    public void ClearOwnedItems()
    {
        OwnedItems.Clear();
        NetworkLifecycle.Instance.Server.LogDebug(() => $"Cleared all owned items for player {Username}");
    }

    public bool TryGetOwnedItem(ushort itemNetId, out NetworkedItem item)
    {
        if (OwnedItems.Contains(itemNetId) && NetworkedItem.TryGet(itemNetId, out item))
        {
            return true;
        }
        item = null;
        return false;
    }
    #endregion

    public override string ToString()
    {
        return $"{PlayerId} ({Username}, {Guid.ToString()})";
    }
}
