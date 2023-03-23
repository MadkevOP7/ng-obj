using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;
using Ceras;
using GPUInstancer;
using System;
using Pathfinding;
using static RootMotion.FinalIK.AimPoser;
using UnityEditor.VersionControl;

//Kevin: Generates Serialized Data for Trees, Chunk Data for spatial hashmap, and other tools
[CustomEditor(typeof(BaseNObjectManager))]
public class BaseNObjectManagerEditor : Editor
{
    public static BaseNObjectManager editor;

    public static void ValidateReference()
    {
        editor = BaseNObjectManager.Instance;
        if (!editor)
        {
            editor = GameObject.Find("NObjectManager").GetComponent<BaseNObjectManager>();
        }
    }
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();

        editor = (BaseNObjectManager)target;

        DisplayDataInformation();

        GUILayout.Space(12);
        GUI.backgroundColor = Color.blue;
        if (GUILayout.Button("Generate Default NObject Data"))
        {
            if (EditorUtility.DisplayDialog("Confirm Generate Default NObject Data", "This operation will overwrite existing NObjectData and will introduce an upgrade operation on players data loading.\nWarning: If new type of data is added (ie. Tree), must Rebuild All Nobject References.", "Continue"))
            {
                CreateDefaultForestData();
            }
        }

        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("Re-Generate NObject Chunks Data"))
        {
            if (EditorUtility.DisplayDialog("Confirm Re-Generate NObject Chunk Data", "This operation will re-generate spatial grid & chunk data created from the Generate Default NObject Data operation.", "Continue"))
            {
                GenerateNObjectChunkData();
            }
        }

        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("Validate NObject Chunks Data"))
        {
            ValidateChunkData();
        }

        //if (GUILayout.Button("Debug NObject Data"))
        //{
        //    DebugNObjectData();
        //}

        //if (GUILayout.Button("Debug Max Chunks Index"))
        //{
        //    GetMaxChunkIndex();
        //}

        GUILayout.Space(12);
        GUI.backgroundColor = Color.grey;
        if (GUILayout.Button("Scan & Remove Empty Mesh Filler"))
        {
            int c = 0;
            foreach (var g in editor.goRefList)
            {
                foreach (var m in g.GetComponentsInChildren<MeshFilter>())
                {
                    //Revert all prefabs
                    //Check sharedmesh for null 
                    if (m.sharedMesh == null)
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
            if (EditorUtility.DisplayDialog("Confirm Rebuild All NObject References", "Warning: This operation will clear all current NObjectReferences including GPUI registered prototypes list, and re-scan project workspace for gathering prefab references and re-create GPUI Prototypes.\n\nThis operation is only neccessary during first generation of NObjectData per scene, or if a new type of NObjectData (ie. new tree variant) is added.", "Continue"))
            {
                RebuildAllNObjectReferences();
            }
        }
        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("Rebuild goRefList Array"))
        {
            RebuildNObjectReferenceArray();
        }

        if (GUILayout.Button("Rebuild Prefabs (GPUInstancerPrefab) Array"))
        {
            RebuildIPrefabsArray();
        }
        GUILayout.Space(6);

        GUI.backgroundColor = Color.blue;
        if (GUILayout.Button("Regenerate GPUI Prototype Billboards"))
        {
            if (EditorUtility.DisplayDialog("Confirm Regenerate GPUI Prototype Billboards", "This operation will clear current generate GPUI Prototype billboards and regenerate them. Billboard settings will be overriden to current generation settings defined in the function.", "Continue"))
            {
                GenerateBillboardForAllGPUIPrototypes();
            }
        }
        GUILayout.Space(6);
        GUI.backgroundColor = Color.red;
        if (GUILayout.Button("Clear GPUI Prototype List"))
        {
            if (EditorUtility.DisplayDialog("Confirm Clear GPUI Prototype List", "Prototypes will need to be re-defined to GPUIPrefabManager manually or through 'Rebuild All' or 'Rebuild GPUI Prototype List", "Continue"))
            {
                ClearGPUIPrefabPrototypeList();
            }
        }
    }

    //Kevin: Use to visually display data info, call from either start refresh or manual
    void DisplayDataInformation()
    {
        GUILayout.Space(12);
        string info = "<size=18> <b>BaseNObject Data Display</b> </size>\n\n";
        var editorData = Load();
        //var playerData = LoadPlayerData();
        int editorVersion = 0, playerVersion = 0;
        info += "<b>Editor NObject Data</b>\n";
        info += editorData == null ? "<color=red>Unable to load Editor NObject data, please regenerate!</color>" : GetDebugDataInfo(editorData, out editorVersion);

        info += "\n\n";

        //info += "<b>Player NObject Data</b>\n";
        //info += playerData == null ? "<color=yellow>Unable to load Player NObject data, enter Playmode to auto generate!</color>" : GetDebugDataInfo(editorData, out playerVersion);

        //info += "\n\n";

        if (editorVersion == playerVersion)
        {
            info += "<color=green>Version Up to Date</color>";
        }
        else
        {
            info += "<color=yellow>Version Mismatch: Enter Playmode to automatically upgrade player data to latest editor data</color>";
        }

        info += "\n\n";
        info += GetRunErrorCheckResultString();
        GUILayout.BeginHorizontal(EditorStyles.helpBox);
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.richText = true;
        style.wordWrap = true;
        GUILayout.Label(info, style);
        GUILayout.EndHorizontal();


    }

    #region Error Checks
    string CheckForReferenceSetup()
    {
        string s = "<b>Setup Errors: </b>";
        bool hasProblem = false;
        if (editor.GPUIManager == null)
        {
            hasProblem = true;
            s += "<color=red>Missing GPUIManager reference\n</color>";
        }
        if (editor.HRItemDataBase == null)
        {
            hasProblem = true;
            s += "<color=red>Missing HRItemDB reference\n</color>";
        }
        if (editor.SearchRoot == null)
        {
            hasProblem = true;
            s += "<color=red>Missing Search Root reference\n</color>";
        }

        if (hasProblem) return s;
        return "Setup Check: <b> <color=green>Passed!</color> </b>";
    }

    bool IsInPrototypeList(GameObject g)
    {
        foreach (var p in editor.GPUIManager.prototypeList)
        {
            if (g == p.prefabObject) return true;
        }
        return false;
    }
    string CheckGPUIPrototypes()
    {
        string s = "<b>GPUI Errors: </b>";
        bool hasProblem = false;
        if (editor.GPUIManager == null)
        {
            hasProblem = true;
            s += "<color=red>Missing GPUIManager reference\n</color>";
        }
        else
        {
            foreach (var p in editor.goRefList)
            {
                if (!IsInPrototypeList(p))
                {
                    hasProblem = true;
                    s += $"<color=red>Prefab {p.gameObject.name} is missing prototype in GPUI, please regenerate GPUI prototypes\n</color>";
                }
            }
        }


        if (hasProblem) return s;
        return "GPUI Check: <b> <color=green>Passed!</color> </b>";
    }

    string CheckArrays()
    {
        string s = "<b>Array Reference Errors: </b>";
        bool hasProblem = false;
        for (int i = 0; i < editor.prefabs.Length; i++)
        {
            if (editor.prefabs[i] == null)
            {
                hasProblem = true;
                s += $"<color=red>Prefab Array index {i} is null! Please rebuild Prefab array references\n</color>";
            }
        }

        for (int i = 0; i < editor.goRefList.Length; i++)
        {
            if (editor.goRefList[i] == null)
            {
                hasProblem = true;
                s += $"<color=red>GoRefList Array index {i} is null! Please rebuild GoRefList references\n</color>";
            }
        }

        if (!hasProblem)
        {
            //Check for same index
            if (editor.prefabs.Length != editor.goRefList.Length)
            {
                hasProblem = true;
                s += $"<color=red>Array length mismatch between Prefabs array and GoRefList array\n</color>";
            }
            else
            {
                for (int i = 0; i < editor.prefabs.Length; i++)
                {
                    if (editor.goRefList[i].gameObject != editor.goRefList[i])
                    {
                        hasProblem = true;
                        s += $"<color=red>Index {i} has mismatch between Prefabs array and GoRefList array. Please rebuild references!\n</color>";
                    }
                }
            }
        }
        if (hasProblem) return s;
        return "Array References Check: <b> <color=green>Passed!</color> </b>";
    }

    //todo
    string CheckShader()
    {
        string s = "<b>Shader Errors: </b>";
        bool hasProblem = false;
        
        if (hasProblem) return s;
        return "Shader Check: <b> <color=green> Passed!</color> </b>";
    }
    #endregion
    string GetRunErrorCheckResultString()
    {
        string s = "";
        s += CheckForReferenceSetup();
        s += CheckArrays();
        s += CheckGPUIPrototypes();
        return s;
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
        foreach (var g in editor.goRefList)
        {
            var c = g.GetComponent<GPUInstancerPrefab>();
            if (c == null)
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
        foreach (GameObject g in editor.goRefList)
        {
            GPUInstancerPrefabManagerEditor.AddPickerObject(editor.GPUIManager, g, null);
        }
        Debug.Log("Successfully Rebuilt GPUI Prototype List");
        RebuildIPrefabsArray();
        GenerateBillboardForAllGPUIPrototypes();
    }

    void GenerateBillboardForAllGPUIPrototypes()
    {
        try
        {
            if (editor == null)
            {
                Debug.Log("Missing reference to BaseNObjectManager, GameObject changed name?");
            }
            foreach (var prefab in editor.prefabs)
            {
                GPUInstancerPrototype prototype = prefab.prefabPrototype;
                prototype.billboard.atlasResolution = 1024;
                prototype.billboard.frameCount = 8;
                prototype.billboard.billboardBrightness = 0.5f;
                prototype.billboard.billboardDistance = 0.45f;
                prototype.billboard.normalStrength = 1;
                prototype.billboard.replaceLODCullWithBillboard = false;
                GPUInstancerUtility.GeneratePrototypeBillboard(prototype, true);
            }
        }
        catch
        {
            Debug.LogError("Failed to generate GPUI Prototype billboard");
            return;
        }

        Debug.Log("Successfully regenerated GPUI Prototype billboard for " + editor.prefabs.Length + " prototypes");
    }

    void RebuildNObjectReferenceArray()
    {
        //if (!editor) return;

        Dictionary<string, int> t = new Dictionary<string, int>();
        BaseNObjectMemberData[] memberDataArray;
        if (editor.DefaultForestData)
        {
            try
            {
                var ceras = new CerasSerializer();
                memberDataArray = ceras.Deserialize<BaseNObjectData>(editor.DefaultForestData.bytes).data;
            }
            catch (Exception e)
            {
                Debug.LogError("NObjectTool: Failed to load data.\nException: " + e.Message);
                return;
            }
        }
        else
        {
            Debug.LogError("NObjectTool: No Default Forest Data Present in NObjectManager");
            return;
        }

        //Keep for building dictionary t
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
        foreach (var s in guids)
        {
            paths.Add(AssetDatabase.GUIDToAssetPath(s));
        }
        if (paths.Count > 1)
        {
            //AssetDatabase.FIndAssets can only find containing names without the ability to find an asset with a SPECIFIC name. As such we ignore names that contain 'fake'
            //Remove fake
            RemoveNonMatchingNamesFromPathList(name, paths);
            if (paths.Count > 1)
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
        catch (Exception e)
        {
            Debug.LogError("Failed to Load Path: " + path + "\n" + e.StackTrace);
            return false;
        }
        gList.Add(prefab);
        return true;
    }


    #region NObjectDataGeneration
    void DebugNObjectData()
    {
        BaseNObjectData data = Load();
        Debug.Log("Data Count: " + data.data.Length);
        List<string> names = new List<string>();
        foreach (var t in data.data)
        {
            if (!names.Contains(t.objectName))
            {
                names.Add(t.objectName);
                Debug.Log("Unique Name: " + t.objectName);
            }
        }
    }

    string GetDebugDataInfo(BaseNObjectData data, out int versionNum)
    {
        string s = "";
        s += "Recorded Members: " + data.data.Length;
        Dictionary<string, int> d = new Dictionary<string, int>();
        foreach (var t in data.data)
        {
            if (!d.ContainsKey(t.objectName))
            {
                d.Add(t.objectName, 1);
                continue;
            }
            d[t.objectName]++;
        }

        int index = 1;
        foreach (KeyValuePair<string, int> v in d)
        {
            s += $"\n[{index}] {v.Key}: {v.Value}";
            index++;
        }

        versionNum = data.versionNumber;
        return s;
    }

    //So grid size can be optimized to the smallest working value (reducing 2D array size)
    void GetMaxChunkIndex()
    {
        if (!editor) return;
        var cd = LoadChunkData();
        Debug.Log("Current 2D array size: " + editor.gridSize / editor.chunkSize);
        int max = -1;
        for (int i = 0; i < cd.chunks.Length; i++)
        {
            for (int j = 0; j < cd.chunks.Length; j++)
            {
                if (cd.chunks[i, j] != null) continue;
                if (i > max)
                {
                    max = i;
                }
                if (j > max)
                {
                    max = j;
                }
            }
        }
        Debug.Log("Max index used is " + max);
    }
    static void ValidateChunkData()
    {
        if (!editor) return;
        BaseNObjectData forest = Load();
        var cd = LoadChunkData();
        if (forest == null || cd == null)
        {
            Debug.LogError("Chunk Validation Failed: Unable to load data");
            return;
        }
        for (int i = 0; i < forest.data.Length; i++)
        {
            EditorUtility.DisplayProgressBar("Validating Chunk Data", "Validate: " + (i + 1).ToString() + "/" + forest.data.Length.ToString(), (i + 1) / (float)forest.data.Length);
            Vector3 position = Matrix4x4FromString(forest.data[i].matrixData).GetColumn(3);
            Vector2Int cell = GetCellIndex(position, editor.gridSize, editor.chunkSize);
            if (cd.chunks[cell.x, cell.y] == null)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError("Chunk Validation Failed at: " + (i + 1).ToString() + "/" + forest.data.Length.ToString() + "\n Object " + forest.data[i].objectName + " is not in any chunks. Try increasing Grid Size and Re-generate chunks");
                return;
            }
        }
        EditorUtility.ClearProgressBar();
        Debug.Log("Chunk Validation Complete: Passed!");
    }
    public static void GenerateNObjectChunkData(AssetList ChangeList = null)
    {
        if (!editor) return;

        BaseNObjectData forest = Load();
        int index = 0;
        NObjectChunkData chunkData = new NObjectChunkData();
        int c = editor.gridSize / editor.chunkSize;
        chunkData.chunks = new NObjectChunk[c, c];

        for (int i = 0; i < forest.data.Length; i++)
        {
            EditorUtility.DisplayProgressBar("Generating Chunk Data", "Processing: " + (i + 1).ToString() + "/" + forest.data.Length.ToString(), (i + 1) / (float)forest.data.Length);
            Vector3 position = Matrix4x4FromString(forest.data[i].matrixData).GetColumn(3);
            Vector2Int cell = GetCellIndex(position, editor.gridSize, editor.chunkSize);
            if (chunkData.chunks[cell.x, cell.y] == null)
            {
                NObjectChunk data = new NObjectChunk();
                data.chunkID = index;
                index++;
                chunkData.chunks[cell.x, cell.y] = data;
            }
            chunkData.chunks[cell.x, cell.y].nObjectIDs.Add(forest.data[i].memberID);
        }

        SaveChunkData(chunkData, ChangeList);
        EditorUtility.ClearProgressBar();
        Debug.Log("Generated Chunk Data: " + (index + 1).ToString() + " chunks");
        ValidateChunkData();
    }

    public NObjectChunk GetChunkByPosition(Vector3 position, NObjectChunkData chunkData)
    {
        if (!editor) return null;
        Vector2Int CellIndex = GetCellIndex(position, editor.gridSize, editor.chunkSize);
        Debug.Log("Success Get Chunk for: " + CellIndex.x + ", " + CellIndex.y);
        return chunkData.chunks[CellIndex.x, CellIndex.y];
    }

    public static Vector2Int GetCellIndex(Vector3 pos, int gridSize, int chunkSize)
    {
        return new Vector2Int(
            Mathf.FloorToInt((gridSize / chunkSize) - Mathf.Abs(pos.x - gridSize / 2) / (float)chunkSize),
            Mathf.FloorToInt((gridSize / chunkSize) - Mathf.Abs(pos.z - gridSize / 2) / (float)chunkSize)
            );
    }

    public static BaseNObjectData Load()
    {
        if (!editor || !editor.DefaultForestData) return null;

        var ceras = new CerasSerializer();
        return ceras.Deserialize<BaseNObjectData>(editor.DefaultForestData.bytes);
    }

    public static NObjectChunkData LoadChunkData()
    {
        if (!editor || editor.ForestChunkData == null) return null;

        var ceras = new CerasSerializer();
        return ceras.Deserialize<NObjectChunkData>(editor.ForestChunkData.bytes);
    }

    static bool CheckInclusion(string name)
    {
        if (editor.filterMode == BaseNObjectManager.FilterMode.off) return true;
        switch (editor.filterMode)
        {
            case BaseNObjectManager.FilterMode.inclusive:
                foreach (var n in editor.includeScanNames)
                {
                    if (name.Contains(n))
                    {
                        return true;
                    }
                }
                return false;

            case BaseNObjectManager.FilterMode.exclusive:
                foreach (var n in editor.excludeScanNames)
                {
                    if (name.Contains(n))
                    {
                        return false;
                    }
                }
                return true;
        }

        return false;
    }
    public static void CreateDefaultForestData(AssetList ChangeList = null)
    {
        if (!editor || !editor.SearchRoot) return;

        HRItemDatabase dataBase = editor.HRItemDataBase;
        //Get all trees
        List<GameObject> members = new List<GameObject>();
        foreach (var a in editor.SearchRoot.GetComponentsInChildren<BaseReplace>())
        {
            if ((a.gameObject.name.ToLower().Contains("pf") || a.gameObject.name.ToLower().Contains("fo")) && CheckInclusion(a.gameObject.name) && CheckInclusion(a.ReplacePrefab.name))
            {
                members.Add(a.gameObject);
                Debug.Log("Added Replace Prefab: " + a.ReplacePrefab.name);
            }
        }

        foreach (var a in editor.SearchRoot.GetComponentsInChildren<BaseWeapon>())
        {
            if (a.gameObject.name.ToLower().Contains("pf") && CheckInclusion(a.gameObject.name))
            {
                members.Add(a.gameObject);
                Debug.Log("Added Prefab: " + a.gameObject.name);
            }
        }
        //Kevin: remove objects without "tree" in name
        for (int i = members.Count - 1; i >= 0; --i)
        {
            if (!members[i].name.ToLower().Contains("tree"))
            {
                members.RemoveAt(i);
            }
        }
        if (members.Count < 10)
        {
            Debug.LogWarning("Auto aborted as count is low, clicked on accident?");
            EditorUtility.ClearProgressBar();
            return;
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


        SaveDefaultData(nData, ChangeList);
        EditorUtility.ClearProgressBar();
        Debug.Log("Successfully processed");
        //Quick Verify
        var n = Load();
        Debug.Log("Verify Count: " + n.data.Length);
        GenerateNObjectChunkData();
    }

    static void SaveChunkData(NObjectChunkData data, AssetList ChangeList)
    {
        if (!editor) return;

        editor.ForestChunkData = SaveData(data, editor.CHUNK_FILENAME(true), editor.CHUNK_FILENAME(false), ChangeList);

        Debug.Log("saved chunk data");
    }

    static void SaveDefaultData(BaseNObjectData data, AssetList ChangeList)
    {
        if (!editor) return;

        editor.DefaultForestData = SaveData(data, editor.EDITOR_FILENAME(true), editor.EDITOR_FILENAME(false), ChangeList);

        Debug.Log("Created default data override");
        editor.defaultDataVersionNum++; //Every time a new default data is created, increament this to trigger diff function on player end
        Load();
    }

    static TextAsset SaveData<T>(T data, string SystemFilePath, string UnityFilePath, AssetList ChangeList)
    {
        var ceras = new CerasSerializer();
        var bytes = ceras.Serialize(data);

        Asset DataSaveFile = Provider.GetAssetByPath(UnityFilePath);
        if (DataSaveFile != null)
        {
            Task CheckoutSaveFile = Provider.Checkout(DataSaveFile, CheckoutMode.Asset);
            CheckoutSaveFile.Wait();
        }

        File.WriteAllBytes(SystemFilePath, bytes);

        AssetDatabase.Refresh();
        AssetDatabase.ImportAsset(UnityFilePath);


        if (ChangeList != null)
        {
            ChangeList.Add(DataSaveFile);
        }

        return AssetDatabase.LoadAssetAtPath<TextAsset>(UnityFilePath);
    }

    public static Matrix4x4 Matrix4x4FromString(string matrixStr)
    {
        Matrix4x4 matrix4x4 = new Matrix4x4();
        string[] floatStrArray = matrixStr.Split(';');
        for (int i = 0; i < floatStrArray.Length; i++)
        {
            matrix4x4[i / 4, i % 4] = float.Parse(floatStrArray[i], System.Globalization.CultureInfo.InvariantCulture);
        }
        return matrix4x4;
    }
    #endregion
}

//public class BaseNObjectHelper : EditorWindow
//{

//    //static BaseNObjectManager editor; //Set this from singleton, if fails null gets by GameObject.Find

//    //[MenuItem("Airstrafe Tools/NObject Workflow/NObject Helper")]
//    //static void Init()
//    //{
//    //    BaseNObjectHelper window = (BaseNObjectHelper)GetWindow(typeof(BaseNObjectHelper));
//    //    ValidateReference();
//    //}

//    //public static void ValidateReference()
//    //{
//    //    editor = BaseNObjectManager.Instance;
//    //    if (!editor)
//    //    {
//    //        editor = GameObject.Find("NObjectManager").GetComponent<BaseNObjectManager>();
//    //    }
//    //}

//    //void OnGUI()
//    //{
//    //    if (Selection.count == 0)
//    //    {

//    //        if (GUILayout.Button("Re-Generate NObject Chunks Data"))
//    //        {
//    //            GenerateNObjectChunkData(); //Regenerates chunks data (redo spatial hashmap, but doesn't generate member data
//    //        }

//    //        if (GUILayout.Button("Validate NObject Chunks Data"))
//    //        {
//    //            ValidateChunkData(); 
//    //        }

//    //        if (GUILayout.Button("Debug NObject Data"))
//    //        {
//    //            DebugNObjectData();
//    //        }

//    //        if (GUILayout.Button("Debug Max Chunks Index"))
//    //        {
//    //            GetMaxChunkIndex();
//    //        }

//    //    }
//    //    else
//    //    {
//    //        if (GUILayout.Button("Create World Data and Write to Streaming Assets as Default Data"))
//    //        {
//    //            CreateDefaultForestData();
//    //        }
//    //    }

//    //}


//}