using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

[Serializable]
public class BaseNObjectData //The main data that will get serialized/de-serialized
{
    [SerializeField]
    public int versionNumber = 1;

    [SerializeReference]
    public BaseNObjectMemberData[] data;
}

[Serializable]
public class BaseNObjectMemberData //Kevin: All trees in the map (fake/real) will be treated as individual members exising in the database
{
    [NonSerialized]
    public int index;

    [NonSerialized]
    public bool calculateMatrix;

    #region Basic Object Data
    [SerializeField]
    public int memberID; //index within the membersDataArray. The array size should be constant as it keeps storing members with 0 HP
    [SerializeField]
    public string objectName; //Points to real tree prefab
    [SerializeField]
    public string matrixData;
    #endregion

    #region Core Component Data
    [SerializeField]
    public float HP;

    #endregion
}
