//© 2022 by MADKEV Studio, all rights reserved

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Ceras;
using UnityEngine;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using GPUInstancer;
using Mirror;
using System.Linq;

public class ForestManager : NetworkBehaviour
{
    [Header("Data")]
    public GPUInstancerPrefabManager GPUIManager;
    public GPUInstancerPrefab[] prefabs;
    public GameObject[] glitchPrefabs;
    //Core data
    static string D_FILENAME = "/Default_Forest_Data.sav";
    static string FILENAME = "/Forest_Data.sav";
    static string CHUNK_FILENAME = "/SM_Forest_Chunk_Data.sav";
    static int gridSize = 7000;
    static int chunkSize = 100;
    ForestData data;
    ForestTreeData[] treeData;

    //Runtime data non-serialized
    List<Transform> clients = new List<Transform>();
    ForestChunk[,] chunks;
    Dictionary<string, ForestRuntimeData> runtimeData = new Dictionary<string, ForestRuntimeData>();
    [Header("Pooling Settings")]
    public GameObject treeMemberPrefab;
    //Pooling Settings
    public int poolInitializationBudget = 100;
    float chunkRefreshRate = 0.3f;
    //Runtime Pool data
    [Header("Runtime Debug")]
    public List<ForestPoolMember> pool = new List<ForestPoolMember>(100);
    public List<ForestChunk> activeChunks = new List<ForestChunk>();
    Transform memberParent;


    //Network Data
    int dataReceivingProgress = 0;
    bool dataLoaded; //If everything has been loaded to start chunk refresh
    int networkPacketPtr = 0; //Pointing to current received client packet index (client side)
    int receiverDelta = 0;
    int packetDelta = 0;
    int timeOutDelta = 0;
    List<byte[]> serverSplitedData; //Lives on server
    List<byte[]> clientByteArrayCache; //Lives on clients
    int maxPacketSize = 40080; //Val = 40080 Computed by worst case (fresh forest data size in bytes / 100)
    bool receiverInitialized; //Received back updated expected packet count from server, if time out connection fail
    int expectedPacketCount = -1;
    int receivedPacketCount = 0;
    bool diffComputed = false; //Server, set to true after diff fininshes
    int timeOut = 60; //60 seconds timeout
    int packetResync = 1; //Resync a packet if not received after 5 seconds
    byte[] diffedForestData; //Computed on server to send changed tree data to clients
    #region Singleton
    public static ForestManager Instance { get; private set; }
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

    //Only add localplayer & AI
    public void AddClient(Transform client)
    {
        clients.Add(client);
        RefreshChunks();
    }

    //Called at interval by refresh rate
    public void RefreshChunks()
    {
        if (clients.Count == 0 || !dataLoaded) return;
        List<ForestChunk> activationQueue = new List<ForestChunk>();
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
    public void LoadChunk(ForestChunk chunk)
    {
        if (chunk.isAllocated) return;
        chunk.isAllocated = true;

        chunk.allocatedTrees = new List<ForestPoolMember>();
        foreach (var t in chunk.forestTreeIDs)
        {
            if (treeData[t].health == 0) continue;

            if (pool.Count == 0)
            {
                pool.Add(Instantiate(treeMemberPrefab, memberParent).GetComponent<ForestPoolMember>());
            }
            pool[pool.Count - 1].InitializeMember(Matrix4x4FromString(treeData[t].matrixData), t, runtimeData[treeData[t].treeName]);
            chunk.allocatedTrees.Add(pool[pool.Count - 1]);
            pool.RemoveAt(pool.Count - 1);
        }

        //Debug.Log("Loaded Chunk: " + chunk.chunkID);
    }

    public void UnLoadChunk(ForestChunk chunk)
    {
        if (!chunk.isAllocated) return;
        chunk.isAllocated = false;

        for (int i = chunk.allocatedTrees.Count - 1; i >= 0; --i)
        {
            pool.Add(chunk.allocatedTrees[i]);
            chunk.allocatedTrees[i].DeAllocateMember();
            chunk.allocatedTrees.RemoveAt(i);
        }

        chunk.allocatedTrees = null;
        //Debug.Log("Unloaded Chunk: " + chunk.chunkID);

    }

    //Used for refreshing members data after tree destroyed
    public void ForceReloadChunk(ForestChunk chunk)
    {
        UnLoadChunk(chunk);
        LoadChunk(chunk);
    }
    #endregion

    #region Core Interactions
    [Command(requiresAuthority = false)]
    public void OnDamageTree(int treeID, int damage)
    {
        ForestTreeData data = treeData[treeID];
        data.health -= damage;
        if (data.health <= 0)
        {
            DestroyTree(treeID);
        }
    }

    [ClientRpc]
    void DestroyTree(int treeID)
    {
        ForestTreeData data = treeData[treeID];
        data.health = 0;
        ForestRuntimeData rData = runtimeData[data.treeName];
        rData.treesData.RemoveAt(data.index);
        Matrix4x4 mData = rData.matrixData[data.index];
        rData.matrixData.RemoveAt(data.index);

        //Refresh index
        for (int j = data.index; j < rData.treesData.Count; j++)
        {
            rData.treesData[j].index = j;
        }

        int i;
        GPUInstancerAPI.UpdateVisibilityBufferWithMatrix4x4Array(GPUIManager, GetPrototype(data.treeName, out i), rData.matrixData.ToArray());
        SetTransformFromMatrixData(Instantiate(glitchPrefabs[i]).transform, mData);
        ForceReloadChunk(GetChunkByPosition(Matrix4x4FromString(treeData[treeID].matrixData).GetColumn(3)));
    }
    #endregion

    #region Initialization

    //Main Loading function to call to load data
    private void Start()
    {
        LoadForest();
        InvokeRepeating("RefreshChunks", 0, chunkRefreshRate);
    }

    //Main Function to call to load forest
    public void LoadForest()
    {
        StartCoroutine(LoadForestAsync());
    }


    #region Byte handling Split/Reconstruct
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

    #region Callbacks (errors)
    void OnRequestTimedOut()
    {
        //TODO
        //Close connection and back to lobby
        Debug.Log("Request timed out, should disconnect and go to lobby");
    }
    #endregion
    #endregion

    void ClientLoadData()
    {
        byte[] byteData = CombineByteData(clientByteArrayCache);

        var ceras = new CerasSerializer();
        var networkData = ceras.Deserialize<ForestNetworkData>(Decompress(byteData));

        //Set Manager data to base default data, then run diff upgrade with received network packets data
        data = ceras.Deserialize<ForestData>(File.ReadAllBytes(Application.streamingAssetsPath + D_FILENAME));
        treeData = data.data;
        //Diff received network data to user's base default data
        foreach (var d in networkData.data)
        {
            treeData[d.treeID] = d;
        }

        InitialiazeRuntimeData();
        LoadForestChunkData();
        InitializePool();

        dataLoaded = true;
        Debug.Log("[Forest Manager] Client data successfully loaded!");
    }
    IEnumerator LoadForestAsync()
    {
        Debug.Log(isServer);
        if (isServer)
        {
            LoadFromDisk();
            CreateDiffForestData();

            InitialiazeRuntimeData();
            LoadForestChunkData();
            InitializePool();

            dataLoaded = true;
            Debug.Log("[Forest Manager] Server/Host data successfully loaded!");
        }
        else
        {
            CMDInitializeDataRequestFromServer(gameObject);
            while (!receiverInitialized)
            {
                Debug.Log("herfe: " + receiverDelta);
                bool retried = false;
                if (!retried && receiverDelta >= 5)
                {
                    //Retry after 5 seconds once just in case a rare temp connection lost
                    retried = true;
                    CMDInitializeDataRequestFromServer(gameObject);
                }
                if (receiverDelta >= 10) //Current receiver request timeout set to 10 seconds, should take less than 1, otherwise connection too slow
                {
                    Debug.LogError("Forest network data initialization request timedout!");
                    OnRequestTimedOut();
                    yield break;
                }
                yield return new WaitForSeconds(1);
                receiverDelta++;
            }

            yield return new WaitForEndOfFrame();
            Debug.Log("[Forest Manager] Begin receiving data, expected " + expectedPacketCount + " packets");
            while (timeOutDelta < timeOut)
            {
                if (receivedPacketCount == expectedPacketCount)
                {
                    Debug.Log("[Forest Manager] Sucessfully received all " + receivedPacketCount + " packets in " + timeOutDelta + " seconds");
                    ClientLoadData();
                    yield break;
                }

                if (packetDelta >= packetResync)
                {
                    CMDRequestByteData(networkPacketPtr);
                    packetDelta = 0;
                }

                dataReceivingProgress = Mathf.RoundToInt(((float)receivedPacketCount / expectedPacketCount) * 100);
                //Debug.Log("Received " + receivedPacketCount + "/" + expectedPacketCount + " packets. Progress: " + ((float)receivedPacketCount / expectedPacketCount) * 100 + "%");
                yield return new WaitForSeconds(1);
                timeOutDelta++;
                packetDelta++;
            }

            Debug.Log("Forest network receiving packet timed out, received " + receivedPacketCount + "/" + expectedPacketCount + " packets. Ptr is at " + networkPacketPtr + "\nProgress: " + dataReceivingProgress + "%");
            OnRequestTimedOut();
        }


    }

    [Command(requiresAuthority = false)]
    public void CMDRequestByteData(int index, NetworkConnectionToClient sender = null)
    {
        RPCReceiveByteData(sender, serverSplitedData[index], index);
    }

    [TargetRpc]
    public void RPCReceiveByteData(NetworkConnection conn, byte[] data, int packetIndex)
    {
        if (clientByteArrayCache[packetIndex] == null || clientByteArrayCache[packetIndex].Length == 0)
        {
            clientByteArrayCache[packetIndex] = data;
            receivedPacketCount++;

            if (networkPacketPtr + 1 < expectedPacketCount)
            {
                packetDelta = 0;
                CMDRequestByteData(++networkPacketPtr);
            }
        }
        else
        {
            Debug.LogWarning("[Forest Manager] Duplicate RPC byte data received!");
        }

    }

    [Command(requiresAuthority = false)]
    public void CMDInitializeDataRequestFromServer(GameObject target, NetworkConnectionToClient sender = null)
    {
        Debug.Log("Server received initialization request");
        //RPCInitializeReceiver(sender, 0);
        StartCoroutine(AsyncInitializeReceiver(sender));
    }

    IEnumerator AsyncInitializeReceiver(NetworkConnectionToClient target)
    {
        while (!diffComputed)
        {
            yield return null;
        }

        int segCount = Mathf.CeilToInt((float)diffedForestData.Length / maxPacketSize);

        if (segCount == 0)
        {
            RPCInitializeReceiver(target, 0);
            yield break;
        }

        serverSplitedData = SplitByteData(diffedForestData, segCount);
        Debug.Log("Server sending rpc response to initialization request");
        RPCInitializeReceiver(target, segCount);

    }

    [Server]

    [TargetRpc]
    public void RPCInitializeReceiver(NetworkConnection conn, int expectedCount)
    {
        Debug.Log("[Forest Manager][Client] Initializing receiver, expCount: " + expectedCount);
        if (!receiverInitialized)
        {
            expectedPacketCount = expectedCount;
            //if(expectedCount > 100)
            //{
            //    timeOut = 120;
            //    Debug.Log("[Forest Manager] Large packet size, increasing timeout to 120 seconds");
            //}
            clientByteArrayCache = new List<byte[]>();
            clientByteArrayCache.AddRange(Enumerable.Repeat(default(byte[]), expectedCount));
            receiverInitialized = true;
        }
        else
        {
            Debug.LogWarning("Duplicate Forest RPC Receiver Initialization");
        }
    }
    void CreateDiffForestData() //Diff save against default data in streaming assets to compute a list of changed data
    {
        var ceras = new CerasSerializer();
        ForestData defaultData = ceras.Deserialize<ForestData>(File.ReadAllBytes(Application.streamingAssetsPath + D_FILENAME));

        ForestNetworkData networkData = new ForestNetworkData();
        networkData.data = new List<ForestTreeData>();
        for (int i = 0; i < treeData.Length; i++)
        {
            //TEST!! Uncomment below, this is testing worst case performance where every tree is modified and thus added to network queue
            if (treeData[i] == defaultData.data[i]) continue;
            networkData.data.Add(treeData[i]);
        }

        if (networkData.data.Count > 0)
        {
            diffedForestData = Compress(ceras.Serialize<ForestNetworkData>(networkData));
        }

        diffComputed = true;
        Debug.Log("Diff completed with entries count: " + networkData.data.Count + "\nCompressed byte[] data size is: " + diffedForestData.Length + " bytes");
    }

    public void InitializePool()
    {
        memberParent = new GameObject("====RUN TIME Member Parent====").transform;
        for (int i = 0; i < poolInitializationBudget; i++)
        {
            ForestPoolMember member = Instantiate(treeMemberPrefab, memberParent).GetComponent<ForestPoolMember>();
            pool.Add(member);
            member.DeAllocateMember();
        }
        Debug.Log("Pool initialized with count: " + pool.Count);
    }
    public void InitialiazeRuntimeData()
    {
        foreach (var p in prefabs)
        {
            ForestRuntimeData data = new ForestRuntimeData();
            CapsuleCollider collider = p.GetComponent<CapsuleCollider>();
            data.center = collider.center;
            data.height = collider.height;
            data.radius = collider.radius;
            runtimeData.Add(p.name, data);
        }
        foreach (var tData in treeData)
        {
            if (tData.health == 0) continue; //[TODO] future may need implement tree trunk generation for dead trees.
            ForestRuntimeData rdata = runtimeData[tData.treeName];
            rdata.treesData.Add(tData);
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
            Matrix4x4[] matrix = new Matrix4x4[d.treesData.Count];
            d.matrixData = new List<Matrix4x4>(d.treesData.Count);
            for (int i = 0; i < matrix.Length; i++)
            {
                matrix[i] = Matrix4x4FromString(d.treesData[i].matrixData);
                d.matrixData.Add(matrix[i]);
            }
            GPUInstancerAPI.InitializeWithMatrix4x4Array(GPUIManager, p.prefabPrototype, matrix);
        }
    }

    #region Load Handling / Disk
    public void LoadForestChunkData()
    {
        var ceras = new CerasSerializer();
        chunks = ceras.Deserialize<ForestChunkData>(File.ReadAllBytes(Application.streamingAssetsPath + CHUNK_FILENAME)).chunks;
    }
    public void LoadFromDisk()
    {
        Debug.Log("Loading forest data");
        if (File.Exists(Application.persistentDataPath + FILENAME))
        {
            try
            {
                var ceras = new CerasSerializer();
                byte[] byteData = File.ReadAllBytes(Application.persistentDataPath + FILENAME);
                Debug.Log("Forest byte[] size is " + byteData.Length);
                data = ceras.Deserialize<ForestData>(byteData);
                treeData = data.data;

                Debug.Log("Forest data loaded");
            }
            catch (Exception e)
            {
                Debug.LogError("GlobalBuild: Error loading forest data, save file may be corrupted or not found.\nException: " + e.Message);
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
        Debug.Log("Created default forest data override");
        LoadFromDisk();
    }

    public void SaveData()
    {
        try
        {
            var ceras = new CerasSerializer();
            var bytes = ceras.Serialize(data);
            File.WriteAllBytes(Application.persistentDataPath + FILENAME, bytes);
            Debug.Log("Saved Forest Data");
        }
        catch (Exception e)
        {
            Debug.LogError("Error Saving Forest Data\n" + e.Message);
        }

    }

    #endregion

    #region Helpers

    public static byte[] Compress(byte[] buffer)
    {
        MemoryStream ms = new MemoryStream();
        GZipStream zip = new GZipStream(ms, CompressionMode.Compress, true);
        zip.Write(buffer, 0, buffer.Length);
        zip.Close();
        ms.Position = 0;

        MemoryStream outStream = new MemoryStream();

        byte[] compressed = new byte[ms.Length];
        ms.Read(compressed, 0, compressed.Length);

        byte[] gzBuffer = new byte[compressed.Length + 4];
        Buffer.BlockCopy(compressed, 0, gzBuffer, 4, compressed.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(buffer.Length), 0, gzBuffer, 0, 4);
        return gzBuffer;
    }

    public static byte[] Decompress(byte[] gzBuffer)
    {
        MemoryStream ms = new MemoryStream();
        int msgLength = BitConverter.ToInt32(gzBuffer, 0);
        ms.Write(gzBuffer, 4, gzBuffer.Length - 4);

        byte[] buffer = new byte[msgLength];

        ms.Position = 0;
        GZipStream zip = new GZipStream(ms, CompressionMode.Decompress);
        zip.Read(buffer, 0, buffer.Length);

        return buffer;
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
    public bool IsInList(ForestChunk chunk, List<ForestChunk> list)
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
    //Returns Forest Chunk by position
    public ForestChunk GetChunkByPosition(Vector3 position)
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

    public Vector3 ToVector3(VectorThree vector)
    {
        return new Vector3(vector.x, vector.y, vector.z);
    }
    #endregion
}



#region Runtime Data
public class ForestRuntimeData
{
    public int curPointer = 0;
    //Holds the list of trees that need to be rendered with matrix4x4 calculation.
    public List<ForestTreeData> treesData = new List<ForestTreeData>();
    public List<Matrix4x4> matrixData;
    //Transform collier data
    public float radius;
    public float height;
    public Vector3 center;
}


[Serializable]
public class ForestChunk
{
    [NonSerialized]
    public bool isAllocated;

    //Runtime
    [NonSerialized]
    public List<ForestPoolMember> allocatedTrees;
    public static bool operator ==(ForestChunk lhs, ForestChunk rhs)
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

    public static bool operator !=(ForestChunk lhs, ForestChunk rhs) => !(lhs == rhs);

    [SerializeField]
    public int chunkID;

    [SerializeReference]
    public List<int> forestTreeIDs = new List<int>();

    //[SerializeReference]
    //public List<int> activateChunks = new List<int>(); //Stores chunk IDs for activation pattern
}

[Serializable]
public class ForestChunkData
{
    [SerializeReference]
    public ForestChunk[,] chunks;
}
#endregion
