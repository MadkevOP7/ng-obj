using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Ceras;
using UnityEngine;
using Debug = UnityEngine.Debug;
using GPUInstancer;
using Mirror;
using BaseScripts;
using System.Linq;

[ExecuteInEditMode]
public class BaseNObjectManager : NetworkBehaviour
{
    [Header("Settings")]
    public FilterMode filterMode;
    public string[] includeScanNames = { "PF_Tree_CherryBlossom_Tree", "PF_Tree_Bamboo_Thick_Single_Canopy", "PF_Tree_Bamboo_Thick_Grouped_Canopy", "PF_Tree_Oak", "PF_Tree_Tier1_Pine", "PF_Tree_Tier1_Palm" };
    public string[] excludeScanNames = { "PF_Tree_Tier1_Maple01" };
    public int defaultDataVersionNum = 1;
    public HRItemDatabase HRItemDataBase;
    public GameObject SearchRoot;
    public TextAsset DefaultForestData;
    public TextAsset ForestChunkData;
    public GPUInstancerPrefabManager GPUIManager;
    public GPUInstancerPrefab[] prefabs;
    public GameObject[] goRefList;
    //Core Namings
    public string EDITOR_FILENAME(bool bSystemPath) => $"{(bSystemPath ? Application.dataPath : "Assets")}/NObjectData/Default_NObject_Data_{gameObject.scene.name}.bytes"; // For Designer
    public string CHUNK_FILENAME(bool bSystemPath) => $"{(bSystemPath ? Application.dataPath : "Assets")}/NObjectData/SM_NObject_Data_{gameObject.scene.name}.bytes"; //This data is static and stays inside StreamingAssets
    public int gridSize = 100000; //Kevin: GridSize can be changed based on the total unitsXunits size of the open-world terrain. Assuminf 100k x 100k terrain
    public int chunkSize = 100;
    BaseNObjectData data;
    BaseNObjectMemberData[] memberDataArray;

    //Procedural real data unload: triggers coroutine after maxDataBufferSize reached to unload real trees when no clients are within interactable distance from trees.
    [Tooltip("Should NObjectManager trigger real object unload process to NObjectData if real data buffer reaches a certain size")]
    public bool useProceduralRealDataUnload = true;
    public int maxRealDataBufferSize = 16;
    public int ByteDataPacketSegCount = 12; //Send Server data to clients in separate packets
    public int PacketNetworkTimeout = 100; //overall timeout
    public int PacketDeltaResync = 6; //seconds between when packet receving should check for lost packets
    int PacketNetworkTimer;
    int packetReceivedDelta;
    int bufferUnloadDistance = 10;
    //Runtime data non-serialized
    List<Transform> clients = new List<Transform>();
    NObjectChunk[,] chunks;
    Dictionary<string, NObjectRuntimeData> runtimeData = new Dictionary<string, NObjectRuntimeData>();
    BasePawn clientPawn;

    [Header("Pooling Settings")]
    public GameObject NObjectMemberPrefab; //The runtime pooled collider object which will dynamically allocate to chunks and hold references to actual trees

    //Pooling Settings
    public int poolInitializationBudget = 100; //Start instantiated pool count
    float chunkRefreshRate = 0.3f; //Refreshes client grid every x seconds

    //Runtime Pool data
    [Header("Runtime Debug")]
    bool loadedByteDataFromServer = false;
    bool receivedByteDataFromServer = false;
    public List<BaseNObjectMember> pool = new List<BaseNObjectMember>(100); //Initiaze list with 100 to avoid growing list performance
    public List<NObjectChunk> activeChunks = new List<NObjectChunk>();
    Transform memberParent; //For cleaning hierachy, parenting all spawned members in this

    bool bDataLoaded = false;
    bool bPendingRefreshChunk = false;
    byte[] LoadingData = null;
    List<byte[]> ClientByteArrayCache;
    //Network Receive data
    int ExpectPacketCount = -1;
    int ReceivedPacketCount = 0;

    public HashSet<HRTreeFX> AllTreeFX = new HashSet<HRTreeFX>();
    CerasSerializer ceras;

    #region Property
    public enum FilterMode
    {
        inclusive,
        exclusive,
        off
    }

    #endregion

    #region Singleton
    public static BaseNObjectManager Instance { get; private set; }
    private void Awake()
    {
        //Singleton
        if (Instance != null && Instance != this)
        {
            Destroy(Instance);
        }

        Instance = this;

        if (GPUIManager)
        {
            GPUIManager.enabled = true;
        }

        ceras = new CerasSerializer();
    }
    #endregion

    #region Core

    public void ForceAllocateMember(int memberID)
    {
        var chunk = GetChunkByPosition(Matrix4x4FromString(memberDataArray[memberID].matrixData).GetColumn(3));
        AllocateMemberToChunk(chunk, memberID);
    }

    [Server]
    public void UnloadRealDataBuffer(NObjectRuntimeData data)
    {
        List<int> memberIDs = new List<int>();
        for (int i = data.realDataBuffer.Length - 1; i >= 0; --i)
        {
            memberIDs.Add(data.realDataBuffer[i].MemberID);
            UnloadRealData(data.realDataBuffer[i], true);
        }

        data.rPointer = 0;
        ResyncMemberData_ClientRpc(memberIDs);
    }

    //Global Saving Function
    public void SaveWorld(HRSaveFile SavingFile)
    {
        if (SavingFile == null || HRSaveSystem.bRunningAutoSave || SavingFile != HRSaveSystem.Get.CurrentFileInstance) return;

        //Write all real tree data to NObjectData
        foreach (HRTreeFX c in AllTreeFX)
        {
            if (c == null) continue;
            UnloadRealData(c);
        }
        SavePlayerRuntimeData();
    }
    //Only add localplayer & AI
    public void AddClient(BasePawn ClientPawn)
    {
        clients.Add(ClientPawn.transform);
        //Only add local player
        clientPawn = ClientPawn;
        if (bDataLoaded)
        {
            RefreshChunks();
        }
        else
        {
            bPendingRefreshChunk = true;
        }

        if (!HRNetworkManager.IsHost())
        {
            RequestByteData_Command(ClientPawn);
            StartCoroutine(AsyncWaitForServerData());
        }
    }

    //Called at interval by refresh rate
    public void RefreshChunks()
    {
        if (chunks == null) return;
        if (clients.Count == 0) return;
        List<NObjectChunk> activationQueue = new List<NObjectChunk>();
        for (int i = clients.Count - 1; i >= 0; --i)
        {
            //Null Check
            if (clients[i] == null)
            {
                clients.RemoveAt(i);
                continue;
            }
            activationQueue.Add(GetChunkByPosition(clients[i].transform.position));
        }

        //Deactivate chunks in the old loaded list that's not in the new queue
        foreach (var c in activeChunks)
        {
            if (!IsInList(c, activationQueue))
            {
                UnLoadChunk(c);
            }
        }

        activeChunks = activationQueue;

        foreach (var c in activeChunks)
        {
            LoadChunk(c);
        }

    }
    public void LoadChunk(NObjectChunk chunk)
    {
        if (chunk == null || chunk.isAllocated) return;
        chunk.isAllocated = true;

        foreach (var t in chunk.nObjectIDs)
        {
            AllocateMemberToChunk(chunk, t);
        }

        //Debug.Log("Loaded Chunk: " + chunk.chunkID);
    }

    public void DeAllocateMemberFromChunk(NObjectChunk chunk, int allocatedIndex)
    {
        pool.Add(chunk.allocatedMembers[allocatedIndex]);
        chunk.allocatedMembers[allocatedIndex].DeAllocateMember();
        chunk.allocatedMembers.RemoveAt(allocatedIndex);
    }
    public void AllocateMemberToChunk(NObjectChunk chunk, int memberID)
    {
        if (memberDataArray == null || memberDataArray[memberID] == null || memberDataArray[memberID].HP == 0 || !memberDataArray[memberID].calculateMatrix) return;

        if (pool.Count == 0)
        {
            pool.Add(Instantiate(NObjectMemberPrefab, memberParent).GetComponent<BaseNObjectMember>());
        }
        chunk.allocatedMembers.Add(pool[pool.Count - 1]);
        pool[pool.Count - 1].InitializeMember(Matrix4x4FromString(memberDataArray[memberID].matrixData), memberID, chunk.allocatedMembers.Count - 1, runtimeData[memberDataArray[memberID].objectName]);
        pool.RemoveAt(pool.Count - 1);
    }
    public void UnLoadChunk(NObjectChunk chunk)
    {
        if (chunk == null || !chunk.isAllocated) return;
        chunk.isAllocated = false;
        for (int i = chunk.allocatedMembers.Count - 1; i >= 0; --i)
        {
            DeAllocateMemberFromChunk(chunk, i);
        }

        //Debug.Log("Unloaded Chunk: " + chunk.chunkID);

    }

    //Used for refreshing members data after tree destroyed
    public void ForceReloadChunk(NObjectChunk chunk)
    {
        UnLoadChunk(chunk);
        LoadChunk(chunk);
    }
    #endregion

    #region Core Interactions
    public void DamageMember(int memberID, float damage, GameObject instigator, BaseDamageType type)
    {
        if (HRNetworkManager.IsHost())
        {
            DamageMember_Server(memberID, damage, instigator, type);
        }
        else
        {
            DamageMember_Command(memberID, damage, instigator, type);
        }
    }

    [Command(ignoreAuthority = true)]
    void DamageMember_Command(int memberID, float damage, GameObject instigator, BaseDamageType type)
    {
        DamageMember_Server(memberID, damage, instigator, type);
    }

    void DamageMember_Server(int memberID, float damage, GameObject instigator, BaseDamageType type)
    {
        BaseNObjectMemberData data = memberDataArray[memberID];
        data.HP -= damage;
        //Replace Prefab
        Replace(memberID, damage, instigator, type);

        RemoveMemberFromMatrixBuffer_Implementation(memberID);
        RemoveMemberFromMatrixBuffer_ClientRpc(memberID);
    }

    [ClientRpc]
    void RemoveMemberFromMatrixBuffer_ClientRpc(int memberID)
    {
        if (!HRNetworkManager.IsHost())
        {
            RemoveMemberFromMatrixBuffer_Implementation(memberID);
        }
    }

    List<int> PendingRemoveMembers = new List<int>();

    void RemoveMemberFromMatrixBuffer_Implementation(int memberID)
    {
        if (memberDataArray == null)
        {
            PendingRemoveMembers.Add(memberID);
            return;
        }

        Debug.Log("Removed " + memberID);
        BaseNObjectMemberData data = memberDataArray[memberID];
        NObjectRuntimeData rData;
        if (!runtimeData.TryGetValue(data.objectName, out rData)) return;
        data.calculateMatrix = false;

        int i;
        GPUInstancerAPI.UpdateVisibilityBufferWithMatrix4x4Array(GPUIManager, GetPrototype(data.objectName, out i), RecalculateMatrixArray(rData));
        ForceReloadChunk(GetChunkByPosition(Matrix4x4FromString(memberDataArray[memberID].matrixData).GetColumn(3)));
    }

    public Matrix4x4[] RecalculateMatrixArray(NObjectRuntimeData runtimeData, int size = -1)
    {
        if (size == -1)
        {
            size = 0;
            foreach (var a in runtimeData.membersData)
            {
                if (a.calculateMatrix && a.HP > 0) size++;
            }
        }

        Matrix4x4[] matrix = new Matrix4x4[size];

        int ptr = 0;
        foreach (var a in runtimeData.membersData)
        {
            if (!a.calculateMatrix || a.HP <= 0) continue;
            matrix[ptr++] = Matrix4x4FromString(a.matrixData);
        }
        return matrix;
    }
    #endregion

    #region Initialization

    //Main Loading function to call to load data
    private void Start()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        HRSaveSystem.OnSaveToFileDelegate += SaveWorld;

        LoadNObjects();
        InvokeRepeating("RefreshChunks", 0, chunkRefreshRate);
    }

    private void OnDestroy()
    {
        HRSaveSystem.OnSaveToFileDelegate -= SaveWorld;
    }

    //Main Function to call to load forest
    public void LoadNObjects()
    {
        StartCoroutine(LoadNObjectsAsync());
    }

    IEnumerator LoadNObjectsAsync()
    {
        if (HRNetworkManager.IsHost())
        {
            LoadFromDisk();
        }
        else
        {
            while (!loadedByteDataFromServer)
            {
                yield return null;
                //Wait for Data
            }
        }
        InitializeRuntimeData();
        LoadForestChunkData();
        InitializePool();

        bDataLoaded = true;

        if (bPendingRefreshChunk)
        {
            RefreshChunks();
        }
    }
    #region Network Data Sync
    void LoadNObjectPacketData()
    {
        Debug.Log("[NObject] Loading NObject Packet Data");
        byte[] byteData = CombineByteData(ClientByteArrayCache);

        data = ceras.Deserialize<BaseNObjectData>(byteData);
        memberDataArray = data.data;
        loadedByteDataFromServer = true;

        if (PendingRemoveMembers.Count > 0)
        {
            for (int i = 0; i < PendingRemoveMembers.Count; i++)
            {
                RemoveMemberFromMatrixBuffer_Implementation(PendingRemoveMembers[i]);
            }
        }
    }

    private byte[] CombineByteData(List<byte[]> arrays)
    {
        byte[] cache = new byte[arrays.Sum(a => a.Length)];
        int offset = 0;
        foreach (byte[] array in arrays)
        {
            System.Buffer.BlockCopy(array, 0, cache, offset, array.Length);
            offset += array.Length;
        }
        return cache;
    }

    IEnumerator AsyncWaitForServerData()
    {
        while (PacketNetworkTimer < PacketNetworkTimeout)
        {
            if (!receivedByteDataFromServer && ExpectPacketCount >= 0 && ReceivedPacketCount == ExpectPacketCount)
            {
                receivedByteDataFromServer = true;
                Debug.Log("[NObject] All packet data received");
                LoadNObjectPacketData();
                yield break;
            }

            if (packetReceivedDelta > PacketDeltaResync)
            {
                List<int> resync = new List<int>();
                //Resync lost data
                for (int i = 0; i < ExpectPacketCount; i++)
                {
                    if (ClientByteArrayCache[i] == null || ClientByteArrayCache[i].Length <= 4)
                    {
                        resync.Add(i);
                    }
                }

                foreach (var id in resync)
                {
                    RequestByteDataNum_Command(clientPawn, id);
                    yield return new WaitForSeconds(1);
                }
                packetReceivedDelta = 0;
            }

            yield return new WaitForSeconds(1);
            PacketNetworkTimer++;
            packetReceivedDelta++;
        }
        if (!receivedByteDataFromServer)
        {
            Debug.LogError("[NObject] Network data receiver timed out!");
        }
    }

    [Command(ignoreAuthority = true)]
    public void RequestByteDataNum_Command(BasePawn ClientPawn, int packetNum)
    {
        List<byte[]> byteData = SplitByteData(ceras.Serialize(data), ByteDataPacketSegCount);
        SendByteData_TargetRpc(ClientPawn.connectionToClient, byteData[packetNum], packetNum, byteData.Count);
    }

    [Command(ignoreAuthority = true)]
    public void RequestByteData_Command(BasePawn ClientPawn)
    {
        StartCoroutine(ServerAsyncSendByteData(ClientPawn));
    }

    IEnumerator ServerAsyncSendByteData(BasePawn ClientPawn)
    {
        // Yiming: Needs to Re-serialize current data here before sending to client
        // Otherwise if server chopped down trees and then client joins, client will still see this chopped down tree
        List<byte[]> byteData = SplitByteData(ceras.Serialize(data), ByteDataPacketSegCount);
        for (int i = 0; i < byteData.Count; i++)
        {
            SendByteData_TargetRpc(ClientPawn.connectionToClient, byteData[i], i, byteData.Count);
            yield return new WaitForSeconds(0.3f);
        }
    }

    [TargetRpc]
    void SendByteData_TargetRpc(NetworkConnection conn, byte[] data, int packetNum, int expCount)
    {
        if (HRNetworkManager.IsHost()) return;

        if (ClientByteArrayCache == null)
        {
            ExpectPacketCount = expCount;
            ClientByteArrayCache = new List<byte[]>();
            ClientByteArrayCache.AddRange(Enumerable.Repeat(default(byte[]), expCount));
        }

        ClientByteArrayCache[packetNum] = data;
        ReceivedPacketCount++;
        packetReceivedDelta = 0; //Kevin: Reset after each packet received
        //PacketNetworkTimer = 0; //Keep for global time out thus no reset here.
        Debug.Log("[NObject] Received byte packet #" + packetNum);

        if (!receivedByteDataFromServer && ExpectPacketCount >= 0 && ReceivedPacketCount == ExpectPacketCount)
        {
            receivedByteDataFromServer = true;
            Debug.Log("[NObject] All packet data received");
            LoadNObjectPacketData();
        }
    }
    public static byte[] GetSplitByteData(byte[] data, int index, int count)
    {
        int PacketSize = data.Length / count + 1;

        int Offset = PacketSize * index;

        if (Offset + PacketSize > data.Length)
        {
            PacketSize = data.Length - Offset;
        }
        Debug.Log($"Packet Size {PacketSize}, Data Length {data.Length}, index {index}, count {count}");
        byte[] slice = new byte[PacketSize];
        Array.Copy(data, Offset, slice, 0, PacketSize);
        return slice;
    }

    public static List<byte[]> SplitByteData(byte[] data, int count)
    {
        List<byte[]> d = new List<byte[]>();
        for (var i = 0; i < count; i++)
            d.Add(GetSplitByteData(data, i, count));
        return d;
    }

    #endregion
    public void InitializePool()
    {
        memberParent = new GameObject("====RUN TIME Member Parent====").transform;
        for (int i = 0; i < poolInitializationBudget; i++)
        {
            BaseNObjectMember member = Instantiate(NObjectMemberPrefab, memberParent).GetComponent<BaseNObjectMember>();
            pool.Add(member);
            member.DeAllocateMember();
        }
        Debug.Log("Pool initialized with count: " + pool.Count);
    }

    public void InitializeRuntimeData()
    {
        foreach (var p in prefabs)
        {
            NObjectRuntimeData data = new NObjectRuntimeData();

            //Fill in transform data
            BoxCollider collider = p.GetComponentInChildren<BoxCollider>();
            data.center = collider.center;
            data.size = collider.size;
            runtimeData.Add(p.name, data);
        }

        if (memberDataArray != null)
        {
            foreach (var tData in memberDataArray)
            {
                if (tData.HP == 0) continue;
                NObjectRuntimeData rdata;
                if (!runtimeData.TryGetValue(tData.objectName, out rdata))
                {
                    Debug.Log("Error Getting RuntimeData for " + tData.objectName);
                }
                tData.calculateMatrix = true;
                rdata.membersData.Add(tData);
                tData.index = rdata.curPointer;
                rdata.curPointer++;
            }
        }
        else
        {
            Debug.Log("memberDataArray is null in BaseNObjectManager.InitializeRuntimeData.");
        }

        InitializeMatrixData();
    }

    void InitializeMatrixData()
    {
        foreach (var p in prefabs)
        {
            var d = runtimeData[p.name];
            if (d.membersData.Count > 0)
            {
                Debug.Log("Matrix: " + p.name + " " + d.membersData.Count);
                GPUInstancerAPI.InitializeWithMatrix4x4Array(GPUIManager, p.prefabPrototype, RecalculateMatrixArray(d, d.membersData.Count));
            }
        }
    }

    #endregion

    #region Replacement
    [Server]
    public void Replace(int memberID, float damage, GameObject instigator, BaseDamageType damageType)
    {
        BaseNObjectMemberData data = memberDataArray[memberID];
        Matrix4x4 mData = Matrix4x4FromString(data.matrixData);
        GameObject replacingObject = Instantiate(GetReplacementObject(data.objectName), memberParent);
        SetTransformFromMatrixData(replacingObject.transform, mData);

        replacingObject.GetComponent<NetworkIdentity>().useWorldPos = true;
        var r = replacingObject.GetComponent<HRTreeFX>();
        r.MemberID = memberID;
        AddRealDataToBuffer(runtimeData[data.objectName], r);
        NetworkServer.Spawn(replacingObject);
        StartCoroutine(DelayedSyncBaseHP(replacingObject, damage, instigator, damageType));
    }

    [Server]
    void AddRealDataToBuffer(NObjectRuntimeData runtimeData, HRTreeFX data)
    {
        if (!useProceduralRealDataUnload) return;

        if (runtimeData.realDataBuffer == null)
        {
            runtimeData.realDataBuffer = new HRTreeFX[maxRealDataBufferSize];
        }

        if (runtimeData.rPointer >= maxRealDataBufferSize)
        {
            UnloadRealDataBuffer(runtimeData);
        }
        runtimeData.realDataBuffer[runtimeData.rPointer++] = data;
    }
    IEnumerator DelayedSyncBaseHP(GameObject replacingObject, float damage, GameObject instigator, BaseDamageType damageType)
    {
        yield return new WaitForSeconds((1f / NetworkManager.singleton.serverTickRate));

        if(replacingObject)
        {
            replacingObject.GetComponent<BaseDamageReceiver>().ApplyDamage(damage, instigator, damageType);
        }
    }
    #endregion

    #region Load Handling / Disk
    public void LoadForestChunkData()
    {
        if (ForestChunkData == null) return;

        chunks = ceras.Deserialize<NObjectChunkData>(ForestChunkData.bytes).chunks;
    }
    public void LoadFromDisk()
    {
        Debug.Log("NObjectManager: Loading NObjectData");


        bool bHasPlayerData = HRSaveSystem.Get && HRSaveSystem.Get.CurrentFileInstance != null && HRSaveSystem.Get.CurrentFileInstance.Size > 0;

        if (bHasPlayerData)
        {
            LoadingData = HRSaveSystem.Get.CurrentFileInstance.Load<byte[]>("NObjectData");
        }

        bHasPlayerData = LoadingData != null && LoadingData.Length > 0;

        if (!bHasPlayerData && DefaultForestData)
        {
            LoadingData = DefaultForestData.bytes;
        }

        if (LoadingData != null)
        {
            try
            {
                data = ceras.Deserialize<BaseNObjectData>(LoadingData);

                if (!bHasPlayerData)
                {
                    Debug.Log("Player save data file is not found, created new one");
                    SavePlayerRuntimeData();
                }

                if (defaultDataVersionNum > data.versionNumber)
                {
                    AutoDiffAndUpgradeData();
                }
                memberDataArray = data.data;
                //RPCLoadFromByteData(LoadingData); Client requests from server to prevent missed sync for late joins
            }
            catch (Exception e)
            {
                Debug.LogError("NObjectManager: Error loading NObject data, save file may be corrupted or not found.\nException: " + e.Message);
            }
        }
    }

    //Saves new default data as root data, but writes existing changes to the default data
    void AutoDiffAndUpgradeData()
    {
        var defaultData = ceras.Deserialize<BaseNObjectData>(DefaultForestData.bytes);
        foreach (var m in defaultData.data)
        {
            ScanAndUpdateDataEntry(m, defaultData);
        }

        data = defaultData;
        memberDataArray = data.data;
        data.versionNumber = defaultDataVersionNum;
        SavePlayerRuntimeData();
    }

    void ScanAndUpdateDataEntry(BaseNObjectMemberData member, BaseNObjectData defaultData)
    {
        foreach (var d in defaultData.data)
        {
            if (member.objectName == d.objectName)
            {
                Vector3 memberPosition = Matrix4x4FromString(member.matrixData).GetColumn(3);
                Vector3 dataPosition = Matrix4x4FromString(d.matrixData).GetColumn(3);
                if (memberPosition.Equals(dataPosition))
                {
                    Debug.Log("Updating data entry: " + d.objectName);
                    d.HP = member.HP;
                }
            }
        }
    }

    public void SavePlayerRuntimeData()
    {
        if (data != null && HRSaveSystem.Get && HRSaveSystem.Get.CurrentFileInstance != null && !HRSaveSystem.bRunningAutoSave)
        {
            data.versionNumber = defaultDataVersionNum;

            try
            {
                byte[] Data = ceras.Serialize(data);

                HRSaveSystem.Get.CurrentFileInstance.Save<byte[]>("NObjectData", Data);

                Debug.Log("Saved NObject Data: Success");
            }
            catch (Exception e)
            {
                Debug.LogError("NObjectManager: Error Saving Runtime Data.\nException: " + e.Message);
            }
        }
    }

    public BaseNObjectData GetPlayerRuntimeData()
    {
        data.versionNumber = defaultDataVersionNum;

        return data;
    }

    #endregion

    #region Helpers

    public void UnloadRealData(HRTreeFX data, bool triggerDestroy = false)
    {
        if (!data || !data.OwningBaseHP || memberDataArray == null || data.MemberID < 0 || data.MemberID >= memberDataArray.Length) return;

        memberDataArray[data.MemberID].HP = data.OwningBaseHP.CurrentHP;

        Debug.Log("[NObject] Unloaded: " + data.MemberID);

        if (triggerDestroy)
        {
            NetworkServer.Destroy(data.gameObject);
        }
    }

    [ClientRpc(excludeOwner = false)]
    void ResyncMemberData_ClientRpc(List<int> memberIDs)
    {
        foreach (var id in memberIDs)
        {
            memberDataArray[id].calculateMatrix = true;
            ForceAllocateMember(id);
        }

        int i;
        GPUInstancerAPI.UpdateVisibilityBufferWithMatrix4x4Array(GPUIManager, GetPrototype(memberDataArray[0].objectName, out i), RecalculateMatrixArray(runtimeData[memberDataArray[0].objectName]));
        Debug.Log("Resynced Member Data");
    }
    public void SetTransformFromMatrixData(Transform t, Matrix4x4 matrix)
    {
        t.position = matrix.GetColumn(3);
        t.localScale = new Vector3(
                            matrix.GetColumn(0).magnitude,
                            matrix.GetColumn(1).magnitude,
                            matrix.GetColumn(2).magnitude
                            );
        t.rotation = Quaternion.LookRotation(matrix.GetColumn(2), matrix.GetColumn(1));
    }
    public GPUInstancerPrefabPrototype GetPrototype(string name, out int index)
    {
        for (int i = 0; i < prefabs.Length; i++)
        {
            if (prefabs[i].name == name)
            {
                index = i;
                return prefabs[i].prefabPrototype;
            }
        }
        index = -1;
        return null;
    }
    public bool IsInList(NObjectChunk chunk, List<NObjectChunk> list)
    {
        foreach (var t in list)
        {
            if (t == chunk)
            {
                return true;
            }
        }
        return false;
    }
    //Returns corresponding chunk by position
    public NObjectChunk GetChunkByPosition(Vector3 position)
    {
        return chunks[GetXCellIndex(position.x), GetZCellIndex(position.z)];
    }
    //Returns the index of cell in the [,] grid given a x position worldpoint
    public int GetXCellIndex(float x)
    {
        return Mathf.FloorToInt((gridSize / chunkSize) - Mathf.Abs(x - gridSize / 2) / (float)chunkSize);
    }

    //Returns the index of cell in the [,] grid given a z position worldpoint
    public int GetZCellIndex(float z)
    {
        return Mathf.FloorToInt((gridSize / chunkSize) - Mathf.Abs(z - gridSize / 2) / (float)chunkSize);
    }
    public Matrix4x4 Matrix4x4FromString(string matrixStr)
    {
        Matrix4x4 matrix4x4 = new Matrix4x4();
        string[] floatStrArray = matrixStr.Split(';');
        for (int i = 0; i < floatStrArray.Length; i++)
        {
            matrix4x4[i / 4, i % 4] = float.Parse(floatStrArray[i], System.Globalization.CultureInfo.InvariantCulture);
        }
        return matrix4x4;
    }

    public GameObject GetReplacementObject(string name)
    {
        foreach (var g in goRefList)
        {
            if (g.name == name)
            {
                return g;
            }
        }

        Debug.LogError("Replacement Prefab for " + name + " isn't found in goRefList. Try Rebuild references or regenerate NObject data");
        return null;
    }
    #endregion
}


#region Runtime Data
public class NObjectRuntimeData
{
    public HRTreeFX[] realDataBuffer;
    public int rPointer = 0;
    public int curPointer = 0;
    //Holds the list of objects that need to be rendered with matrix4x4 calculation.
    public List<BaseNObjectMemberData> membersData = new List<BaseNObjectMemberData>();

    //Transform collider data
    public Vector3 center;
    public Vector3 size;
}


[Serializable]
public class NObjectChunk
{
    [NonSerialized]
    public bool isAllocated;

    //Runtime
    [NonSerialized]
    public List<BaseNObjectMember> allocatedMembers = new List<BaseNObjectMember>();
    public static bool operator ==(NObjectChunk lhs, NObjectChunk rhs)
    {
        if (lhs is null)
        {
            if (rhs is null) return true;
            return false;
        }
        if (rhs is null) return false;
        if (lhs.chunkID == rhs.chunkID) return true;
        return false;
    }

    public static bool operator !=(NObjectChunk lhs, NObjectChunk rhs) => !(lhs == rhs);

    [SerializeField]
    public int chunkID;

    [SerializeReference]
    public List<int> nObjectIDs = new List<int>();

    //[SerializeReference]
    //public List<int> activateChunks = new List<int>(); //Stores chunk IDs for activation pattern
}

[Serializable]
public class NObjectChunkData
{
    [SerializeReference]
    public NObjectChunk[,] chunks;
}
#endregion