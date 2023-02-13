using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Ceras;
using UnityEngine;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using GPUInstancer;
using Mirror;
public class BaseNObjectManager : NetworkBehaviour
{
    [Header("Settings")]
    public HRItemDatabase HRItemDataBase;
    public GPUInstancerPrefabManager GPUIManager;
    public GPUInstancerPrefab[] prefabs;
    public GameObject[] goRefList;
    //Core Namings
    static string D_FILENAME = "/Default_NObject_Data.sav";
    static string FILENAME = "/NObjectData.sav";
    static string CHUNK_FILENAME = "/SM_NObject_Data.sav"; //This data is static and stays inside StreamingAssets
    static int gridSize = 100000; //Kevin: GridSize can be changed based on the total unitsXunits size of the open-world terrain. Assuminf 100k x 100k terrain
    static int chunkSize = 100;
    BaseNObjectData data;
    BaseNObjectMemberData[] memberDataArray;

    //Procedural real data unload: triggers coroutine after maxDataBufferSize reached to unload real trees when no clients are within interactable distance from trees.
    [Tooltip("Should NObjectManager trigger real object unload process to NObjectData if real data buffer reaches a certain size")]
    public bool useProceduralRealDataUnload = true;
    public int maxRealDataBufferSize = 16;
    int bufferUnloadDistance = 10;
    //Runtime data non-serialized
    List<Transform> clients = new List<Transform>();
    NObjectChunk[,] chunks;
    Dictionary<string, NObjectRuntimeData> runtimeData = new Dictionary<string, NObjectRuntimeData>();


    [Header("Pooling Settings")]
    public GameObject NObjectMemberPrefab; //The runtime pooled collider object which will dynamically allocate to chunks and hold references to actual trees

    //Pooling Settings
    public int poolInitializationBudget = 100; //Start instantiated pool count
    float chunkRefreshRate = 0.3f; //Refreshes client grid every x seconds

    //Runtime Pool data
    [Header("Runtime Debug")]
    bool loadedByteDataFromServer = false;
    public List<BaseNObjectMember> pool = new List<BaseNObjectMember>(100); //Initiaze list with 100 to avoid growing list performance
    public List<NObjectChunk> activeChunks = new List<NObjectChunk>();
    Transform memberParent; //For cleaning hierachy, parenting all spawned members in this

    #region Singleton
    public static BaseNObjectManager Instance { get; private set; }
    private void Awake()
    {
        //Singleton
        if (Instance != null && Instance != this)
        {
            Destroy(this);
        }
        else
        {
            Instance = this;
        }
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
        RPCResyncMemberData(memberIDs);
    }

    //Global Saving Function
    [Server]
    public void SaveWorld()
    {
        //Write all real tree data to NObjectData
        foreach (HRTreeFX c in FindObjectsOfTypeAll(typeof(HRTreeFX)))
        {
            UnloadRealData(c);
        }
        SaveData();

    }
    //Only add localplayer & AI
    public void AddClient(Transform client)
    {
        clients.Add(client);
        RefreshChunks();
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
        if (memberDataArray[memberID].HP == 0 || !memberDataArray[memberID].calculateMatrix) return;

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
    [Command(ignoreAuthority = true)]
    public void OnDamageMember(int memberID, float damage, GameObject instigator, BaseDamageType type)
    {
        BaseNObjectMemberData data = memberDataArray[memberID];
        data.HP -= damage;
        //Replace Prefab
        Replace(memberID, damage, instigator, type);
        RPCRemoveMemberFromMatrixBuffer(memberID);
    }

    [ClientRpc]
    void RPCRemoveMemberFromMatrixBuffer(int memberID)
    {
        Debug.Log("Removed");
        BaseNObjectMemberData data = memberDataArray[memberID];
        NObjectRuntimeData rData = runtimeData[data.objectName];
        data.calculateMatrix = false;

        //Move to static index for buffer unload
        //Refresh index
        //for (int j = data.index; j < rData.membersData.Count; j++)
        //{
        //    rData.membersData[j].index = j;
        //}

        int i;
        GPUInstancerAPI.UpdateVisibilityBufferWithMatrix4x4Array(GPUIManager, GetPrototype(data.objectName, out i), RecalculateMatrixArray(rData));
        ForceReloadChunk(GetChunkByPosition(Matrix4x4FromString(memberDataArray[memberID].matrixData).GetColumn(3)));
    }

    public Matrix4x4[] RecalculateMatrixArray(NObjectRuntimeData runtimeData, int size = -1)
    {
        if(size == -1)
        {
            size = 0;
            foreach (var a in runtimeData.membersData)
            {
                if (a.calculateMatrix && a.HP > 0) size++;
            }
        }

        Matrix4x4[] matrix = new Matrix4x4[size];

        int ptr = 0;
        foreach(var a in runtimeData.membersData)
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
        LoadNObjects();
        InvokeRepeating("RefreshChunks", 0, chunkRefreshRate);
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
        InitialiazeRuntimeData();
        LoadForestChunkData();
        InitializePool();
    }

    [ClientRpc(excludeOwner = true)]
    void RPCLoadFromByteData(byte[] data)
    {
        if (HRNetworkManager.IsHost()) return;
        Debug.Log("Received byte array data from server");
        var ceras = new CerasSerializer();
        var d = ceras.Deserialize<BaseNObjectData>(data);
        memberDataArray = d.data;
        loadedByteDataFromServer = true;
    }
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
    public void InitialiazeRuntimeData()
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

        InitializeMatrixData();
    }

    void InitializeMatrixData()
    {
        foreach (var p in prefabs)
        {
            var d = runtimeData[p.name];
            Debug.Log("Matrix: " + p.name + " " + d.membersData.Count);
            GPUInstancerAPI.InitializeWithMatrix4x4Array(GPUIManager, p.prefabPrototype, RecalculateMatrixArray(d, d.membersData.Count));

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
        replacingObject.GetComponent<BaseDamageReceiver>().ApplyDamage(damage, instigator, damageType);
    }
    #endregion

    #region Load Handling / Disk
    public void LoadForestChunkData()
    {
        var ceras = new CerasSerializer();
        chunks = ceras.Deserialize<NObjectChunkData>(File.ReadAllBytes(Application.streamingAssetsPath + CHUNK_FILENAME)).chunks;
    }
    public void LoadFromDisk()
    {
        Debug.Log("NObjectManager: Loading NObjectData");
        if (File.Exists(Application.persistentDataPath + FILENAME))
        {
            try
            {
                var ceras = new CerasSerializer();
                byte[] byteData = File.ReadAllBytes(Application.persistentDataPath + FILENAME);
                data = ceras.Deserialize<BaseNObjectData>(byteData);
                memberDataArray = data.data;
                RPCLoadFromByteData(byteData);
            }
            catch (Exception e)
            {
                Debug.LogError("NObjectManager: Error loading NObject data, save file may be corrupted or not found.\nException: " + e.Message);
                CreateDefaultSaveOverride();
            }
        }
        else
        {
            CreateDefaultSaveOverride();
        }

    }

    void CreateDefaultSaveOverride()
    {
        byte[] data = File.ReadAllBytes(Application.streamingAssetsPath + D_FILENAME);
        File.WriteAllBytes(Application.persistentDataPath + FILENAME, data);
        Debug.Log("Created default data override");
        LoadFromDisk();
    }

    public void SaveData()
    {
        try
        {
            var ceras = new CerasSerializer();
            var bytes = ceras.Serialize(data);
            File.WriteAllBytes(Application.persistentDataPath + FILENAME, bytes);
            Debug.Log("Saved NObject Data: Success");
        }
        catch (Exception e)
        {
            Debug.LogError("Error Saving Forest Data\n" + e.Message);
        }

    }

    #endregion

    #region Helpers

    public void UnloadRealData(HRTreeFX data, bool triggerDestroy = false)
    {
        memberDataArray[data.MemberID].HP = data.OwningBaseHP.CurrentHP;
        if (triggerDestroy)
        {
            NetworkServer.Destroy(data.gameObject);
        }
    }

    [ClientRpc(excludeOwner = false)]
    void RPCResyncMemberData(List<int> memberIDs)
    {
        foreach(var id in memberIDs)
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