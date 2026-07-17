using System.Collections.Generic;
using System.Linq;
using DV.CabControls;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Networking.Data;
using Multiplayer.Components.Networking.World;
using System;
using Multiplayer.Utils;
using DV;
using DV.Interaction;
using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Components.Networking.World;

public class NetworkedItemManager : SingletonBehaviour<NetworkedItemManager>
{
    /*
     * Server 
     */

    //Culling distance for items
    public const float MAX_DISTANCE_TO_ITEM = 100f;
    public const float MAX_DISTANCE_TO_ITEM_SQR = MAX_DISTANCE_TO_ITEM * MAX_DISTANCE_TO_ITEM;
    public const float NEARBY_REMOVAL_DELAY = 3f; // 3 seconds delay
    public const float REACH_DISTANCE_BUFFER = 0.5f;
    public float MAX_REACH_DISTANCE = 4f + REACH_DISTANCE_BUFFER;         //from the game, but we should try to look up the value

    //caches for item snapshots
    private List<ItemUpdateData> DestroyedItems = new(64);

    //Item ownership
    //private Dictionary<ushort, PlayerInventory> playerInventories = new Dictionary<ushort, PlayerInventory>();
    //private Dictionary<NetworkedItem, ushort> itemToPlayerMap = new Dictionary<NetworkedItem, ushort>();


    /*
     * Client
     */

    //cache for client-sided items & spawns
    private Dictionary<string, List<NetworkedItem>> CachedItems = new(1024); //Client cached items
    private HashSet<NetworkedItem> CachedItemSet = new(1024);                //Guard against caching the same instance twice
    private Dictionary<string, InventoryItemSpec> ItemPrefabs = new(1024);   //Item prefabs
    private bool ClientInitialised = false;


    /* 
     * Common
     */
    private Queue<Tuple<ItemUpdateData, ServerPlayer>> ReceivedSnapshots = new(64);

    protected override void Awake()
    {
        base.Awake();
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        NetworkLifecycle.Instance.Server.PlayerDisconnected += PlayerDisconnected;

        try
        {
            MAX_REACH_DISTANCE = GrabberRaycasterDV.RAYCAST_MAX_DIST + REACH_DISTANCE_BUFFER;
        }
        catch (Exception ex)
        {
            NetworkLifecycle.Instance.Server.LogWarning($"NatworkedItemManager.Awake() Failed to find GrabberRaycasterDV\r\n{ex.Message}");
        }
    }

    // Whatever the player was carrying is hidden on every other machine and owned by an id that
    // will never speak again. Hand the items back to the world at the spot they left from.
    private void PlayerDisconnected(ServerPlayer player)
    {
        if (player == null)
            return;

        int released = 0;

        foreach (ushort netId in player.OwnedItems.ToArray())
        {
            try
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem netItem) || netItem == null)
                    continue;

                //a metre up, so it lands at their feet instead of inside the ground
                netItem.ReleaseFromDisconnectedOwner(player.WorldPosition + Vector3.up);
                released++;
            }
            catch (Exception ex)
            {
                NetworkLifecycle.Instance.Server.LogError($"PlayerDisconnected({player.Username}) item {netId}: {ex.Message}");
            }
        }

        player.ClearOwnedItems();
        player.KnownItems.Clear();
        player.NearbyItems.Clear();

        Multiplayer.Log($"[Diag] Items: {player.Username} left, returned {released} carried item(s) to the world.");
    }

    protected void Start()
    {
        NetworkLifecycle.Instance.OnTick += Common_OnTick;

        BuildPrefabLookup();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (UnloadWatcher.isQuitting)
            return;

        NetworkLifecycle.Instance.OnTick -= Common_OnTick;

        if (NetworkLifecycle.Instance.IsHost())
            NetworkLifecycle.Instance.Server.PlayerDisconnected -= PlayerDisconnected;
    }

    public void AddDirtyItemSnapshot(NetworkedItem netItem, ItemUpdateData snapshot)
    {
        DestroyedItems.Add(snapshot);

        foreach(var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if(player.KnownItems.ContainsKey(netItem))
                player.KnownItems.Remove(netItem);

            if(player.NearbyItems.ContainsKey(netItem))
                player.NearbyItems.Remove(netItem);
        }
    }

    public void ReceiveSnapshots(List<ItemUpdateData> snapshots, ServerPlayer sender)
    {
        if (snapshots == null)
            return;

        foreach (var snapshot in snapshots)
        {
            ReceivedSnapshots.Enqueue(new (snapshot, sender));
        }

        //Multiplayer.LogDebug(() => $"NetworkItemManager.ReceiveSnapshots() count: {ReceivedSnapshots.Count}, from: ");
    }

    #region Common

    private void Common_OnTick(uint tick)
    {
        ProcessReceived();

        if (NetworkLifecycle.Instance.IsHost())
        {
            UpdatePlayerItemLists();
            ProcessChanged(tick);
        }
        else
        {
            SuppressLocalWorldItems();
            ProcessClientChanges(tick);
        }
    }

    private void ProcessReceived()
    {
        while (ReceivedSnapshots.Count > 0)
        {
            var snapshotInfo = ReceivedSnapshots.Dequeue();
            ItemUpdateData snapshot = snapshotInfo.Item1;
            try
            {
                //Multiplayer.LogDebug(() => $"ProcessReceived: {snapshot.UpdateType}");

                if (snapshot == null || snapshot.UpdateType == ItemUpdateData.ItemUpdateType.None)
                {
                    Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Invalid Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
                    continue;
                }

                if (NetworkLifecycle.Instance.IsHost())
                {
                    ProcessReceivedAsHost(snapshot, snapshotInfo.Item2);
                }
                else
                {
                    ProcessReceivedAsClient(snapshot);
                }
            }
            catch (Exception ex)
            {
                Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Error! {ex.Message}\r\n{ex.StackTrace}");
            }
        }
    }

    #endregion

    #region Server

    //reused each pass; NearbyItems cannot be edited while it is being walked
    private readonly List<NetworkedItem> staleItems = new(64);

    // Deciding who can see what is O(players x items) - 700-odd items against every player - and
    // it does not need doing 24 times a second. A player crosses the 100 m boundary no faster
    // than a train moves, and NEARBY_REMOVAL_DELAY gives another 3 s of slack on top.
    private const float RANGE_SWEEP_PERIOD = 0.25f;
    private float lastRangeSweep;

    private void UpdatePlayerItemLists()
    {
        if (Time.time - lastRangeSweep < RANGE_SWEEP_PERIOD)
            return;

        lastRangeSweep = Time.time;

        float currentTime = Time.time;

        var allItems = NetworkedItem.GetAll();

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            Vector3 playerPosition = player.WorldPosition;

            foreach (var item in allItems)
            {
                if (item == null)
                {
                    NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Null item found in allItems!");
                    continue;
                }

                float sqrDistance = (playerPosition - item.transform.position).sqrMagnitude;

                if (sqrDistance <= MAX_DISTANCE_TO_ITEM_SQR)
                {
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Adding for player: {player?.Username}, Nearby Item: {item?.NetId}, {item?.name}");
                    player.NearbyItems[item] = currentTime;
                }
            }

            // Remove items that are no longer nearby. Collect first: removing inside an indexed
            // ElementAt() walk shifts the dictionary under the index and skips entries, so stale
            // items lingered - and ElementAt() on a dictionary is O(n), making the walk O(n^2).
            staleItems.Clear();

            foreach (var kvp in player.NearbyItems)
                if (currentTime - kvp.Value > NEARBY_REMOVAL_DELAY)
                    staleItems.Add(kvp.Key);

            // Forget it was sent, so coming back re-announces what is actually there - otherwise
            // KnownItems would swear they still have it and the Create would never come again
            // (B23). Their copy simply stays put and idle until then.
            //
            // We do NOT take the item back with a Destroy. Tried that in 0.1.23.0 and it was too
            // sharp a tool: leaving range is decided from a player's reported position, and any
            // hiccup in that - a moment on a car, a frame with stale tracking - despawned the
            // world and rebuilt it. The logs caught it thrashing 54 items four times over
            // (B27). A stale idle copy is a far smaller price than that.
            foreach (var item in staleItems)
            {
                player.KnownItems.Remove(item);
                player.NearbyItems.Remove(item);
            }
        }
    }

    // Keyed by net id: this is looked up once per nearby item per player, and a linear scan of
    // everything that changed made that quadratic.
    private readonly Dictionary<ushort, ItemUpdateData> dirtyItems = new(64);

    private void ProcessChanged(uint tick)
    {
        dirtyItems.Clear();

        foreach (var item in NetworkedItem.GetAll())
        {
            ItemUpdateData snapshot = item.GetSnapshot();
            if (snapshot != null)
                dirtyItems[snapshot.ItemNetId] = snapshot;
        }

        //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) DirtyItems: {dirtyItems.Count}");

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            List<ItemUpdateData> playerUpdates = new List<ItemUpdateData>();

            // Process nearby items
            foreach (var nearbyItem in player.NearbyItems.Keys)
            {
                if (!player.KnownItems.ContainsKey(nearbyItem))
                {
                    // This is a new item for the player
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) New item for: {player.Username}, itemNetID{nearbyItem.NetId}");

                    ItemUpdateData snapshot = nearbyItem.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create);

                    // Could not describe the item, so the player cannot receive it. Calling it
                    // known anyway would leave us addressing state to an item they will never
                    // have, for the rest of the session (B18). Try again next tick instead.
                    if (snapshot == null)
                        continue;

                    player.KnownItems[nearbyItem] = tick;

                    //prevent propagation of creates for special items
                    //(GetType() here would always be NetworkedItem - the paper types live in TrackedItemType)
                    //the job system makes these on every machine, so they are known without a Create
                    if(!DoNotCreateItem(nearbyItem.TrackedItemType))
                        playerUpdates.Add(snapshot);
                }
                else
                {
                    // Check if this item is in the dirty items list
                    dirtyItems.TryGetValue(nearbyItem.NetId, out ItemUpdateData dirtyUpdate);

                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Item exists for: {player.Username}, {dirtyUpdate != null}");

                    if (dirtyUpdate == null)
                    {
                        //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Item exists for: {player.Username}, LastDirtyTick: {player.KnownItems[nearbyItem] < nearbyItem.LastDirtyTick}");
                        if (player.KnownItems[nearbyItem] < nearbyItem.LastDirtyTick)
                        {
                            dirtyUpdate = nearbyItem.CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);
                        }
                    }

                    if (dirtyUpdate != null)
                    {
                        Multiplayer.LogDebug(() => $"ProcessChanged({tick}) Update Type: {dirtyUpdate.UpdateType}, Item State: {dirtyUpdate.ItemState}");
                        playerUpdates.Add(dirtyUpdate);
                        player.KnownItems[nearbyItem] = tick;
                    }
                }
            }

            //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Adding {DestroyedItems.Count()} DestroyedItems for: {player.Username}");

            playerUpdates.AddRange(DestroyedItems);

            if (playerUpdates.Count > 0)
            {
                //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Sending {playerUpdates.Count()} to player: {player.Username}");
                NetworkLifecycle.Instance.Server.SendItemsChangePacket(playerUpdates, player);
            }
        }

        DestroyedItems.Clear();
    }

    private void ProcessReceivedAsHost(ItemUpdateData snapshot, ServerPlayer player)
    {
        if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Create)
        {
            NetworkLifecycle.Instance.Server.LogError($"NetworkedItemManager.ProcessReceivedAsHost() Host received Create snapshot! ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
            return;
        }

        // Player arrives off the wire and decides who ends up owning the item. Whoever sent the
        // packet is the only one who can have acted, so name them and ignore the claim - naming
        // someone else would hand them an item they never touched, and lock it there.
        snapshot.Player = player.PlayerId;

        if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem netItem))
        {
            if (ValidatePlayerAction(snapshot, player)) //Ensure the player can do this
            {
                NetworkLifecycle.Instance.Server.LogDebug(() => $"NetworkedItemManager.ProcessReceivedAsHost() ItemNetId: {snapshot.ItemNetId}, snapshot type: {snapshot.UpdateType}");
                netItem.ReceiveSnapshot(snapshot);

                // Applying the snapshot cleared the dirty flags, so ProcessChanged would tell
                // nobody: with three players, B never sees what A did. Date the item now so the
                // others get a FullSync, and date it for the sender too - they already did this,
                // and replaying a throw at them would add a second impulse.
                if (player.KnownItems.ContainsKey(netItem))
                    player.KnownItems[netItem] = NetworkLifecycle.Instance.Tick;

                netItem.MarkRelayDirty();
            }
            else
            {
                NetworkLifecycle.Instance.Server.LogWarning($"NetworkedItemManager.ProcessReceivedAsHost() Player action validation failed for ItemNetId: {snapshot.ItemNetId}");

                // Refusing in silence leaves them holding an item that, to everyone else, never
                // left the ground - and nothing would ever tell them otherwise. Dating the item
                // makes ProcessChanged send them what is actually true, so their copy snaps back.
                netItem.MarkRelayDirty();
            }
        }
        else
        {
            NetworkLifecycle.Instance.Server.LogError($"NetworkedItemManager.ProcessReceivedAsHost() NetworkedItem not found! Update Type: {snapshot.UpdateType}, ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
        }
    }

    private bool ValidatePlayerAction(ItemUpdateData snapshot, ServerPlayer player)
    {
        // Must have valid item
        if (!NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem networkedItem))
            return false;

        // Whoever holds it is the only one who may act on it. Nobody holding it is fair game:
        // ownership lives only in the server's memory, so treating unowned as forbidden would
        // strand items no one could ever pick up or put down again.
        GetItemOwner(snapshot.ItemNetId, out ServerPlayer currentOwner);

        if (currentOwner != null && currentOwner != player)
        {
            NetworkLifecycle.Instance.Server.LogWarning($"ValidatePlayerAction() {player.Username} touched item {snapshot.ItemNetId} ({networkedItem.name}, {snapshot.ItemState}) held by {currentOwner.Username}. Refused");
            return false;
        }

        // Distance only on a fresh pickup. Once held, the item is deactivated and its transform
        // stays frozen where it was picked up, so an owner who walked away would fail a check
        // they cannot pass - and could never put the item down again (B5).
        if ((snapshot.ItemState == ItemState.InHand || snapshot.ItemState == ItemState.InInventory)
            && currentOwner == null)
        {
            float distance = Vector3.Distance(player.WorldPosition, networkedItem.transform.position);
            if (distance > MAX_REACH_DISTANCE)
            {
                NetworkLifecycle.Instance.Server.LogWarning($"ValidatePlayerAction() {player.Username} reached item {snapshot.ItemNetId} ({networkedItem.name}) from {distance:F1} m, max {MAX_REACH_DISTANCE:F1} m. Refused");
                return false;
            }
        }

        return true;
    }

    private bool GetItemOwner(ushort itemNetId, out ServerPlayer owner)
    {
        owner = NetworkLifecycle.Instance.Server.ServerPlayers.FirstOrDefault(p => p.OwnsItem(itemNetId));
        return owner != null;
    }
    #endregion

    #region Client

    // The game keeps world items disabled until the player is within reach of their 128 m grid
    // cell (ItemDisablerGrid) and re-activates them itself as the player moves, so the one-time
    // sweep at login can never hold: local copies keep appearing - and reappearing - afterwards.
    // This pass repeats the sweep, returning to the cache anything the server has no id for.
    private const float SUPPRESS_SWEEP_PERIOD = 1f;
    private float lastSuppressSweep;

    private void SuppressLocalWorldItems()
    {
        if (!ClientInitialised || Time.time - lastSuppressSweep < SUPPRESS_SWEEP_PERIOD)
            return;

        lastSuppressSweep = Time.time;

        int suppressed = 0, reHidden = 0;

        foreach (var item in NetworkedItem.GetAll())
        {
            try
            {
                if (item == null || item.NetId != 0 || !item.gameObject.activeSelf)
                    continue;

                // ItemDisabler re-activates items it disabled itself, cached or not - put those back
                if (CachedItemSet.Contains(item))
                {
                    item.gameObject.SetActive(false);
                    reHidden++;
                    continue;
                }

                if (!ShouldSuppressLocalItem(item))
                    continue;

                SendToCache(item);
                suppressed++;
            }
            catch (Exception ex)
            {
                NetworkLifecycle.Instance.Client.LogError($"SuppressLocalWorldItems() {item?.name}: {ex.Message}");
            }
        }

        if (suppressed > 0 || reHidden > 0)
            Multiplayer.Log($"[Diag] Items: sweep suppressed {suppressed} local item(s), re-hid {reHidden} cached item(s) the game re-activated.");
    }

    // Job papers are created and destroyed by the job system on every client, and the player's
    // own gear stays local until inventories are synchronised - neither must be hidden.
    private bool ShouldSuppressLocalItem(NetworkedItem item)
    {
        if (item.Item == null)
            return false;

        if (item.Item.IsEssential() || item.Item.IsGrabbed())
            return false;

        // The sweep is for the world's items, not the player's own. The game marks what belongs
        // to them - starting gear, and anything bought, like a licence off the terminal printer.
        // Those are made here and the server has no id for them, so hiding them would make a
        // licence vanish a second after it printed. A pooled item has this cleared on the way in.
        if (item.Item.InventorySpecs != null && item.Item.InventorySpecs.BelongsToPlayer)
            return false;

        if (StorageController.Instance.StorageInventory.ContainsItem(item.Item))
            return false;

        if (item.GetComponent<JobOverview>() != null || item.GetComponent<JobBooklet>() != null ||
            item.GetComponent<JobReport>() != null || item.GetComponent<JobExpiredReport>() != null ||
            item.GetComponent<JobMissingLicenseReport>() != null)
            return false;

        return true;
    }

    private void ProcessClientChanges(uint tick)
    {
        List<ItemUpdateData> changedItems = new List<ItemUpdateData>();

        if(!ClientInitialised)
            return;

        foreach (var item in NetworkedItem.GetAll())
        {
            // The server never issued an id for this item, so a snapshot could not name it;
            // local-only items stay local until the suppression sweep picks them up.
            if (item.NetId == 0)
                continue;

            ItemUpdateData snapshot = item.GetSnapshot();
            if (snapshot != null)
            {
                changedItems.Add(snapshot);
            }
        }

        if (changedItems.Count > 0)
        {
            NetworkLifecycle.Instance.Client.SendItemsChangePacket(changedItems);
        }
    }

    private void ProcessReceivedAsClient(ItemUpdateData snapshot)
    {
        NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem netItem);

        NetworkLifecycle.Instance.Client.LogDebug(() => $"NetworkedItemManager.ProcessReceivedAsClient() Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
        if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Create)
        {
            //if the item already exists we need to remove it
            if (netItem != null)
                SendToCache(netItem);

            CreateItem(snapshot);
        }
        else if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Destroy)
        {
            SendToCache(netItem);
        }
        else if (netItem != null)
        {
            netItem.ReceiveSnapshot(snapshot);
        }
        else
        {
            NetworkLifecycle.Instance.Client.LogError($"NetworkedItemManager.ProcessReceivedAsClient() NetworkedItem not found on client! Update Type: {snapshot.UpdateType}, ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
        }
    }
    #endregion

    #region Item Cache And Management
    private void CreateItem(ItemUpdateData snapshot)
    {
        if(snapshot == null || snapshot.ItemNetId == 0)
        {
            Multiplayer.LogError($"NetworkedItemManager.CreateItem() Invalid snapshot! ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
            return;
        }

        NetworkedItem newItem = GetFromCache(snapshot.PrefabName);

        if(newItem == null)
        {
            //GameObject prefabObj = Resources.Load(snapshot.PrefabName) as GameObject;
            
            if (!ItemPrefabs.TryGetValue(snapshot.PrefabName, out InventoryItemSpec spec))
            {
                Multiplayer.LogError($"NetworkedItemManager.CreateItem() Unable to load prefab for ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
                return;
            }

            //create a new item
            GameObject gameObject = Instantiate(spec.gameObject, snapshot.ItemPosition + WorldMover.currentMove, snapshot.ItemRotation);

            //Make sure we have a NetworkedItem
            newItem = gameObject.GetOrAddComponent<NetworkedItem>();
        }

        // Id first: anything that reacts to activation must already see a server-owned item,
        // or the suppression sweep could mistake it for a local copy.
        newItem.NetId = snapshot.ItemNetId;
        newItem.gameObject.SetActive(true);

        newItem.ReceiveSnapshot(snapshot);
    }

    private void BuildPrefabLookup()
    {
        NetworkLifecycle.Instance.Client.LogDebug(() => $"BuildPrefabLookup()");

        foreach (var item in Globals.G.Items.items)
        {
            if (!ItemPrefabs.ContainsKey(item.ItemPrefabName))
            {
                ItemPrefabs[item.itemPrefabName] = item;
            }
        }
    }
    public void CacheWorldItems()
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        // Diagnostic: this sweep is supposed to leave the client with no world items of its
        // own, so that the server's are the only ones. In game every player still has their own
        // copy of everything, so it is not doing that. Count what it actually sees.
        int seen = 0, cached = 0, essential = 0, grabbed = 0, inInventory = 0, noItemBase = 0;

        // Remove all spawned world items and place them into a cache for later use
        foreach (var item in NetworkedItem.GetAll())
        {
            try
            {
                seen++;

                if (item.Item == null)
                    noItemBase++;
                else if (item.Item.IsEssential())
                    essential++;
                else if (item.Item.IsGrabbed())
                    grabbed++;
                else if (StorageController.Instance.StorageInventory.ContainsItem(item.Item))
                    inInventory++;

                if (item.Item != null && !item.Item.IsEssential() && !item.Item.IsGrabbed() && !StorageController.Instance.StorageInventory.ContainsItem(item.Item))
                {
                    cached++;
                    SendToCache(item);
                }
                //else
                //{
                //    NetworkLifecycle.Instance.Client.LogDebug(() => $"CacheWorldItems() Not caching: {item.Item.InventorySpecs.previewPrefab} is in Inventory: {StorageController.Instance.StorageInventory.ContainsItem(item.Item)}");
                //}
            }
            catch (Exception ex)
            {
                NetworkLifecycle.Instance.Client.LogError($"Error Caching Spawned Item: {ex.Message}");
            }
        }

        Multiplayer.Log($"[Diag] Items: CacheWorldItems() saw {seen} items, cached {cached}. Skipped: {essential} essential, {grabbed} grabbed, {inInventory} in inventory, {noItemBase} with no ItemBase.");

        ClientInitialised = true;
    }

    // Diagnostic: how many items turn up after the sweep has already run? If the world's items
    // stream in later, the sweep can never have caught them, and every one of these is a local
    // copy the server knows nothing about - which is what duplicate items look like from here.
    private int lateItems;
    private float lastLateReport;

    public void ReportLateItem(ItemBase item)
    {
        if (!ClientInitialised || NetworkLifecycle.Instance.IsHost())
            return;

        lateItems++;

        if (Time.time - lastLateReport < 5f)
            return;

        lastLateReport = Time.time;
        Multiplayer.Log($"[Diag] Items: {lateItems} item(s) have appeared since CacheWorldItems ran. Newest: {item?.InventorySpecs?.itemPrefabName ?? "?"}");
    }

    private NetworkedItem GetFromCache(string prefabName)
    {
        if (CachedItems.TryGetValue(prefabName, out var items) && items.Count > 0)
        {

            var cachedItem = items[items.Count - 1];
            items.RemoveAt(items.Count - 1);
            CachedItemSet.Remove(cachedItem);
            return cachedItem;
        }

        return null;
    }

    private void SendToCache(NetworkedItem netItem)
    {
        if (netItem == null)
            return;

        // Already pooled: just make sure it is hidden, without a second list entry -
        // one instance handed out twice would wear two NetIds at once.
        if (!CachedItemSet.Add(netItem))
        {
            netItem.gameObject.SetActive(false);
            return;
        }

        string prefabName = netItem?.Item?.InventorySpecs?.itemPrefabName;

        //NetworkLifecycle.Instance.Client.LogDebug(() => $"Caching Spawned Item: {prefabName ?? ""}");

        netItem.gameObject.SetActive(false);
        RespawnOnDrop respawn = netItem.Item.GetComponent<RespawnOnDrop>();

        Destroy(respawn);

        //NetworkLifecycle.Instance.Client.LogDebug(() => $"Caching Spawned Item: {prefabName ?? ""}: checkWhileDisabled {respawn.checkWhileDisabled}, ignoreDistanceFromSpawnPosition {respawn.ignoreDistanceFromSpawnPosition}, respawnOnDropThroughFloor {respawn.respawnOnDropThroughFloor}");

        //respawn.checkWhileDisabled = false;
        //respawn.ignoreDistanceFromSpawnPosition = true;
        //respawn.respawnOnDropThroughFloor = false;

        if (SingletonBehaviour<StorageController>.Instance.StorageWorld.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromWorldStorage(netItem.Item);
        }

        if (SingletonBehaviour<StorageController>.Instance.StorageInventory.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromStorageItemList(netItem.Item);
        }

        if (SingletonBehaviour<StorageController>.Instance.StorageLostAndFound.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromStorageItemList(netItem.Item);
        }

        netItem.Item.InventorySpecs.BelongsToPlayer = false;
        netItem.NetId = 0;
        
        if (!CachedItems.ContainsKey(prefabName))
        {
            CachedItems[prefabName] = new List<NetworkedItem>();
        }
        CachedItems[prefabName].Add(netItem);
    }

    #endregion

    public bool DoNotCreateItem(Type itemType)
    {
        if (itemType == null)
            return false;

        if (
            itemType == typeof(JobOverview) ||
            itemType == typeof(JobBooklet) ||
            itemType == typeof(JobReport) ||
            itemType == typeof(JobExpiredReport) ||
            itemType == typeof(JobMissingLicenseReport)
           )
        {
            return true;
        }

            return false;
    }

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedItemManager)}]";
    }
}
