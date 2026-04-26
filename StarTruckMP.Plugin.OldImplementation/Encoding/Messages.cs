using StarTruckMP.Client;
using StarTruckMP.Utilities;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace StarTruckMP.Encoding
{
    internal class Messages
    {
        public static playerInfo createPlayer(int playerId, Vector3 position, Vector3 rotation, string sector)
        {
            GameObject sectorGo = GameObject.Find("[Sector]");
            var myTruck = StarTruckClient.MyTruck;
            var myPlayer = StarTruckClient.MyPlayer;
            if (myTruck == null || myPlayer == null || StarTruckClient.SpaceSuitObj == null || StarTruckClient.SpaceSuitMats == null)
                return new playerInfo();

            var myRigid = myTruck.GetComponent<Rigidbody>();

            //Spawn new Truck GameObject
            GameObject newTruck = new GameObject("RemoteTruck" + playerId);
            SceneManager.MoveGameObjectToScene(newTruck, sectorGo.scene);
            newTruck.transform.SetParent(null);
            var newRigid = newTruck.AddComponent<Rigidbody>();
            newRigid.useGravity = myRigid.useGravity;
            newRigid.drag = myRigid.drag;
            newRigid.angularDrag = myRigid.angularDrag;
            newRigid.mass = myRigid.mass;
            newRigid.centerOfMass = myRigid.centerOfMass;
            newRigid.detectCollisions = false;
            newRigid.isKinematic = myRigid.isKinematic;
            newRigid.maxAngularVelocity = myRigid.maxAngularVelocity;
            newRigid.maxDepenetrationVelocity = myRigid.maxDepenetrationVelocity;
            newRigid.inertiaTensor = myRigid.inertiaTensor;
            newRigid.inertiaTensorRotation = myRigid.inertiaTensorRotation;

            GameObject exteriorObj = GameObject.Find("Exterior");
            GameObject newExterior = GameObject.Instantiate(exteriorObj, Vector3.zero, Quaternion.EulerAngles(Vector3.zero), newTruck.transform);
            newExterior.name = "ClientExterior" + playerId;

            newExterior.transform.Find("StarTruck_Hatch").Find("Marker").gameObject.SetActive(false);
            Object.Destroy(newExterior.transform.Find("StarTruck_Hatch").GetChild(0).GetComponent<DoorAnimator>());
            Object.Destroy(newExterior.transform.Find("StarTruck_Hatch").GetChild(0).GetComponent<GameEventListener>());
            Object.Destroy(newExterior.transform.Find("StarTruck_Hatch").GetChild(0).GetComponent<EPOOutline.TargetStateListener>());
            newExterior.transform.Find("MonitorCameras").gameObject.SetActive(false);
            newExterior.transform.Find("PlayerSpawnMarker").gameObject.SetActive(false);
            newExterior.transform.Find("ThrusterCameraShakeController").gameObject.SetActive(false);
            var customization = newExterior.transform.GetComponent<CustomizationApplier>();
            var livDamApp = newExterior.transform.GetComponent<LiveryAndDamageApplierTruckExterior>();
            customization.m_linkedLiveryApplier = livDamApp;

            //Disable Truck Collision
            foreach (var item in newExterior.GetComponentsInChildren<Collider>())
            {
                item.enabled = false;
            }

            //Spawn new Player GameObject
            GameObject newPlayer = new GameObject("RemotePlayer" + playerId);
            SceneManager.MoveGameObjectToScene(newPlayer, sectorGo.scene);
            newPlayer.transform.SetParent(null);

            GameObject newSuit = GameObject.Instantiate(StarTruckClient.SpaceSuitObj, Vector3.zero, Quaternion.EulerAngles(Vector3.zero), newPlayer.transform);
            newSuit.GetComponent<MeshRenderer>().materials = StarTruckClient.SpaceSuitMats;
            newSuit.active = true;
            newSuit.name = "ClientSuit" + playerId;
            Object.Destroy(newSuit.transform.GetComponent<SpaceSuitController>());
            Object.Destroy(newSuit.transform.GetComponent<UnityEngine.CapsuleCollider>());
            Object.Destroy(newSuit.transform.GetComponent<OutlinableSetterUpper>());
            Object.Destroy(newSuit.transform.GetComponent<EPOOutline.Outlinable>());
            Object.Destroy(newSuit.transform.GetComponent<EPOOutline.TargetStateListener>());
            Object.Destroy(newSuit.transform.GetComponent<MaterialSwitcher>());
            Object.Destroy(newSuit.transform.GetComponent<InteractTarget>());
            Object.Destroy(newSuit.transform.GetComponent<DoorController>());
            myRigid = myPlayer.GetComponent<Rigidbody>();

            newRigid = newPlayer.AddComponent<Rigidbody>();
            newRigid.useGravity = myRigid.useGravity;
            newRigid.drag = myRigid.drag;
            newRigid.angularDrag = myRigid.angularDrag;
            newRigid.mass = myRigid.mass;
            newRigid.centerOfMass = myRigid.centerOfMass;
            newRigid.detectCollisions = false;
            newRigid.isKinematic = myRigid.isKinematic;
            newRigid.maxAngularVelocity = myRigid.maxAngularVelocity;
            newRigid.maxDepenetrationVelocity = myRigid.maxDepenetrationVelocity;
            newRigid.inertiaTensor = myRigid.inertiaTensor;
            newRigid.inertiaTensorRotation = myRigid.inertiaTensorRotation;

            playerInfo currentPlayer = new playerInfo();
            currentPlayer.Player = newPlayer;
            currentPlayer.Truck = newTruck;
            currentPlayer.sector = sector;
            currentPlayer.truckTrans.Pos = position;
            currentPlayer.truckTrans.Rot = rotation;

            return currentPlayer;
        }

        public static void updateMovement(GameObject? playerObject, Vector3 position, Vector3 rotation, Vector3 velocity, Vector3 angVel)
        {
            var floatingOrigin = StarTruckClient.FloatingOrigin;
            if (floatingOrigin == null)
                return;

            if (playerObject != null)
            {
                playerObject.transform.position = position - floatingOrigin.m_currentOrigin;
                playerObject.transform.eulerAngles = rotation;
                var rigidbody = playerObject.GetComponent<Rigidbody>();
                if (rigidbody == null)
                    return;

                rigidbody.velocity = velocity;
                rigidbody.angularVelocity = angVel;
            }
        }
    }
}
