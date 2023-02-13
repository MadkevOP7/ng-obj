using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;
using Ceras;
using GPUInstancer;
using System;
//Kevin: Generates Serialized Data for Trees, Chunk Data for spatial hashmap, and other tools
[CustomEditor(typeof(BaseNObjectManager))]
public class BaseNObjectManagerEditor : Editor
{
    BaseNObjectManager editor;
    static string FILENAME = "/Default_NObject_Data.sav";

    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();

        editor = (BaseNObjectManager)target;

        if (GUILayout.Button("Scan & Remove Empty Mesh Filler"))
        {
            int c = 0;
            foreach(var g in editor.goRefList)
            {
                foreach(var m in g.GetComponentsInChildren<MeshFilter>())
                {
                    //Revert all prefabs
                    //Check sharedmesh for null 
                    if(m.sharedMesh == null)
                    {
                        DestroyImmediate(m, true);
                        c++;
                    }
                }
            }
            Debug.Log("Scan Complete, Removed " + c + " Null Mesh Fillers");
        }
        GUILayout.Space(6);
        GUI.backgroundColor = Color.yellow;
        if (GUILayout.Button("Rebuild All NObject References"))
        {
            RebuildAllNObjectReferences();
        }
        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("Rebuild goRefList Array"))
        {
            RebuildNObjectReferenceArray();
        }

        if(GUILayout.Button("Rebuild Prefabs (GPUInstancerPrefab) Array"))
        {
            RebuildIPrefabsArray();
        }
        GUILayout.Space(6);
        GUILayout.Label("Prototypes will need to be re-defined to GPUIPrefabManager manually or through 'Rebuild All' or 'Rebuild GPUI Prototype List");
        GUI.backgroundColor = Color.red;
        if (GUILayout.Button("Clear GPUI Prototype List"))
        {
            ClearGPUIPrefabPrototypeList();
        }
    }
    
    void RebuildAllNObjectReferences()
    {
        RebuildNObjectReferenceArray();
        RebuildGPUInstancerPrototypesList();
    }

    void ClearGPUIPrefabPrototypeList()
    {
        editor.GPUIManager.prototypeList.Clear();

        foreach (var a in editor.goRefList)
        {
            if (a.GetComponent<GPUInstancerPrefab>() != null)
            {
                DestroyImmediate(a.GetComponent<GPUInstancerPrefab>(), true);
            }
        }
    }

    void RebuildIPrefabsArray()
    {
        List<GPUInstancerPrefab> l = new List<GPUInstancerPrefab>();
        foreach(var g in editor.goRefList)
        {
            var c = g.GetComponent<GPUInstancerPrefab>();
            if(c == null)
            {
                Debug.LogError("Rebuild IPrefabs Array failed: GameObject " + g.name + " is missing GPUInstancerPrefab component, please Rebuild GPUI Prototype List or Rebuild All NObject Reference");
                return;
            }
            l.Add(c);
            editor.prefabs = l.ToArray();
        }
    }
    void RebuildGPUInstancerPrototypesList()
    {
        ClearGPUIPrefabPrototypeList();
        foreach(GameObject g in editor.goRefList)
        {
            GPUInstancerPrefabManagerEditor.AddPickerObject(editor.GPUIManager, g, null);
        }
        Debug.Log("Successfully Rebuilt GPUI Prototype List");
        RebuildIPrefabsArray();
    }
    void RebuildNObjectReferenceArray()
    {
        Dictionary<string, int> t = new Dictionary<string, int>();
        BaseNObjectMemberData[] memberDataArray;
        if (File.Exists(Application.streamingAssetsPath + FILENAME))
        {
            try
            {
                var ceras = new CerasSerializer();
                memberDataArray = ceras.Deserialize<BaseNObjectData>(File.ReadAllBytes(Application.streamingAssetsPath + FILENAME)).data;
            }
            catch (Exception e)
            {
                Debug.LogError("NObjectTool: Failed to load data.\nException: " + e.Message);
                return;
            }
        }
        else
        {
            Debug.LogError("NObjectTool: Data not found.");
            return;
        }

        Debug.Log("Loaded memberDataArray Length: " + memberDataArray.Length + "\n" + "Building Prefab Dictionary for unique prefab array");
        foreach (var a in memberDataArray)
        {
            int d;
            if (!t.TryGetValue(a.objectName, out d))
            {
                t.Add(a.objectName, 1);
                continue;
            }
            t[a.objectName] += 1;
        }

        Debug.Log("Unique Prefab Data Scan Complete: ");
        List<string> searchList = new List<string>();
        foreach (KeyValuePair<string, int> entry in t)
        {
            Debug.Log("Name: " + entry.Key + " Count: " + entry.Value);
            searchList.Add(entry.Key);
        }

        Debug.Log("Starting Workspace Prefab Scan");
        List<GameObject> iGOList = new List<GameObject>();
        for (int i = 0; i < searchList.Count; i++)
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                EditorUtility.ClearProgressBar();
                Debug.Log("Canceled Scan Operation");
                return;
            }
            EditorUtility.DisplayProgressBar("Building Prefab Array: " + searchList[i], "Scanning Workspace: " + (i + 1).ToString() + "/" + (searchList.Count).ToString(), (i + 1) / (float)(searchList.Count));
            if (!SearchAndLoadAsset(searchList[i], iGOList))
            {
                EditorUtility.ClearProgressBar();
                Debug.Log("Failed Operation");
                return;
            }
        }
        editor.goRefList = iGOList.ToArray();
        Debug.Log("goRefList Array Build Complete");
        EditorUtility.ClearProgressBar();

    }

    void RemoveFakeNamesFromList(List<string> list)
    {
        for (int i = list.Count - 1; i >= 0; --i)
        {
            if (list[i].ToLower().Contains("fake"))
            {
                list.RemoveAt(i);
            }
        }
    }

    void RemoveNonMatchingNamesFromPathList(string name, List<string> list)
    {
        for (int i = list.Count - 1; i >= 0; --i)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(list[i]).name != name)
            {
                list.RemoveAt(i);
            }
        }
    }
    bool SearchAndLoadAsset(string name, List<GameObject> gList) //Returns true if found unique
    { 
        string SearchParam = name + " t:Prefab";
        Debug.Log("Starting Search: " + SearchParam);
        string[] guids = AssetDatabase.FindAssets(SearchParam, new[] { "Assets/Prefabs" });
        List<string> paths = new List<string>();
        foreach(var s in guids)
        {
            paths.Add(AssetDatabase.GUIDToAssetPath(s));
        }
        if (paths.Count > 1)
        {
            //AssetDatabase.FIndAssets can only find containing names without the ability to find an asset with a SPECIFIC name. As such we ignore names that contain 'fake'
            //Remove fake
            RemoveNonMatchingNamesFromPathList(name, paths);
            if(paths.Count > 1)
            {
                Debug.LogError("Search Param Duplicate Found: " + SearchParam);
                for (int i = 0; i < paths.Count; i++)
                {
                    Debug.Log("Duplicate " + i + " Path: " + paths[i]);
                }
                return false;
            }
        }
        else if (paths.Count < 1)
        {
            Debug.LogError("Search Param Not Found: " + SearchParam);
            return false;

        }

        string path = paths[0];
        Debug.Log("Starting Load Path: " + path);
        GameObject prefab;
        try
        {
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        catch(Exception e)
        {
            Debug.LogError("Failed to Load Path: " + path + "\n" + e.StackTrace);
            return false;
        }
        gList.Add(prefab);
        return true;
    }
}

public class BaseNObjectHelper : EditorWindow
{
    public HRItemDatabase dataBase;

    static string FILENAME = "Default_NObject_Data.sav";
    static string CHUNK_FILENAME = "SM_NObject_Data.sav"; //This data is static and stays inside StreamingAssets
    static string path = "Assets/StreamingAssets/";

    static int chunkSize = 100;
    static int gridSize = 100000; //Kevin: GridSize can be changed based on the total unitsXunits size of the open-world terrain. Assuminf 100k x 100k terrain
    [MenuItem("Airstrafe Tools/NObject Workflow/NObject Helper")]
    static void Init()
    {
        BaseNObjectHelper window = (BaseNObjectHelper)GetWindow(typeof(BaseNObjectHelper));
    }
    void OnGUI()
    {
        if (Selection.count == 0)
        {

            if (GUILayout.Button("Re-Generate NObject Chunks Data"))
            {
                GenerateNObjectChunkData(); //Regenerates chunks data (redo spatial hashmap, but doesn't generate member data
            }

            if (GUILayout.Button("Validate NObject Chunks Data"))
            {
                ValidateChunkData(); 
            }

            if (GUILayout.Button("Debug NObject Data"))
            {
                DebugNObjectData();
            }

            if (GUILayout.Button("Debug Max Chunks Index"))
            {
                GetMaxChunkIndex();
            }

        }
        else
        {
            if (GUILayout.Button("Create World Data and Write to Streaming Assets as Default Data"))
            {
                CreateDefaultForestData();
            }
        }

    }
    
    void DebugNObjectData()
    {
        BaseNObjectData data = Load();
        Debug.Log("Data Count: " + data.data.Length);
        List<string> names = new List<string>();
        foreach(var t in data.data)
        {
            if (!names.Contains(t.objectName))
            {
                names.Add(t.objectName);
                Debug.Log("Unique Name: " + t.objectName);
            }
        }
    }

    //So grid size can be optimized to the smallest working value (reducing 2D array size)
    void GetMaxChunkIndex()
    {
        var cd = LoadChunkData();
        Debug.Log("Current 2D array size: " + gridSize / chunkSize);
        int max = -1;
        for(int i=0; i<cd.chunks.Length; i++)
        {
            for(int j=0; j<cd.chunks.Length; j++)
            {
                if (cd.chunks[i, j] != null) continue;
                if(i > max)
                {
                    max = i;
                }
                if(j > max)
                {
                    max = j;
                }
            }
        }
        Debug.Log("Max index used is " + max);
    }
    void ValidateChunkData()
    {
        BaseNObjectData forest = Load();
        var cd = LoadChunkData();
        if(forest==null || cd == null)
        {
            Debug.LogError("Chunk Validation Failed: Unable to load data");
            return;
        }
        for (int i = 0; i < forest.data.Length; i++)
        {
            EditorUtility.DisplayProgressBar("Validating Chunk Data", "Validate: " + (i + 1).ToString() + "/" + forest.data.Length.ToString(), (i + 1) / (float)forest.data.Length);
            Vector3 position = Matrix4x4FromString(forest.data[i].matrixData).GetColumn(3);
            int cellX = GetXCellIndex(position.x);
            int cellZ = GetZCellIndex(position.z);
            if (cd.chunks[cellX, cellZ] == null)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError("Chunk Validation Failed at: " + (i + 1).ToString() + "/" + forest.data.Length.ToString() + "\n Object " + forest.data[i].objectName + " is not in any chunks. Try increasing Grid Size and Re-generate chunks");
                return;
            }
        }
        EditorUtility.ClearProgressBar();
        Debug.Log("Chunk Validation Complete: Passed!");
    }
    public void GenerateNObjectChunkData()
    {
        //Singleton not setup in editor
        dataBase = GameObject.Find("GPUIPrefabManager").GetComponent<BaseNObjectManager>().HRItemDataBase;
        BaseNObjectData forest = Load();
        int index = 0;
        NObjectChunkData chunkData = new NObjectChunkData();
        int c = gridSize / chunkSize;
        chunkData.chunks = new NObjectChunk[c, c];

        for (int i = 0; i < forest.data.Length; i++)
        {
            EditorUtility.DisplayProgressBar("Generating Chunk Data", "Processing: " + (i + 1).ToString() + "/" + forest.data.Length.ToString(), (i + 1) / (float)forest.data.Length);
            Vector3 position = Matrix4x4FromString(forest.data[i].matrixData).GetColumn(3);
            int cellX = GetXCellIndex(position.x);
            int cellZ = GetZCellIndex(position.z);

            if (chunkData.chunks[cellX, cellZ] == null)
            {
                NObjectChunk data = new NObjectChunk();
                data.chunkID = index;
                index++;
                chunkData.chunks[cellX, cellZ] = data;
            }
            chunkData.chunks[cellX, cellZ].nObjectIDs.Add(forest.data[i].memberID);
        }

        SaveChunkData(chunkData);
        EditorUtility.ClearProgressBar();
        Debug.Log("Generated Chunk Data: " + (index+1).ToString() + " chunks");
        ValidateChunkData();
    }

    public NObjectChunk GetChunkByPosition(Vector3 position, NObjectChunkData chunkData)
    {
        int xCell = GetXCellIndex(position.x);
        int zCell = GetZCellIndex(position.z);
        Debug.Log("Sucess Get Chunk for: " + xCell + ", " + zCell);
        return chunkData.chunks[xCell, zCell];
    }

    public int GetXCellIndex(float x)
    {
        return Mathf.FloorToInt((gridSize / chunkSize) - Mathf.Abs(x - gridSize / 2) / (float)chunkSize);
    }

    //Returns the index of cell in the [,] grid given a z position worldpoint
    public int GetZCellIndex(float z)
    {
        return Mathf.FloorToInt((gridSize / chunkSize) - Mathf.Abs(z - gridSize / 2) / (float)chunkSize);
    }

    public BaseNObjectData Load()
    {
        var ceras = new CerasSerializer();
        return ceras.Deserialize<BaseNObjectData>(File.ReadAllBytes(path + FILENAME));
    }
    public NObjectChunkData LoadChunkData()
    {
        var ceras = new CerasSerializer();
        return ceras.Deserialize<NObjectChunkData>(File.ReadAllBytes(path + CHUNK_FILENAME));
    }

    bool CheckInclusion(string name)
    {
        string[] names = { "PF_Tree_CherryBlossom_Tree", "PF_Tree_Bamboo_Thick_Single_Canopy", "PF_Tree_Bamboo_Thick_Grouped_Canopy", "PF_Tree_Oak", "PF_Tree_Tier1_Pine", "PF_Tree_Tier1_Palm"};
        foreach(var n in names)
        {
            if(name.Contains(n))
            {
                return true;
            }
        }
        return false;
    }
    void CreateDefaultForestData()
    {
        dataBase = GameObject.Find("GPUIPrefabManager").GetComponent<BaseNObjectManager>().HRItemDataBase;

        //Get all trees
        List<GameObject> members = new List<GameObject>();
        foreach (var a in Selection.activeGameObject.GetComponentsInChildren<BaseReplace>())
        {
            if (a.gameObject.name.ToLower().Contains("pf")&&CheckInclusion(a.gameObject.name) && CheckInclusion(a.ReplacePrefab.name))
            {
                members.Add(a.ReplacePrefab);
                Debug.Log("Added Replace Prefab: " + a.ReplacePrefab.name);
            }
        }

        foreach (var a in Selection.activeGameObject.GetComponentsInChildren<BaseWeapon>())
        {
            if (a.gameObject.name.ToLower().Contains("pf")&&CheckInclusion(a.gameObject.name))
            {
                members.Add(a.gameObject);
                Debug.Log("Added Prefab: " + a.gameObject.name);
            }
        }
        //Kevin: remove objects without "tree" in name
        for(int i = members.Count - 1; i >=0; --i)
        {
            if (!members[i].name.ToLower().Contains("tree"))
            {
                members.RemoveAt(i);
            }
        }
        if(members.Count < 10)
        {
            Debug.LogWarning("Auto aborted as count is low, clicked on accident?");
        }
        List<BaseNObjectMemberData> baseMembersData = new List<BaseNObjectMemberData>();
        BaseNObjectData nData = new BaseNObjectData();
        Debug.Log("Begin: " + members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Converting to NObject Data", "Processing: " + (i + 1).ToString() + "/" + members.Count.ToString(), (float)i / members.Count);
                Transform t = members[i].transform;
                BaseNObjectMemberData data = new BaseNObjectMemberData();
                data.objectName = members[i].GetComponent<BaseReplace>() != null ? members[i].GetComponent<BaseReplace>().ReplacePrefab.name : dataBase.ItemArray[members[i].GetComponent<BaseWeapon>().ItemID].ItemPrefab.name;
                Debug.Log("Final Name: " + data.objectName);
                //Matrix 4x4 data
                data.matrixData = GPUInstancerUtility.Matrix4x4ToString(t.localToWorldMatrix);

                data.HP = 100; //Kevin: Create default of 100
                baseMembersData.Add(data);
            }
            catch
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError("Failed");
                return;
            }

        }

        nData.data = baseMembersData.ToArray();

        //Assign IDs
        for (int i = 0; i < nData.data.Length; i++)
        {
            nData.data[i].memberID = i;
        }


        SaveData(nData);
        EditorUtility.ClearProgressBar();
        Debug.Log("Sucessfully processed");
        //Quick Verify
        var n = Load();
        Debug.Log("Verify Count: " + n.data.Length);
        CreateDefaultSaveOverride(nData);
    }
    public static void SaveData(BaseNObjectData data)
    {
        var ceras = new CerasSerializer();
        var bytes = ceras.Serialize(data);
        File.WriteAllBytes(path + FILENAME, bytes);
        Debug.Log("saved forest data");
    }

    public static void SaveChunkData(NObjectChunkData data)
    {
        var ceras = new CerasSerializer();
        var bytes = ceras.Serialize(data);
        File.WriteAllBytes(path + CHUNK_FILENAME, bytes);
        Debug.Log("saved chunk data");
    }
    void CreateDefaultSaveOverride(BaseNObjectData data)
    {
        var ceras = new CerasSerializer();
        var bytes = ceras.Serialize(data);
        string R_FILENAME = "/NObjectData.sav";
        File.WriteAllBytes(Application.persistentDataPath + R_FILENAME, bytes);
        Debug.Log("Created default data override");
        Load();
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
}