using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ModAPI.Attributes;
using TheForest.Items;
using TheForest.Items.World;
using TheForest.Utils;
using UnityEngine;

namespace LiveMap
{

    class LiveMap : MonoBehaviour
    {

        [ExecuteOnGameStart]
        private static void AddMeToScene()
        {
            new GameObject("__LiveMap__").AddComponent<LiveMap>();
        }

        const int TYPE_PLAYER = 0;
        const int TYPE_ENEMY = 1;
        const int TYPE_CAVE_ENTRANCE = 2;
        const int TYPE_PICKUP = 3;

        const int ACTION_TYPE_CLEAR = 0;

        const float UPDATE_INTERVAL_SECONDS = 1f;

        // how often the scene is re-scanned for enemies when running as a multiplayer client
        // (clients don't get the host's cannibal lists, so we have to look for enemy components ourselves)
        const float CLIENT_ENEMY_RESCAN_INTERVAL_SECONDS = 5f;

        // a remote player this far below the terrain surface is assumed to be in the caves
        const float CAVE_DEPTH_THRESHOLD = 8f;

        // names of the AI components attached to networked enemies on multiplayer clients
        // (resolved via reflection so the mod still compiles/runs if a game update renames them)
        private static readonly string[] CLIENT_ENEMY_TYPE_NAMES = { "mutantAI_net", "mutantAI" };

        const string DEFAULT_HOST = "127.0.0.1";
        const int DEFAULT_PORT = 9999;
        const string CONFIG_FILE_NAME = "LiveMap.cfg";

        private string host = DEFAULT_HOST;
        private int port = DEFAULT_PORT;

        private float lastUpdate = 0.0f;
        private float lastClientEnemyScan = float.MaxValue;

        private TcpClient client;

        private Queue<System.Object> queue = new Queue<System.Object>();
        private readonly object queueLock = new object();
        private bool isThreadStarted = false;

        private List<Type> clientEnemyTypes = null;
        private List<Component> clientEnemies = new List<Component>();

        void Awake()
        {
            ModAPI.Log.Write("live map started (multiplayer build)");
            LoadConfig();
            if (!isThreadStarted)
            {
                var th = new Thread(TcpClientLoop);
                th.IsBackground = true;
                th.Start();
                isThreadStarted = true;
                ModAPI.Log.Write("Started TCP client thread");
            }
        }

        void Update()
        {
            if (ModAPI.Input.GetButtonDown("Update"))
            {
                FindStaticObjects();
            }

            lastUpdate += Time.deltaTime;
            if (lastUpdate > UPDATE_INTERVAL_SECONDS)
            {
                lastUpdate = 0.0f;

                try
                {
                    FindPlayers();
                }
                catch (Exception e)
                {
                    ModAPI.Log.Write("Error finding players: " + e);
                }

                try
                {
                    FindCannibals();
                }
                catch (Exception e)
                {
                    ModAPI.Log.Write("Error finding enemies: " + e);
                }
            }
        }

        // ---------------------------------------------------------------- config

        // Reads <game folder>/LiveMap.cfg with "host=..." and "port=..." lines.
        // Falls back to 127.0.0.1:9999 when the file is missing, so a friend can
        // point their mod at the map viewer running on another machine.
        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), CONFIG_FILE_NAME);
                if (!File.Exists(path))
                {
                    ModAPI.Log.Write("No " + path + ", using default viewer address " + host + ":" + port);
                    return;
                }

                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq < 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    if (key == "host" && value.Length > 0)
                    {
                        host = value;
                    }
                    else if (key == "port")
                    {
                        int p;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p > 0 && p < 65536)
                        {
                            port = p;
                        }
                    }
                }
                ModAPI.Log.Write("Viewer address from " + path + ": " + host + ":" + port);
            }
            catch (Exception e)
            {
                ModAPI.Log.Write("Error reading config, using defaults: " + e);
            }
        }

        // ---------------------------------------------------------------- players

        // Sends every player in the session (local and remote). In single player
        // the list contains just the local player.
        private void FindPlayers()
        {
            GameObject localGo = LocalPlayer.GameObject;
            Transform localRoot = localGo != null ? localGo.transform.root : null;
            bool sentLocal = false;
            int index = 0;

            if (Scene.SceneTracker != null && Scene.SceneTracker.allPlayers != null)
            {
                foreach (GameObject p in Scene.SceneTracker.allPlayers)
                {
                    index++;
                    if (p == null)
                    {
                        continue;
                    }

                    bool isLocal = localGo != null && (p == localGo || (localRoot != null && p.transform.root == localRoot));
                    if (isLocal && sentLocal)
                    {
                        continue; // the local player can show up more than once in the list
                    }

                    Vector3 position = p.transform.position;
                    Vector3 rotation = p.transform.rotation.eulerAngles;
                    bool inCave;
                    if (isLocal)
                    {
                        if (LocalPlayer.Transform != null)
                        {
                            position = LocalPlayer.Transform.position;
                            rotation = LocalPlayer.Transform.rotation.eulerAngles;
                        }
                        inCave = LocalPlayer.IsInCaves;
                        sentLocal = true;
                    }
                    else
                    {
                        inCave = IsBelowTerrain(position);
                    }

                    string name = GetPlayerName(p, isLocal, index);
                    AddObjectToQueue(new MapEntry(TYPE_PLAYER, p.GetInstanceID(), -1, name, position, rotation, inCave, isLocal));
                }
            }

            if (!sentLocal && LocalPlayer.Transform != null && localGo != null)
            {
                AddObjectToQueue(new MapEntry(TYPE_PLAYER, localGo.GetInstanceID(), -1, GetPlayerName(localGo, true, 0),
                    LocalPlayer.Transform.position, LocalPlayer.Transform.rotation.eulerAngles, LocalPlayer.IsInCaves, true));
            }
        }

        private string GetPlayerName(GameObject player, bool isLocal, int index)
        {
            try
            {
                if (BoltNetwork.isRunning)
                {
                    BoltEntity entity = player.GetComponentInChildren<BoltEntity>();
                    if (entity == null)
                    {
                        entity = player.GetComponentInParent<BoltEntity>();
                    }
                    if (entity != null && entity.isAttached)
                    {
                        IPlayerState state = entity.GetState<IPlayerState>();
                        if (state != null && !string.IsNullOrEmpty(state.name))
                        {
                            return state.name;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // fall through to a generic name
            }
            return isLocal ? "You" : "Player " + index;
        }

        // Rough test for "is this position inside a cave": caves are carved
        // below the terrain surface, so anything well below it counts.
        private bool IsBelowTerrain(Vector3 position)
        {
            try
            {
                Terrain terrain = Terrain.activeTerrain;
                if (terrain == null)
                {
                    return false;
                }
                float surface = terrain.SampleHeight(position) + terrain.transform.position.y;
                return position.y < surface - CAVE_DEPTH_THRESHOLD;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool IsMultiplayer()
        {
            try
            {
                return BoltNetwork.isRunning;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool IsMultiplayerClient()
        {
            try
            {
                return BoltNetwork.isRunning && BoltNetwork.isClient;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- enemies

        private void FindCannibals()
        {
            HashSet<int> cannibalIDs = new HashSet<int>();

            // host / single player: the game keeps lists of the active cannibals.
            // Both lists are always sent because in co-op the host can be on the surface
            // while another player is in a cave; the viewer filters by the map layer it shows.
            if (Scene.MutantControler != null)
            {
                if (Scene.MutantControler.ActiveCaveCannibals != null)
                {
                    foreach (var c in Scene.MutantControler.ActiveCaveCannibals)
                    {
                        AddEnemy(c, true, cannibalIDs);
                    }
                }

                if (Scene.MutantControler.ActiveWorldCannibals != null)
                {
                    foreach (var c in Scene.MutantControler.ActiveWorldCannibals)
                    {
                        AddEnemy(c, false, cannibalIDs);
                    }
                }

                if (Scene.MutantControler.activeInstantSpawnedCannibals != null)
                {
                    bool multiplayer = IsMultiplayer();
                    foreach (GameObject c in Scene.MutantControler.activeInstantSpawnedCannibals)
                    {
                        if (c != null)
                        {
                            AddEnemy(c, multiplayer ? IsBelowTerrain(c.transform.position) : LocalPlayer.IsInCaves, cannibalIDs);
                        }
                    }
                }
            }

            // multiplayer client: the lists above live on the host, so look for the
            // networked enemy proxies in the scene instead
            if (IsMultiplayerClient())
            {
                FindClientCannibals(cannibalIDs);
            }
        }

        private void AddEnemy(GameObject c, bool inCave, HashSet<int> cannibalIDs)
        {
            if (c == null || !c.activeInHierarchy)
            {
                return;
            }
            if (cannibalIDs.Add(c.GetInstanceID()))
            {
                AddObjectToQueue(new MapEntry(TYPE_ENEMY, c, inCave));
            }
        }

        private void FindClientCannibals(HashSet<int> cannibalIDs)
        {
            if (clientEnemyTypes == null)
            {
                ResolveClientEnemyTypes();
            }
            if (clientEnemyTypes.Count == 0)
            {
                return;
            }

            lastClientEnemyScan += UPDATE_INTERVAL_SECONDS;
            if (lastClientEnemyScan > CLIENT_ENEMY_RESCAN_INTERVAL_SECONDS)
            {
                lastClientEnemyScan = 0f;
                clientEnemies.Clear();
                foreach (Type t in clientEnemyTypes)
                {
                    UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(t);
                    if (found == null)
                    {
                        continue;
                    }
                    foreach (UnityEngine.Object o in found)
                    {
                        Component comp = o as Component;
                        if (comp != null)
                        {
                            clientEnemies.Add(comp);
                        }
                    }
                }
            }

            foreach (Component comp in clientEnemies)
            {
                if (comp == null) // destroyed since the last scan
                {
                    continue;
                }
                GameObject go = comp.gameObject;
                AddEnemy(go, IsBelowTerrain(go.transform.position), cannibalIDs);
            }
        }

        private void ResolveClientEnemyTypes()
        {
            clientEnemyTypes = new List<Type>();
            foreach (string typeName in CLIENT_ENEMY_TYPE_NAMES)
            {
                Type t = FindType(typeName);
                if (t != null)
                {
                    clientEnemyTypes.Add(t);
                    ModAPI.Log.Write("Client enemy type: " + t.FullName);
                }
                else
                {
                    ModAPI.Log.Write("Client enemy type not found: " + typeName);
                }
            }
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(name);
                    if (t != null)
                    {
                        return t;
                    }
                }
                catch (Exception)
                {
                    // some dynamic assemblies refuse type lookups
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- static objects

        private void FindStaticObjects()
        {
            List<MapEntry> entries = new List<MapEntry>();
            FindItems(entries, "PickUp", TYPE_PICKUP);
            //FindSpawnPools();
            FindCaveEntrances(entries);
            // ModAPI.Log.Write("Sending static entries: " + entries.Count);
            SendStaticObjects(entries);
        }

        private void FindCaveEntrances(List<MapEntry> entries)
        {
            if (Scene.SceneTracker == null)
            {
                return;
            }

            foreach (var ce in Scene.SceneTracker.caveEntrances) {
                GameObject go = ce.blackBackingGo;
                if (go != null)
                {
                    entries.Add(new MapEntry(TYPE_CAVE_ENTRANCE, go));
                    continue;
                }
                go = ce.fadeToDarkGo;
                if (go != null)
                {
                    entries.Add(new MapEntry(TYPE_CAVE_ENTRANCE, go));
                    continue;
                }
                go = ce.blackBackingFadeGo;
                if (go != null)
                {
                    entries.Add(new MapEntry(TYPE_CAVE_ENTRANCE, go));
                    continue;
                }
            }
        }

        private void FindItems(List<MapEntry> entries, string itemName, int entryType)
        {
            GameObject[] objectsOfType;

            objectsOfType = UnityEngine.Object.FindObjectsOfType<GameObject>();
            ModAPI.Log.Write("Object.FindObjectsOfType: " + objectsOfType.Length);
            HandleItems(entries, entryType, objectsOfType);

            objectsOfType = Resources.FindObjectsOfTypeAll(typeof(GameObject)) as GameObject[];
            ModAPI.Log.Write("Resources.FindObjectsOfTypeAll: " + objectsOfType.Length);
            HandleItems(entries, entryType, objectsOfType);
        }

        private void HandleItems(List<MapEntry> entries, int entryType, GameObject[] objectsOfType)
        {
            if (objectsOfType.NullOrEmpty())
                return;

            foreach (GameObject gameObject in objectsOfType)
            {
                if (ContainsComponent(gameObject, typeof(PickUp)))
                {
                    entries.Add(new MapEntry(entryType, gameObject));
                }
            }
        }

        private void FindSpawnPools()
        {
            MonoBehaviour[] objectsOfType = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)) as MonoBehaviour[];
            if (objectsOfType.NullOrEmpty())
                return;

            CultureInfo culture = CultureInfo.InvariantCulture;
            string itemName = "rope";

            foreach (MonoBehaviour obj in objectsOfType)
            {
                if (!((UnityEngine.Object)obj == (UnityEngine.Object)null) && !obj.name.NullOrEmpty() && culture.CompareInfo.IndexOf(gameObject.name, itemName, CompareOptions.IgnoreCase) >= 0)
                {
                    ModAPI.Log.Write("Found SpawnItemFromPool: " + obj.name + ", " + obj.GetInstanceID() + ", " + obj.transform.position + ", " + obj);
                    DumpGameObject(obj.transform.gameObject);
                }
            }
        }

        private bool ContainsComponent(GameObject go, Type type)
        {
            if ((UnityEngine.Object)go == (UnityEngine.Object)null)
            {
                return false;
            }
            if (go.name.NullOrEmpty())
            {
                return false;
            }

            Component component = go.GetComponentInChildren(type);
            return component != null;
        }

        private void DumpGameObject(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            ModAPI.Log.Write("Game object: " + go.GetInstanceID() + ", " + go.name + " [" + go.tag + "]:" + go.ToString());
            foreach (Component c in go.GetComponentsInChildren<Component>())
            {
                ModAPI.Log.Write("\tComponent: " + c);
                if (c is PickUp)
                {
                    PickUp pickUp = c as PickUp;
                    ModAPI.Log.Write("\tPickUp Item ID: " + pickUp._itemId);
                }
            }
        }

        private void SendStaticObjects(List<MapEntry> entries)
        {
            try
            {
                ModAPI.Log.Write("SendStaticObjects: " + entries.Count);

                int nrPickup = 0;
                int nrCave = 0;

                AddObjectToQueue(new Action(ACTION_TYPE_CLEAR));

                foreach (MapEntry e in entries)
                {
                    AddObjectToQueue(e);

                    if (e.type == TYPE_PICKUP)
                    {
                        nrPickup++;
                    }
                    if (e.type == TYPE_CAVE_ENTRANCE)
                    {
                        nrCave++;
                    }
                }
                ModAPI.Log.Write("Sent: " + nrCave + " caves, " + nrPickup + " pickups");
            }
            catch (Exception e)
            {
                ModAPI.Log.Write("Error: " + e);
            }
        }

        // ---------------------------------------------------------------- networking

        private void AddObjectToQueue(System.Object obj)
        {
            lock (queueLock)
            {
                queue.Enqueue(obj);
            }
        }

        private void TcpClientLoop()
        {
            while (true)
            {
                System.Object obj = null;
                lock (queueLock)
                {
                    if (queue.Count > 0)
                    {
                        obj = queue.Dequeue();
                    }
                }

                if (obj == null)
                {
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    SendAsJson(obj);
                }
                catch (Exception e)
                {
                    // viewer not running or unreachable: drop what we have queued so far
                    // (it would be stale by the time we reconnect) and try again later
                    ModAPI.Log.Write("Cannot send to viewer at " + host + ":" + port + " (" + e.Message + "), retrying in 5 seconds");
                    CloseClient();
                    lock (queueLock)
                    {
                        queue.Clear();
                    }
                    Thread.Sleep(5000);
                }
            }
        }

        private void SendAsJson(System.Object obj)
        {
            string json = JsonUtility.ToJson(obj);
            json += "\n"; // separate objects with new line
            Byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            TcpClient client = GetTcpClient();
            NetworkStream stream = client.GetStream();
            stream.Write(jsonBytes, 0, jsonBytes.Length);
        }

        private TcpClient GetTcpClient()
        {
            if (client != null && client.Connected)
            {
                return client;
            }
            CloseClient();

            client = new TcpClient(host, port);
            ModAPI.Log.Write("Connected to " + host + ":" + port);
            return client;
        }

        private void CloseClient()
        {
            if (client != null)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
                client = null;
            }
        }

    }

    [Serializable]
    public class MapEntry
    {
        public int type;
        public int id;
        public int itemID;
        public string name;
        public float x;
        public float y;
        public float z;
        public float rotZ;
        public bool inCave;
        public bool isLocal;

        public MapEntry(int type, GameObject gameObject, bool inCave = false) :
            this(type, gameObject.GetInstanceID(), GetItemID(gameObject), gameObject.name, gameObject.transform.position, gameObject.transform.rotation.eulerAngles, inCave, false)
        { }

        public MapEntry(int type, int id, int itemID, string name, Vector3 position, Vector3? rotation = null, bool? inCave = false, bool isLocal = false)
        {
            this.type = type;
            this.id = id;
            this.itemID = itemID;
            this.name = name;
            this.x = position.x;
            this.y = position.z; // y => z
            this.z = position.y; // z => y
            this.rotZ = rotation != null ? rotation.Value.y : 0; // y is z
            this.inCave = inCave != null && inCave.Value;
            this.isLocal = isLocal;
        }

        private static int GetItemID(GameObject gameObject)
        {
            PickUp pickUp = gameObject.GetComponentInChildren(typeof(PickUp)) as PickUp;
            if (pickUp != null)
            {
                return pickUp._itemId;
            }
            return -1;
        }

    }

    [Serializable]
    public class Action
    {
        public int actionType;

        public Action(int actionType)
        {
            this.actionType = actionType;
        }

    }

}
