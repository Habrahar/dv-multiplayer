using DV.CabControls;
using DV.InventorySystem;
using DV.Logic.Job;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Networking.Packets.Clientbound.Jobs;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.Jobs;

public class NetworkedJob : IdMonoBehaviour<ushort, NetworkedJob>
{
    #region Lookup Cache

    private static readonly Dictionary<Job, NetworkedJob> jobToNetworkedJob = [];
    private static readonly Dictionary<string, NetworkedJob> jobIdToNetworkedJob = [];
    private static readonly Dictionary<string, Job> jobIdToJob = [];

    public static Dictionary<string, NetworkedJob>.ValueCollection GetAll() => jobIdToNetworkedJob.Values;

    public static bool Get(ushort netId, out NetworkedJob obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedJob> rawObj);
        obj = (NetworkedJob)rawObj;
        return b;
    }

    public static bool TryGetJob(ushort netId, out Job obj)
    {
        bool b = Get(netId, out NetworkedJob networkedJob);
        obj = b ? networkedJob.Job : null;
        return b;
    }

    public static bool TryGetFromJob(Job job, out NetworkedJob networkedJob)
    {
        return jobToNetworkedJob.TryGetValue(job, out networkedJob);
    }

    public static bool TryGetFromJobId(string jobId, out NetworkedJob networkedJob)
    {
        return jobIdToNetworkedJob.TryGetValue(jobId, out networkedJob);
    }

    public static bool TryGetNetId(Job job, out ushort netId)
    {
        if (TryGetFromJob(job, out var networkedJob))
        {
            netId = networkedJob.NetId;
            return true;
        }

        netId = 0;
        return false;
    }

    #endregion

    private static readonly Dictionary<Job, List<Task>> pendingJobTasks = [];

    protected override bool IsIdServerAuthoritative => true;
    public enum DirtyCause
    {
        JobOverview,
        JobBooklet,
        JobReport,
        JobState
    }

    public Job Job { get; private set; }
    public NetworkedStationController Station { get; private set; }

    private NetworkedItem _jobOverview;

    public NetworkedItem JobOverview
    {
        get => _jobOverview;
        set
        {
            if (value != null && value.GetTrackedItem<JobOverview>() == null)
                return;

            _jobOverview = value;

            if (value != null)
            {
                Cause = DirtyCause.JobOverview;
                OnJobDirty?.Invoke(this);
            }
        }
    }

    private NetworkedItem _jobBooklet;

    public NetworkedItem JobBooklet
    {
        get => _jobBooklet;
        set
        {
            if (value != null && value.GetTrackedItem<JobBooklet>() == null)
                return;

            _jobBooklet = value;
            if (value != null)
            {
                Cause = DirtyCause.JobBooklet;
                OnJobDirty?.Invoke(this);
            }
        }
    }
    private NetworkedItem _jobReport;
    public NetworkedItem JobReport
    {
        get => _jobReport;
        set
        {
            if (value != null && value.GetTrackedItem<JobReport>() == null)
                return;

            _jobReport = value;
            if (value != null)
            {
                Cause = DirtyCause.JobReport;
                OnJobDirty?.Invoke(this);
            }
        }
    }

    private readonly List<NetworkedItem> JobReports = [];

    /// <summary>
    /// Server-side: whose job this is, kept for saving. Guid.Empty while nobody has taken it.
    /// </summary>
    public Guid OwnedBy { get; private set; } = Guid.Empty;

    /// <summary>
    /// The owner's player id, which is what goes on the wire: clients only ever ask "is this
    /// mine?", and a session id answers that. 0 means untaken.
    /// </summary>
    public byte OwnerId { get; private set; }

    /// <summary>
    /// Named at the moment the job is handed over, rather than looked up later. The Guid comes
    /// from the player's own settings file, so two installs cloned from one another share it -
    /// searching the player list for a Guid match then finds whoever joined first and quietly
    /// hands them someone else's job.
    /// </summary>
    public void SetOwner(ServerPlayer player)
    {
        OwnedBy = player.Guid;
        OwnerId = player.PlayerId;
    }

    /// <summary>
    /// Names the owner of a job restored from a save, when nobody has connected yet. Only the
    /// Guid survives a restart - a PlayerId is a seat in one session - so the job waits here
    /// with a name and no id until that player turns up, and <see cref="ClaimBy"/> seats them.
    /// </summary>
    public void RestoreOwner(Guid guid)
    {
        OwnedBy = guid;
        OwnerId = 0;
    }

    /// <summary>
    /// Seats the Guid's owner in this session. Covers both ways an owner loses their id: a job
    /// restored from a save has none yet, and a player who reconnects is issued a new one while
    /// the job still names their old seat. Returns false when the job is not theirs.
    /// </summary>
    public bool ClaimBy(ServerPlayer player)
    {
        if (OwnedBy == Guid.Empty || OwnedBy != player.Guid || OwnerId == player.PlayerId)
            return false;

        OwnerId = player.PlayerId;
        player.AddTakenJob(NetId);

        //the owner id is what clients read to ask "is this mine?", so it has to reach them
        MarkStateDirty();

        Multiplayer.Log($"[Diag] Jobs: {Job?.ID} handed back to {player.Username} ({player.TakenJobCount}/{player.AllowedConcurrentJobs} slots)");
        return true;
    }

    public JobValidator JobValidator { get; set; }

    public bool ValidatorRequestSent { get; set; } = false;
    public bool ValidatorResponseReceived { get; set; } = false;
    public bool ValidationAccepted { get; set; } = false;

    /// <summary>Why the server refused, so the validator can print it rather than just beep.</summary>
    public ClientboundJobValidateResponsePacket.RefusalReason RefusalReason { get; set; } = ClientboundJobValidateResponsePacket.RefusalReason.Accepted;
    public ValidationType ValidationType { get; set; }

    public DirtyCause Cause { get; private set; }

    public Action<NetworkedJob> OnJobDirty;

    public List<ushort> JobCars = [];

    private bool tasksInitialized = false;

    protected override void Awake()
    {
        base.Awake();
    }

    protected void Start()
    {
        if (Job != null)
        {
            AddToCache();
        }
        else
        {
            Multiplayer.LogError($"NetworkedJob Start(): Job is null for {gameObject.name}");
        }
    }

    public void Initialize(Job job, NetworkedStationController station)
    {
        Job = job;
        Station = station;

        transform.SetParent(station.transform);

        // Setup handlers
        job.JobTaken += OnJobTaken;
        job.JobAbandoned += OnJobAbandoned;
        job.JobCompleted += OnJobCompleted;
        job.JobExpired += OnJobExpired;

        // If this is called after Start(), we need to add to cache here
        if (gameObject.activeInHierarchy)
        {
            AddToCache();
        }

        // Check for any pending tasks that were added before the NetworkedJob was created
        if (pendingJobTasks.TryGetValue(job, out var taskList) && taskList != null)
        {
            pendingJobTasks.Remove(job);

            //Multiplayer.LogDebug(() => $"NetworkedJob.Initialize(): Found {taskList.Count} pending tasks for jobId {job.ID}");

            foreach (var task in taskList)
                CreateNetworkedTask(task);
        }

        tasksInitialized = true;

        //Multiplayer.LogDebug(() => $"NetworkedJob.Initialize(): Initialized NetworkedJob for jobId {job.ID} with {Job.tasks.Count} tasks");
    }

    private void AddToCache()
    {
        jobToNetworkedJob[Job] = this;
        jobIdToNetworkedJob[Job.ID] = this;
        jobIdToJob[Job.ID] = Job;
        //Multiplayer.Log($"NetworkedJob added to cache: {Job.ID}");
    }

    public static void EnqueueTask(Task task, Job job)
    {
        if (TryGetFromJob(job, out var netJob) || netJob != null && netJob.tasksInitialized)
        {
            Multiplayer.LogDebug(() => $"NetworkedJob.EnqueueTask(): Creating task immediately for jobId {task.Job.ID}");
            netJob.CreateNetworkedTask(task);
            return;
        }

        Multiplayer.LogDebug(() => $"NetworkedJob.EnqueueTask(): Enqueuing task for later creation for jobId {task.Job.ID}");

        if (!pendingJobTasks.TryGetValue(task.Job, out var taskList))
        {
            taskList = [];
            pendingJobTasks[task.Job] = taskList;
        }
        taskList.Add(task);
    }

    public void SetTasksFromServer(Dictionary<ushort, Task> netIdToTask)
    {
        if (netIdToTask == null)
        {
            Multiplayer.LogError($"NetworkedJob.SetTasksFromServer(): netIdToTask is null for jobId {Job?.ID}");
            return;
        }
        
        foreach (var kvp in netIdToTask)
        {
            CreateNetworkedTask(kvp.Value, kvp.Key);
        }
    }

    private void CreateNetworkedTask(Task task, ushort netId = 0)
    {
        if (task == null)
        {
            Multiplayer.LogError($"NetworkedJob.CreateNetworkedTask(): Task is null for jobId {Job?.ID}");
            return;
        }

        NetworkedTask taskObj = new GameObject().AddComponent<NetworkedTask>();
        taskObj.Initialize(task, netId);
        taskObj.name = $"{Job.ID}-{taskObj.NetId}";
        taskObj.transform.SetParent(transform);
    }

    private void OnJobTaken(Job job, bool viaLoadGame)
    {
        if (viaLoadGame)
            return;

        MarkStateDirty();
    }

    /// <summary>
    /// Queues this job's state for broadcast. Taking a job with takenViaLoadGame set stays
    /// deliberately quiet, which is right for a client restoring its own save but wrong when
    /// the server hands a job to a player: nobody would hear that it had gone.
    /// </summary>
    public void MarkStateDirty()
    {
        Cause = DirtyCause.JobState;
        OnJobDirty?.Invoke(this);
    }

    // The owner's job slot frees the moment the job leaves their hands. Host only: ownership
    // and slot counts live on the server. Matched on player id, not Guid - see SetOwner.
    private void ReleaseFromOwner()
    {
        if (OwnerId == 0 || !NetworkLifecycle.Instance.IsHost())
            return;

        if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(OwnerId, out ServerPlayer player))
            player.RemoveTakenJob(NetId);
    }

    private void OnJobAbandoned(Job job)
    {
        ReleaseFromOwner();
        MarkStateDirty();
    }

    private void OnJobCompleted(Job job)
    {
        ReleaseFromOwner();
        MarkStateDirty();
    }

    private void OnJobExpired(Job job)
    {
        ReleaseFromOwner();
        MarkStateDirty();
    }

    public void AddReport(NetworkedItem item)
    {
        if (item == null || !item.UsefulItem)
        {
            Multiplayer.LogError($"Attempted to add a null or uninitialised report: JobId: {Job?.ID}, JobNetID: {NetId}");
            return;
        }

        Type reportType = item.TrackedItemType;
        if (reportType == typeof(JobReport) ||
               reportType == typeof(JobExpiredReport) ||
               reportType == typeof(JobMissingLicenseReport) /*||
               reportType == typeof(Debtre) ||*/
           )
        {
            JobReports.Add(item);
            Cause = DirtyCause.JobReport;
            OnJobDirty?.Invoke(this);
        }
    }

    public void DestroyJobOverview()
    {
        JobOverview joItem = JobOverview?.GetTrackedItem<JobOverview>();

        if (joItem != null)
        {
            var itemBase = joItem.GetComponent<ItemBase>();
            itemBase?.ForceEndInteraction();
            itemBase?.InContainer?.RemoveItem(itemBase.gameObject, false, true);
            Inventory.Instance.DropItemFromHandsOrInventory(itemBase.gameObject);
            joItem.DestroyJobOverview();
        }
    }

    public void DestroyJobBooklet()
    {
        JobBooklet jbItem = JobBooklet?.GetTrackedItem<JobBooklet>();

        if (jbItem != null)
        {
            var itemBase = jbItem.GetComponent<ItemBase>();
            itemBase?.ForceEndInteraction();
            itemBase?.InContainer?.RemoveItem(itemBase.gameObject, false, true);
            Inventory.Instance.DropItemFromHandsOrInventory(itemBase.gameObject);
            jbItem.DestroyJobBooklet();
        }
    }

    public void RemoveReport(NetworkedItem item)
    {

    }

    public void ClearReports()
    {
        foreach (var report in JobReports)
        {
            Destroy(report.gameObject);
        }

        JobReports.Clear();
    }

    protected void OnDisable()
    {
        if (UnloadWatcher.isQuitting || UnloadWatcher.isUnloading)
            return;

        // Remove from lookup caches
        jobToNetworkedJob.Remove(Job);
        jobIdToNetworkedJob.Remove(Job.ID);
        jobIdToJob.Remove(Job.ID);

        // Unsubscribe from events
        Job.JobTaken -= OnJobTaken;
        Job.JobAbandoned -= OnJobAbandoned;
        Job.JobCompleted -= OnJobCompleted;
        Job.JobExpired -= OnJobExpired;

        Destroy(this);
    }

}
